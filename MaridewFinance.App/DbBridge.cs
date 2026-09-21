using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace MaridewFinance.App
{
    /// <summary>
    /// SQLite persistence bridge exposed to the dashboard JavaScript through
    /// WebView2 host objects. Holds the four user-data tables (transactions,
    /// budgets, loans, savings goals) in a database under %LOCALAPPDATA%.
    /// Tables are small, so save rewrites the full table contents — this keeps
    /// the bridge trivial while always mirroring the in-memory state exactly.
    /// Also keeps automatic daily backups (one snapshot per day, newest 7 kept)
    /// in a "backups" folder next to the database. All data is scoped to the
    /// signed-in user (see AuthService); each person sees only their own rows.
    /// </summary>
    public class DbBridge
    {
        private const int BackupRetention = 7;
        private readonly string _connectionString;
        private readonly string _dbPath;
        private int _currentUserId;

        /// <summary>Id of the signed-in user (0 when none) — used by the lock screen.</summary>
        public int CurrentUserId => _currentUserId;

        /// <summary>Sets whose data this bridge serves (called after sign-in).</summary>
        public void SetCurrentUser(int userId)
        {
            _currentUserId = userId;
        }

        // ----------------- PER-USER SETTINGS (key/value) -----------------

        /// <summary>
        /// Creates the user_settings table (key/value preferences scoped to
        /// the signed-in user; currently used by the auto-lock feature).
        /// </summary>
        private void EnsureSettingsTable()
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS user_settings (
                        user_id INTEGER NOT NULL,
                        key TEXT NOT NULL,
                        value TEXT NOT NULL,
                        PRIMARY KEY (user_id, key))";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Settings are optional; never break the app over them.
            }
        }

        /// <summary>
        /// Saves one setting (e.g. "autoLockEnabled" / "autoLockMinutes") for
        /// the signed-in user. Returns JSON: { ok, error? }.
        /// </summary>
        public string SaveSetting(string key, string value)
        {
            if (_currentUserId == 0)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "No user is signed in." });
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "Setting name is required." });
            }
            try
            {
                EnsureSettingsTable();
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT INTO user_settings (user_id, key, value) VALUES ($u, $k, $v)
                                    ON CONFLICT (user_id, key) DO UPDATE SET value = $v";
                cmd.Parameters.AddWithValue("$u", _currentUserId);
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value ?? "");
                cmd.ExecuteNonQuery();
                App.CloudSync?.SchedulePush();          // settings sync to the cloud too
                return JsonSerializer.Serialize(new { ok = true });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Returns a JSON object of all settings for the signed-in user
        /// (empty object when none are stored).
        /// </summary>
        public string LoadSettings()
        {
            var result = new Dictionary<string, string>();
            if (_currentUserId == 0)
            {
                return JsonSerializer.Serialize(result);
            }
            try
            {
                EnsureSettingsTable();
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT key, value FROM user_settings WHERE user_id = $u";
                cmd.Parameters.AddWithValue("$u", _currentUserId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result[reader.GetString(0)] = reader.GetString(1);
                }
            }
            catch
            {
                // Fall through with whatever was loaded.
            }
            return JsonSerializer.Serialize(result);
        }

        /// <summary>Clears the signed-in user; subsequent data calls throw.</summary>
        public void SignOut()
        {
            _currentUserId = 0;
        }

        /// <summary>Display name of the signed-in user (empty if none).</summary>
        public string GetCurrentUserName()
        {
            return _currentUserId == 0 ? "" : App.Auth?.GetUsername(_currentUserId) ?? "";
        }

        /// <summary>
        /// Changes the signed-in user's password after verifying the current
        /// one. Returns JSON: { ok, error? }.
        /// </summary>
        public string ChangePassword(string currentPassword, string newPassword)
        {
            if (_currentUserId == 0)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "No user is signed in." });
            }
            var auth = App.Auth;
            if (auth == null)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "Authentication is unavailable." });
            }
            try
            {
                var (ok, error) = auth.ChangePassword(_currentUserId, currentPassword, newPassword);
                if (ok)
                {
                    // Keep the zero-knowledge cloud link working: re-encrypt the
                    // cloud blob under the new password and update its auth proof.
                    _ = App.CloudSync?.OnPasswordChangedAsync(currentPassword, newPassword);
                }
                return ok
                    ? JsonSerializer.Serialize(new { ok = true })
                    : JsonSerializer.Serialize(new { ok = false, error });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        }

        /// <summary>Called by the dashboard before closing so the login screen shows again.</summary>
        public void SignOutToLogin()
        {
            App.ReloginRequested = true;
        }

        /// <summary>
        /// Called by the dashboard when the idle timeout expires (or the user
        /// clicks "Lock Now"): shows the modal lock screen on the UI thread.
        /// The WebView2 host-object proxy may invoke this from a worker thread,
        /// so the dialog must be launched via the Dispatcher.
        /// </summary>
        public void RequestLock()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (App.LockRequested) return;   // already showing
                App.LockRequested = true;
                try
                {
                    App.LaunchLockWindow();
                }
                finally
                {
                    App.LockRequested = false;
                }
            });
        }
        private readonly string _backupDir;
        private string _lastBackupDate = "";
        private readonly object _backupLock = new();
        private readonly System.Timers.Timer _backupTimer;

        private sealed record TableDef(string[] Columns, string[] Props, string[] Types, string CreateSql, string OrderBy);

        private static readonly Dictionary<string, TableDef> Tables = new()
        {
            // NB: the SQL column is "to_account" because "to" is a SQLite
            // reserved keyword; the JSON property stays "to" for the dashboard.
            ["transactions"] = new(
                new[] { "id", "desc", "type", "cat", "amount", "date", "method", "to_account" },
                new[] { "id", "desc", "type", "cat", "amount", "date", "method", "to" },
                new[] { "INTEGER", "TEXT", "TEXT", "TEXT", "REAL", "TEXT", "TEXT", "TEXT" },
                "CREATE TABLE IF NOT EXISTS transactions (id INTEGER PRIMARY KEY, desc TEXT NOT NULL, type TEXT NOT NULL, cat TEXT NOT NULL, amount REAL NOT NULL, date TEXT NOT NULL, method TEXT NOT NULL, to_account TEXT NOT NULL DEFAULT '')",
                "id DESC"),
            ["budgets"] = new(
                new[] { "id", "name", "limitAmount", "spent", "icon", "color" },
                new[] { "id", "name", "limit", "spent", "icon", "color" },
                new[] { "INTEGER", "TEXT", "REAL", "REAL", "TEXT", "TEXT" },
                "CREATE TABLE IF NOT EXISTS budgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL, limitAmount REAL NOT NULL, spent REAL NOT NULL, icon TEXT NOT NULL, color TEXT NOT NULL)",
                "id ASC"),
            ["loans"] = new(
                new[] { "id", "name", "principal", "original", "rate", "monthly", "due" },
                new[] { "id", "name", "principal", "original", "rate", "monthly", "due" },
                new[] { "INTEGER", "TEXT", "REAL", "REAL", "REAL", "REAL", "TEXT" },
                "CREATE TABLE IF NOT EXISTS loans (id INTEGER PRIMARY KEY, name TEXT NOT NULL, principal REAL NOT NULL, original REAL NOT NULL, rate REAL NOT NULL, monthly REAL NOT NULL, due TEXT NOT NULL)",
                "id ASC"),
            ["savings_goals"] = new(
                new[] { "id", "name", "target", "current", "due", "color" },
                new[] { "id", "name", "target", "current", "due", "color" },
                new[] { "INTEGER", "TEXT", "REAL", "REAL", "TEXT", "TEXT" },
                "CREATE TABLE IF NOT EXISTS savings_goals (id INTEGER PRIMARY KEY, name TEXT NOT NULL, target REAL NOT NULL, current REAL NOT NULL, due TEXT NOT NULL, color TEXT NOT NULL)",
                "id ASC"),
            ["categories"] = new(
                new[] { "id", "name", "type", "color" },
                new[] { "id", "name", "type", "color" },
                new[] { "INTEGER", "TEXT", "TEXT", "TEXT" },
                "CREATE TABLE IF NOT EXISTS categories (id INTEGER PRIMARY KEY, user_id INTEGER, name TEXT NOT NULL, type TEXT NOT NULL, color TEXT NOT NULL DEFAULT '')",
                "id ASC"),
            ["savings_transactions"] = new(
                new[] { "id", "goal_id", "kind", "amount", "date", "note" },
                new[] { "id", "goal_id", "kind", "amount", "date", "note" },
                new[] { "INTEGER", "INTEGER", "TEXT", "REAL", "TEXT", "TEXT" },
                "CREATE TABLE IF NOT EXISTS savings_transactions (id INTEGER PRIMARY KEY, user_id INTEGER, goal_id INTEGER NOT NULL, kind TEXT NOT NULL, amount REAL NOT NULL, date TEXT NOT NULL, note TEXT NOT NULL)",
                "id DESC"),
            ["loan_transactions"] = new(
                new[] { "id", "loan_id", "amount", "date", "note" },
                new[] { "id", "loan_id", "amount", "date", "note" },
                new[] { "INTEGER", "INTEGER", "REAL", "TEXT", "TEXT" },
                "CREATE TABLE IF NOT EXISTS loan_transactions (id INTEGER PRIMARY KEY, user_id INTEGER, loan_id INTEGER NOT NULL, amount REAL NOT NULL, date TEXT NOT NULL, note TEXT NOT NULL)",
                "id DESC"),
            ["accounts"] = new(
                new[] { "id", "name", "opening" },
                new[] { "id", "name", "opening" },
                new[] { "INTEGER", "TEXT", "REAL" },
                "CREATE TABLE IF NOT EXISTS accounts (id INTEGER PRIMARY KEY, user_id INTEGER, name TEXT NOT NULL, opening REAL NOT NULL)",
                "id ASC"),
        };

        public DbBridge()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaridewFinance");
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "maridew.db");
            _dbPath = dbPath;
            _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            _backupDir = Path.Combine(dir, "backups");
            Directory.CreateDirectory(_backupDir);
            Initialize();
            EnsureDailyBackup();

            // Long-running sessions: roll the backup over when the date changes.
            _backupTimer = new System.Timers.Timer(TimeSpan.FromMinutes(15).TotalMilliseconds)
            {
                AutoReset = true
            };
            _backupTimer.Elapsed += (_, _) => EnsureDailyBackup();
            _backupTimer.Start();
        }

        /// <summary>
        /// Creates a consistent snapshot of the database via "VACUUM INTO" once
        /// per calendar day, then prunes old backups beyond the newest 7.
        /// Failures are swallowed — backups must never break the app.
        /// </summary>
        private void EnsureDailyBackup()
        {
            lock (_backupLock)
            {
                try
                {
                    var today = DateTime.Now.ToString("yyyy-MM-dd");
                    if (today == _lastBackupDate) return;

                    var target = Path.Combine(_backupDir, $"maridew-{today}.db");
                    if (!File.Exists(target))
                    {
                        using var db = Open();
                        using var cmd = db.CreateCommand();
                        cmd.CommandText = "VACUUM INTO $path";
                        var p = cmd.CreateParameter();
                        p.ParameterName = "$path";
                        p.Value = target;
                        cmd.Parameters.Add(p);
                        cmd.ExecuteNonQuery();
                    }
                    _lastBackupDate = today;
                    PruneOldBackups();
                }
                catch
                {
                    // Ignore backup failures (locked file, disk full, ...).
                }
            }
        }

        private void PruneOldBackups()
        {
            try
            {
                // Only daily snapshots count against retention; "pre-restore"
                // safety snapshots are kept until removed manually.
                var files = Directory.GetFiles(_backupDir, "maridew-????-??-??.db")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .ToList();
                foreach (var old in files.Skip(BackupRetention))
                {
                    try { File.Delete(old); } catch { /* try again next run */ }
                }
            }
            catch
            {
                // Ignore pruning failures.
            }
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private void Initialize()
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = string.Join("; ", Tables.Values.Select(t => t.CreateSql));
            cmd.ExecuteNonQuery();
            MigrateLegacyTables(db);
        }

        /// <summary>
        /// Adds columns that older builds lacked: user_id (per-user scoping,
        /// without which LoadAll/SaveTable throw and the dashboard appears to
        /// have lost all data) and categories.color (stable chart colors).
        /// </summary>
        private static void MigrateLegacyTables(SqliteConnection db)
        {
            var wanted = new (string Table, string Column, string Decl)[]
            {
                ("transactions", "user_id", "INTEGER"),
                ("budgets", "user_id", "INTEGER"),
                ("loans", "user_id", "INTEGER"),
                ("savings_goals", "user_id", "INTEGER"),
                ("categories", "user_id", "INTEGER"),
                ("savings_transactions", "user_id", "INTEGER"),
                ("loan_transactions", "user_id", "INTEGER"),
                ("accounts", "user_id", "INTEGER"),
                ("transactions", "to_account", "TEXT NOT NULL DEFAULT ''"),
                ("categories", "color", "TEXT NOT NULL DEFAULT ''")
            };
            foreach (var (table, column, decl) in wanted)
            {
                if (TableExists(db, table) && !ColumnExists(db, table, column))
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {decl}";
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

        /// <summary>Full path of the live database file, for display in Settings.</summary>
        public string GetDatabasePath()
        {
            return _dbPath;
        }

        /// <summary>
        /// Brings a freshly-restored database up to the current schema: creates
        /// any missing tables and adds user_id where an older backup lacks it,
        /// so LoadAll/SaveTable work immediately after a restore.
        /// </summary>
        private void EnsureCurrentSchema()
        {
            try
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = string.Join("; ", Tables.Values.Select(t => t.CreateSql));
                cmd.ExecuteNonQuery();
                MigrateLegacyTables(db);
            }
            catch
            {
                // Never block a restore over schema upkeep.
            }
        }

        /// <summary>Metadata for each backup file, newest first.</summary>
        public string ListBackups()
        {
            var list = new List<object>();
            try
            {
                foreach (var f in Directory.GetFiles(_backupDir, "maridew-*.db").OrderByDescending(f => f, StringComparer.Ordinal))
                {
                    var info = new FileInfo(f);
                    list.Add(new Dictionary<string, object>
                    {
                        ["name"] = info.Name,
                        ["sizeKb"] = Math.Round(info.Length / 1024.0, 1),
                        ["modified"] = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                    });
                }
            }
            catch
            {
                // Return whatever was collected (possibly nothing).
            }
            return JsonSerializer.Serialize(list);
        }

        /// <summary>
        /// Restores a backup over the live database: validates the file is a
        /// real SQLite database, takes a safety snapshot of the current data,
        /// then copies the backup into place. The dashboard reloads afterwards.
        /// </summary>
        public string RestoreBackup(string fileName)
        {
            var name = Path.GetFileName(fileName ?? "");   // blocks any path traversal
            var source = Path.Combine(_backupDir, name);
            if (!File.Exists(source))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "Backup file not found." });
            }
            if (!IsSqliteFile(source))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "That file is not a valid SQLite database." });
            }

            try
            {
                // Safety net: snapshot the current database before overwriting it.
                var safety = Path.Combine(_backupDir, $"maridew-pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                using (var db = Open())
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "VACUUM INTO $path";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "$path";
                    p.Value = safety;
                    cmd.Parameters.Add(p);
                    cmd.ExecuteNonQuery();
                }

                // Release pooled connections so the file can be replaced.
                SqliteConnection.ClearAllPools();
                File.Copy(source, _dbPath, overwrite: true);
                EnsureCurrentSchema();
                App.ReapplyLockSettings();

                return JsonSerializer.Serialize(new { ok = true, safety = Path.GetFileName(safety) });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        }

        private static bool IsSqliteFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                Span<byte> header = stackalloc byte[16];
                if (stream.Read(header) != 16) return false;
                return System.Text.Encoding.ASCII.GetString(header).StartsWith("SQLite format 3", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Opens the backups folder in Windows Explorer.</summary>
        public void OpenBackupFolder()
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _backupDir,
                UseShellExecute = true
            });
        }

        /// <summary>
        /// Asks the user where to save a portable copy of the whole database,
        /// then writes it. Used by the Settings tab for moving data between
        /// machines.
        /// </summary>
        public string ExportDatabase()
        {
            string? target = null;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export Maridew Finance Database",
                    FileName = $"MaridewFinance-{DateTime.Now:yyyy-MM-dd}.db",
                    Filter = "Maridew Finance Database (*.db)|*.db|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true) target = dlg.FileName;
            });
            if (target == null)
            {
                return JsonSerializer.Serialize(new { ok = false, cancelled = true });
            }
            return ExportDatabaseTo(target);
        }

        /// <summary>Writes a consistent snapshot of the database to an arbitrary path.</summary>
        public string ExportDatabaseTo(string filePath)
        {
            try
            {
                var target = Path.GetFullPath(filePath ?? "");
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "VACUUM INTO $path";
                var p = cmd.CreateParameter();
                p.ParameterName = "$path";
                p.Value = target;
                cmd.Parameters.Add(p);
                cmd.ExecuteNonQuery();
                return JsonSerializer.Serialize(new { ok = true, path = target });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Asks the user for a previously exported database file and replaces
        /// the live database with it (after validating it and snapshotting the
        /// current data). The dashboard reloads afterwards.
        /// </summary>
        public string ImportDatabase()
        {
            string? source = null;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import Maridew Finance Database",
                    Filter = "Maridew Finance Database (*.db)|*.db|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true) source = dlg.FileName;
            });
            if (source == null)
            {
                return JsonSerializer.Serialize(new { ok = false, cancelled = true });
            }
            return ImportDatabaseFrom(source);
        }

        /// <summary>Validates a database file and installs it as the live database.</summary>
        public string ImportDatabaseFrom(string filePath)
        {
            try
            {
                var source = Path.GetFullPath(filePath ?? "");
                if (!File.Exists(source))
                {
                    return JsonSerializer.Serialize(new { ok = false, error = "File not found." });
                }
                if (!IsSqliteFile(source))
                {
                    return JsonSerializer.Serialize(new { ok = false, error = "That file is not a valid SQLite database." });
                }
                if (!HasOurTables(source))
                {
                    return JsonSerializer.Serialize(new { ok = false, error = "That file is not a Maridew Finance database (required tables are missing)." });
                }

                // Safety net: snapshot the current database before overwriting it.
                // Also remember who is signed in so we can re-scope afterwards.
                var previousUsername = App.Auth?.GetUsername(_currentUserId);
                var safety = Path.Combine(_backupDir, $"maridew-pre-import-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                using (var db = Open())
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "VACUUM INTO $path";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "$path";
                    p.Value = safety;
                    cmd.Parameters.Add(p);
                    cmd.ExecuteNonQuery();
                }

                SqliteConnection.ClearAllPools();
                File.Copy(source, _dbPath, overwrite: true);
                EnsureCurrentSchema();

                // If the installed database has no users (pre-accounts export)
                // or the signed-in username is not in it, require a re-login.
                var auth = App.Auth;
                if (auth == null || auth.UserCount() == 0 || previousUsername == null
                    || !auth.TryGetUserIdByName(previousUsername, out var newId))
                {
                    _currentUserId = 0;
                    return JsonSerializer.Serialize(new { ok = true, reauth = true, safety = Path.GetFileName(safety) });
                }

                // Same username exists in the imported data — stay signed in,
                // now scoped to its (possibly different) user id.
                _currentUserId = newId;
                App.ReapplyLockSettings();
                return JsonSerializer.Serialize(new { ok = true, safety = Path.GetFileName(safety) });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        }

        private static bool HasOurTables(string path)
        {
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
                using var conn = new SqliteConnection(cs);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('transactions', 'budgets', 'loans', 'savings_goals', 'categories', 'savings_transactions', 'loan_transactions', 'accounts')";
                return Convert.ToInt64(cmd.ExecuteScalar()) >= 4;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Serializes the signed-in user's tables to JSON for the dashboard.</summary>
        public string LoadAll()
        {
            if (_currentUserId == 0)
            {
                throw new InvalidOperationException("No user is signed in.");
            }

            var result = new Dictionary<string, object>();
            foreach (var (table, def) in Tables)
            {
                var rows = new List<Dictionary<string, object>>();
                using (var db = Open())
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = $"SELECT {string.Join(", ", def.Columns)} FROM {table} WHERE user_id = $uid ORDER BY {def.OrderBy}";
                    var uid = cmd.CreateParameter();
                    uid.ParameterName = "$uid";
                    uid.Value = _currentUserId;
                    cmd.Parameters.Add(uid);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = new Dictionary<string, object>();
                        for (int i = 0; i < def.Columns.Length; i++)
                        {
                            object value = reader.IsDBNull(i)
                                ? (def.Types[i] == "TEXT" ? (object)"" : (object)0)
                                : reader.GetValue(i);
                            row[def.Props[i]] = value;
                        }
                        rows.Add(row);
                    }
                }
                result[table] = rows;
            }
            return JsonSerializer.Serialize(result);
        }

        /// <summary>Replaces the contents of one table with the supplied JSON array.</summary>
        public void SaveTable(string table, string jsonArrayJson)
        {
            if (!Tables.TryGetValue(table, out var def))
            {
                throw new ArgumentException($"Unknown table '{table}'.");
            }

            if (_currentUserId == 0)
            {
                throw new InvalidOperationException("No user is signed in.");
            }

            using var doc = JsonDocument.Parse(jsonArrayJson);
            using var db = Open();
            using var tx = db.BeginTransaction();

            using (var del = db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM {table} WHERE user_id = $uid";
                var uid = del.CreateParameter();
                uid.ParameterName = "$uid";
                uid.Value = _currentUserId;
                del.Parameters.Add(uid);
                del.ExecuteNonQuery();
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                var cols = string.Join(", ", def.Columns);
                var pars = string.Join(", ", def.Columns.Select((_, i) => "$p" + i));
                cmd.CommandText = $"INSERT INTO {table} ({cols}, user_id) VALUES ({pars}, $uid)";
                var uid = cmd.CreateParameter();
                uid.ParameterName = "$uid";
                uid.Value = _currentUserId;
                cmd.Parameters.Add(uid);

                for (int i = 0; i < def.Columns.Length; i++)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = "$p" + i;
                    if (element.TryGetProperty(def.Props[i], out var value))
                    {
                        switch (def.Types[i])
                        {
                            case "INTEGER": p.Value = value.GetInt64(); break;
                            case "REAL": p.Value = value.GetDouble(); break;
                            default: p.Value = value.GetString() ?? ""; break;
                        }
                    }
                    else
                    {
                        p.Value = def.Types[i] == "TEXT" ? "" : 0;
                    }
                    cmd.Parameters.Add(p);
                }
                cmd.ExecuteNonQuery();
            }

            tx.Commit();

            // Local save landed: schedule a debounced encrypted cloud push.
            App.CloudSync?.SchedulePush();
        }
    }
}
