using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MaridewFinance.App
{
    /// <summary>
    /// Local account management for Maridew Finance. Each person gets their own
    /// account (username + PBKDF2-hashed password); all financial data is
    /// scoped per user via a user_id column on the data tables. Everything is
    /// stored locally — no server, no network.
    /// </summary>
    public class AuthService
    {
        private const int Iterations = 100_000;
        private readonly string _connectionString;

        public AuthService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaridewFinance");
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "maridew.db");
            _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            EnsureSchema();
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        /// <summary>
        /// Creates the users table (if missing) and, for databases created
        /// before accounts existed, adds the user_id column to the data tables.
        /// Pre-existing rows keep user_id NULL until the first account is
        /// created, then are adopted by that account.
        /// </summary>
        public void EnsureSchema()
        {
            using var db = Open();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS users (
                        id INTEGER PRIMARY KEY,
                        username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                        password_hash TEXT NOT NULL,
                        salt TEXT NOT NULL,
                        created_at TEXT NOT NULL)";
                cmd.ExecuteNonQuery();
            }

            foreach (var table in new[] { "transactions", "budgets", "loans", "savings_goals" })
            {
                if (TableExists(db, table) && !ColumnExists(db, table, "user_id"))
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN user_id INTEGER";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static bool TableExists(SqliteConnection db, string table)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t";
            var p = cmd.CreateParameter();
            p.ParameterName = "$t";
            p.Value = table;
            cmd.Parameters.Add(p);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        private static bool ColumnExists(SqliteConnection db, string table, string column)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public int UserCount()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM users";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        /// <summary>Creates a new account. Returns the user id on success.</summary>
        public (bool Ok, int UserId, string Error) CreateAccount(string username, string password)
        {
            var name = (username ?? "").Trim();
            if (name.Length < 3 || name.Length > 32)
            {
                return (false, 0, "Username must be 3–32 characters.");
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\w.\- ]+$"))
            {
                return (false, 0, "Username may only contain letters, numbers, spaces, dots, dashes and underscores.");
            }
            if (string.IsNullOrEmpty(password) || password.Length < 6)
            {
                return (false, 0, "Password must be at least 6 characters.");
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = HashPassword(password, salt);

            using (var db = Open())
            {
                using (var check = db.CreateCommand())
                {
                    check.CommandText = "SELECT COUNT(*) FROM users WHERE username = $u";
                    check.Parameters.AddWithValue("$u", name);
                    if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                    {
                        return (false, 0, "That username is already taken.");
                    }
                }

                try
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = @"INSERT INTO users (username, password_hash, salt, created_at)
                                        VALUES ($u, $h, $s, $c)";
                    cmd.Parameters.AddWithValue("$u", name);
                    cmd.Parameters.AddWithValue("$h", Convert.ToBase64String(hash));
                    cmd.Parameters.AddWithValue("$s", Convert.ToBase64String(salt));
                    cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
                    cmd.ExecuteNonQuery();

                    using var idCmd = db.CreateCommand();
                    idCmd.CommandText = "SELECT last_insert_rowid()";
                    var userId = Convert.ToInt32(idCmd.ExecuteScalar());
                    return (true, userId, "");
                }
                catch (Exception ex)
                {
                    return (false, 0, ex.Message);
                }
            }
        }

        /// <summary>Signs in with username + password. Returns the user id on success.</summary>
        public (bool Ok, int UserId, string Error) SignIn(string username, string password)
        {
            var name = (username ?? "").Trim();
            string? storedHash = null, storedSalt = null;
            int userId = 0;

            using (var db = Open())
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT id, password_hash, salt FROM users WHERE username = $u";
                cmd.Parameters.AddWithValue("$u", name);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    userId = reader.GetInt32(0);
                    storedHash = reader.GetString(1);
                    storedSalt = reader.GetString(2);
                }
            }

            if (storedHash == null || storedSalt == null)
            {
                return (false, 0, "No account found with that username.");
            }

            var expected = Convert.FromBase64String(storedHash);
            var actual = HashPassword(password ?? "", Convert.FromBase64String(storedSalt));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return (false, 0, "Incorrect password.");
            }
            return (true, userId, "");
        }

        /// <summary>
        /// Changes a user's password after verifying the current one. The new
        /// password gets a fresh salt; the old hash is never reused.
        /// </summary>
        public (bool Ok, string Error) ChangePassword(int userId, string currentPassword, string newPassword)
        {
            string? storedHash = null, storedSalt = null;
            using (var db = Open())
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT password_hash, salt FROM users WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", userId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    storedHash = reader.GetString(0);
                    storedSalt = reader.GetString(1);
                }
            }
            if (storedHash == null || storedSalt == null)
            {
                return (false, "Account not found.");
            }

            var expected = Convert.FromBase64String(storedHash);
            var actual = HashPassword(currentPassword ?? "", Convert.FromBase64String(storedSalt));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return (false, "Current password is incorrect.");
            }
            if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
            {
                return (false, "New password must be at least 6 characters.");
            }
            if (currentPassword == newPassword)
            {
                return (false, "Choose a password different from the current one.");
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = HashPassword(newPassword, salt);
            using (var db = Open())
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE users SET password_hash = $h, salt = $s WHERE id = $id";
                cmd.Parameters.AddWithValue("$h", Convert.ToBase64String(hash));
                cmd.Parameters.AddWithValue("$s", Convert.ToBase64String(salt));
                cmd.Parameters.AddWithValue("$id", userId);
                cmd.ExecuteNonQuery();
            }
            return (true, "");
        }

        /// <summary>
        /// Checks a password against the stored hash without signing in.
        /// Used by the lock screen to confirm the account holder is back.
        /// </summary>
        public bool VerifyPassword(int userId, string password)
        {
            string? storedHash = null, storedSalt = null;
            using (var db = Open())
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT password_hash, salt FROM users WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", userId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    storedHash = reader.GetString(0);
                    storedSalt = reader.GetString(1);
                }
            }
            if (storedHash == null || storedSalt == null)
            {
                return false;
            }

            var expected = Convert.FromBase64String(storedHash);
            var actual = HashPassword(password ?? "", Convert.FromBase64String(storedSalt));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        /// <summary>
        /// Adopts rows that predate accounts (user_id IS NULL) into the first
        /// account created on an upgraded database.
        /// </summary>
        public void AssignOrphanData(int userId)
        {
            using var db = Open();
            foreach (var table in new[] { "transactions", "budgets", "loans", "savings_goals" })
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"UPDATE {table} SET user_id = $u WHERE user_id IS NULL";
                cmd.Parameters.AddWithValue("$u", userId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Resolves a username to its user id (false if not found).</summary>
        public bool TryGetUserIdByName(string username, out int userId)
        {
            userId = 0;
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id FROM users WHERE username = $u";
            cmd.Parameters.AddWithValue("$u", username);
            var value = cmd.ExecuteScalar();
            if (value is long id)
            {
                userId = (int)id;
                return true;
            }
            return false;
        }

        /// <summary>Looks up a display name by user id (null if not found).</summary>
        public string? GetUsername(int userId)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT username FROM users WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", userId);
            var value = cmd.ExecuteScalar() as string;
            return value;
        }

        private static byte[] HashPassword(string password, byte[] salt)
        {
            using var derive = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
            return derive.GetBytes(32);
        }
    }
}
