using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MaridewFinance.App
{
    /// <summary>
    /// Desktop-side zero-knowledge cloud sync, protocol-compatible with the
    /// browser edition (web-bridge.js) and the Cloudflare sync worker:
    ///
    ///  - PBKDF2-SHA256, 150k rounds, from the account password:
    ///      salt "auth|&lt;lowercase username&gt;" -> authHex (login proof sent to server)
    ///      salt "enc|&lt;lowercase username&gt;"  -> AES-256 key (never leaves this machine)
    ///  - All data AES-GCM encrypted before upload; server stores ciphertext only.
    ///  - Sync model: whole-database encrypted blobs, merged by row id on pull.
    ///      * rows present on only one side are kept (union)
    ///      * rows differing on both sides: the side whose cloud blob changed
    ///        most recently wins (tracked via the server's updatedAt stamp)
    ///      * every sync ends with a push, so the server converges to the union
    ///
    /// The link (token + encryption key + last-sync stamps) is stored in
    /// %LOCALAPPDATA%\MaridewFinance\cloudsync.json, next to the SQLite
    /// database it protects. Pushes are debounced after every save; pulls run
    /// on sign-in, on demand ("Sync Now") and every five minutes.
    /// </summary>
    public class CloudSyncService
    {
        private const string SyncServer = "https://maridew-sync.maridew.workers.dev";
        private const int KdfIterations = 150_000;
        private const int PushDebounceMs = 2_500;
        private const int PullIntervalMs = 5 * 60 * 1_000;

        private static readonly string[] TableNames =
        {
            "transactions", "budgets", "loans", "savings_goals",
            "categories", "savings_transactions", "loan_transactions", "accounts"
        };

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly string _statePath;
        private readonly object _gate = new();
        private CloudState _state = new();

        // Sync machinery
        private readonly object _pushLock = new();
        private System.Timers.Timer? _pushTimer;
        private System.Timers.Timer? _pullTimer;
        private volatile bool _busy;
        private volatile bool _syncInProgress;

        private sealed class CloudState
        {
            public string Username { get; set; } = "";
            public string LinkedDesktopUser { get; set; } = "";
            public string Token { get; set; } = "";
            public string EncKey { get; set; } = "";          // base64, this machine only
            public string LastServerUpdatedAt { get; set; } = "";
            public string LastPushAt { get; set; } = "";
            public string LastPullAt { get; set; } = "";
            public string LastError { get; set; } = "";
            public string LastPushFingerprint { get; set; } = "";   // sha256 of last uploaded payload
            public bool PendingReload { get; set; }                  // pull merged new data; page should reload
        }

        public CloudSyncService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaridewFinance");
            Directory.CreateDirectory(dir);
            _statePath = Path.Combine(dir, "cloudsync.json");
            LoadState();
        }

        // ------------------------------------------------------------ status

        /// <summary>True when a cloud link exists for the signed-in desktop user.</summary>
        public bool IsEnabledForCurrentUser =>
            !string.IsNullOrEmpty(_state.Token) &&
            string.Equals(_state.LinkedDesktopUser, CurrentDesktopUsername(), StringComparison.OrdinalIgnoreCase);

        private string CurrentDesktopUsername()
        {
            try
            {
                var uid = App.Bridge?.CurrentUserId ?? 0;
                return App.Auth?.GetUsername(uid) ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Status snapshot for the Settings card. Returns JSON:
        /// { enabled, username, server, lastPush, lastPull, lastError, busy }.
        /// </summary>
        public string GetStatus()
        {
            lock (_gate)
            {
                return JsonSerializer.Serialize(new
                {
                    enabled = IsEnabledForCurrentUser,
                    username = _state.Username,
                    server = SyncServer,
                    lastPush = _state.LastPushAt,
                    lastPull = _state.LastPullAt,
                    lastError = _state.LastError,
                    busy = _busy,
                    pendingReload = _state.PendingReload
                });
            }
        }

        // ------------------------------------------------------- enable / disable

        /// <summary>
        /// Links the signed-in desktop account to its cloud account (creating
        /// the cloud account on first use) using the cloud password. Merges
        /// any existing cloud data into the desktop database, then pushes the
        /// desktop state up. Returns JSON { ok, error?, created? }.
        /// </summary>
        public async Task<string> Enable(string password)
        {
            var desktopUser = CurrentDesktopUsername();
            if (string.IsNullOrEmpty(desktopUser))
            {
                return Fail("No desktop account is signed in.");
            }
            if (string.IsNullOrEmpty(password) || password.Length < 6)
            {
                return Fail("Password must be at least 6 characters.");
            }

            try
            {
                var nameLower = desktopUser.Trim().ToLowerInvariant();
                var authHex = DeriveAuthHex(nameLower, password);
                var encKey = DeriveEncKey(nameLower, password);

                var signin = await PostJson("/signin", new Dictionary<string, object>
                {
                    ["username"] = desktopUser.Trim(),
                    ["authHash"] = authHex
                });

                bool created = false;
                string token;
                JsonObject? cloudBlobWrapper = null;
                string? serverUpdatedAt = null;

                if (signin != null && signin.TryGetPropertyValue("ok", out var okNode) && okNode is JsonValue v && v.TryGetValue<bool>(out var okVal) && okVal)
                {
                    token = signin["token"]?.GetValue<string>() ?? "";
                    if (signin["blob"] is JsonValue bv && bv.TryGetValue<string>(out var blobStr))
                    {
                        cloudBlobWrapper = DecodeBlob(blobStr, encKey);
                    }
                    if (signin["updatedAt"] is JsonValue uv && uv.TryGetValue<string>(out var upStr))
                    {
                        serverUpdatedAt = upStr;
                    }
                }
                else
                {
                    var error = signin?["error"]?.GetValue<string>() ?? "";
                    var noAccount = error.Contains("No account", StringComparison.OrdinalIgnoreCase);
                    if (!noAccount)
                    {
                        return Fail(string.IsNullOrEmpty(error)
                            ? "Could not reach the sync server."
                            : error);
                    }
                    var signup = await PostJson("/signup", new Dictionary<string, object>
                    {
                        ["username"] = desktopUser.Trim(),
                        ["authHash"] = authHex
                    });
                    if (signup == null || !IsOk(signup))
                    {
                        return Fail(signup?["error"]?.GetValue<string>() ?? "Cloud sign-up failed.");
                    }
                    created = true;
                    token = signup["token"]?.GetValue<string>() ?? "";
                }

                if (string.IsNullOrEmpty(token))
                {
                    return Fail("Sync server did not issue a session token.");
                }

                lock (_gate)
                {
                    _state = new CloudState
                    {
                        Username = nameLower,
                        LinkedDesktopUser = desktopUser,
                        Token = token,
                        EncKey = Convert.ToBase64String(encKey),
                        LastError = ""
                    };
                }
                SaveState();
                StartPullLoop();

                // First merge: whatever is already in the cloud (e.g. pushed
                // from a browser earlier) unions with the desktop database.
                // Desktop rows win on conflicts — it is the data's origin.
                if (cloudBlobWrapper != null)
                {
                    MergeCloudIntoDesktop(cloudBlobWrapper, cloudNewer: false);
                }

                // Stamp the server state we just consumed so the first sync's
                // pull does not re-merge the same blob with "cloud wins" and
                // override the desktop-wins merge above.
                lock (_gate)
                {
                    _state.LastServerUpdatedAt = serverUpdatedAt ?? "";
                    SaveStateLocked();
                }

                await SyncNowAsync();

                return JsonSerializer.Serialize(new { ok = true, created });
            }
            catch (Exception ex)
            {
                return Fail(Friendly(ex));
            }
        }

        /// <summary>
        /// Removes the cloud link from this machine. The encrypted copy on the
        /// server is left in place; linking again re-merges it.
        /// </summary>
        public Task<string> Disable()
        {
            lock (_gate)
            {
                _state = new CloudState();
            }
            SaveState();
            StopTimers();
            return Task.FromResult(JsonSerializer.Serialize(new { ok = true }));
        }

        // ------------------------------------------------------------- sync

        /// <summary>Pull → merge → push, once. Returns JSON { ok, error?, note? }.</summary>
        public async Task<string> SyncNowAsync()
        {
            if (_busy)
            {
                return JsonSerializer.Serialize(new { ok = true, note = "Sync already running." });
            }
            if (string.IsNullOrEmpty(_state.Token) || !IsEnabledForCurrentUser)
            {
                return Fail("Cloud sync is not enabled.");
            }

            _busy = true;
            _syncInProgress = true;   // keep SaveTable-triggered pushes quiet
            try
            {
                var getRes = await CloudApi(HttpMethod.Get, "/data", _state.Token);
                if (getRes == null || !IsOk(getRes))
                {
                    var err = getRes?["error"]?.GetValue<string>() ?? "Could not reach the sync server.";
                    SetError(err);
                    return Fail(err);
                }

                string? serverUpdatedAt = getRes["updatedAt"]?.GetValue<string>();
                bool cloudChanged = !string.IsNullOrEmpty(serverUpdatedAt)
                    && serverUpdatedAt != _state.LastServerUpdatedAt;

                if (getRes["blob"] is JsonValue bv && bv.TryGetValue<string>(out var blobStr) && !string.IsNullOrEmpty(blobStr))
                {
                    var encKey = CurrentEncKey();
                    if (encKey == null)
                    {
                        return Fail("Encryption key missing — disable and re-enable sync.");
                    }
                    var payload = DecodeBlob(blobStr, encKey);
                    if (payload == null)
                    {
                        SetError("Cloud password changed on another device - Disable, then Enable with your current cloud password to re-link.");
                        return Fail("Cloud password changed on another device - Disable, then Enable with your current cloud password to re-link.");
                    }
                    if (cloudChanged)
                    {
                        MergeCloudIntoDesktop(payload, cloudNewer: true);
                        lock (_gate)
                        {
                            _state.LastPullAt = Now();
                            _state.PendingReload = true;   // dashboard should re-read the DB
                        }
                    }
                    lock (_gate)
                    {
                        if (!string.IsNullOrEmpty(serverUpdatedAt))
                        {
                            _state.LastServerUpdatedAt = serverUpdatedAt;
                        }
                    }
                }

                // Always finish with a push so the server converges to the union.
                await PushAsync();

                lock (_gate)
                {
                    _state.LastError = "";
                    SaveStateLocked();
                }
                return JsonSerializer.Serialize(new { ok = true });
            }
            catch (Exception ex)
            {
                SetError(Friendly(ex));
                return Fail(Friendly(ex));
            }
            finally
            {
                _busy = false;
                _syncInProgress = false;
            }
        }

        /// <summary>
        /// Called by the dashboard after it reloaded in response to PendingReload,
        /// so the flag does not trigger another reload.
        /// </summary>
        public void AcknowledgeReload()
        {
            lock (_gate)
            {
                if (_state.PendingReload)
                {
                    _state.PendingReload = false;
                    SaveStateLocked();
                }
            }
        }

        /// <summary>Debounced push after a local save (called from DbBridge).</summary>
        public void SchedulePush()
        {
            if (string.IsNullOrEmpty(_state.Token) || !IsEnabledForCurrentUser) return;
            if (_syncInProgress) return;

            lock (_pushLock)
            {
                if (_pushTimer == null)
                {
                    _pushTimer = new System.Timers.Timer(PushDebounceMs) { AutoReset = false };
                    _pushTimer.Elapsed += async (_, _) =>
                    {
                        _pushTimer?.Stop();
                        _pushTimer?.Dispose();
                        _pushTimer = null;
                        if (!_busy && !_syncInProgress)
                        {
                            await SyncNowAsync();
                        }
                    };
                    _pushTimer.Start();
                }
                else
                {
                    _pushTimer.Stop();
                    _pushTimer.Start();
                }
            }
        }

        /// <summary>
        /// Starts the periodic pull loop (after enable / sign-in) and runs one
        /// sync shortly after, so web-side edits made while the app was closed
        /// appear without waiting for the first interval tick.
        /// </summary>
        public void StartPullLoop()
        {
            if (!IsEnabledForCurrentUser) return;
            StopTimers();
            _pullTimer = new System.Timers.Timer(PullIntervalMs) { AutoReset = true };
            _pullTimer.Elapsed += async (_, _) =>
            {
                if (!_busy && !_syncInProgress && IsEnabledForCurrentUser)
                {
                    await SyncNowAsync();
                }
            };
            _pullTimer.Start();

            // Kick off an initial catch-up sync shortly after startup.
            Task.Run(async () =>
            {
                await Task.Delay(3_000);
                if (!_busy && !_syncInProgress && IsEnabledForCurrentUser)
                {
                    await SyncNowAsync();
                }
            });
        }

        /// <summary>Call after a desktop sign-in: resumes the link if one exists.</summary>
        public void OnSignedIn(string username)
        {
            if (string.Equals(_state.LinkedDesktopUser, username, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(_state.Token))
            {
                StartPullLoop();
            }
        }

        /// <summary>
        /// Re-keys the cloud account after a desktop password change: re-encrypts
        /// the cloud blob under the new key and updates the stored auth proof.
        /// Fire-and-forget; failures surface in the Settings status line.
        /// </summary>
        public async Task OnPasswordChangedAsync(string currentPassword, string newPassword)
        {
            if (string.IsNullOrEmpty(_state.Token) || !IsEnabledForCurrentUser) return;
            try
            {
                var nameLower = _state.Username;
                var oldAuth = DeriveAuthHex(nameLower, currentPassword);
                var oldEnc = DeriveEncKey(nameLower, currentPassword);
                var newAuth = DeriveAuthHex(nameLower, newPassword);
                var newEnc = DeriveEncKey(nameLower, newPassword);

                // Re-encrypt the current cloud payload under the new key.
                var getRes = await CloudApi(HttpMethod.Get, "/data", _state.Token);
                JsonObject? payload = null;
                if (getRes != null && IsOk(getRes) &&
                    getRes["blob"] is JsonValue bv && bv.TryGetValue<string>(out var blobStr) &&
                    !string.IsNullOrEmpty(blobStr))
                {
                    payload = DecodeBlob(blobStr, oldEnc);
                    if (payload == null)
                    {
                        // The cloud account is keyed to a different password than
                        // the desktop one. NEVER wipe it with a re-encrypt — leave
                        // the cloud account alone and tell the user.
                        SetError("Cloud account uses a different password — it was left unchanged. Change it from the web edition.");
                        return;
                    }
                }
                var blob = EncodeBlob(payload ?? new JsonObject(), newEnc);

                var put = await CloudApi(HttpMethod.Put, "/data", _state.Token,
                    new Dictionary<string, object> { ["blob"] = blob });
                if (put == null || !IsOk(put))
                {
                    SetError("Cloud re-encrypt after password change failed.");
                    return;
                }

                var pw = await CloudApi(HttpMethod.Post, "/password", _state.Token,
                    new Dictionary<string, object> { ["newAuthHash"] = newAuth });
                if (pw == null || !IsOk(pw))
                {
                    SetError("Cloud password update failed — sign in on the web may need the old password.");
                    return;
                }

                lock (_gate)
                {
                    _state.EncKey = Convert.ToBase64String(newEnc);
                    if (put["updatedAt"] is JsonValue uv && uv.TryGetValue<string>(out var upStr))
                    {
                        _state.LastServerUpdatedAt = upStr;
                    }
                    _state.LastPushAt = Now();
                    _state.LastError = "";
                    SaveStateLocked();
                }
            }
            catch (Exception ex)
            {
                SetError(Friendly(ex));
            }
        }

        // ---------------------------------------------------------- plumbing

        private async Task PushAsync()
        {
            var bridge = App.Bridge;
            if (bridge == null) return;

            var encKey = CurrentEncKey();
            if (encKey == null) return;

            var tables = JsonNode.Parse(bridge.LoadAll()) as JsonObject;
            if (tables == null) return;

            JsonObject settings = new();
            try
            {
                var s = JsonNode.Parse(bridge.LoadSettings()) as JsonObject;
                if (s != null) settings = s;
            }
            catch { /* settings are optional */ }

            var payload = new JsonObject
            {
                ["tables"] = tables,
                ["settings"] = settings
            };

            // Skip the upload when the local payload is byte-identical to what
            // we pushed last time — otherwise every sync round-trips a PUT and
            // the other device re-applies + re-pushes forever.
            var fingerprint = Fingerprint(payload);
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_state.LastPushFingerprint) && fingerprint == _state.LastPushFingerprint)
                {
                    return;
                }
            }

            var blob = EncodeBlob(payload, encKey);

            var put = await CloudApi(HttpMethod.Put, "/data", _state.Token,
                new Dictionary<string, object> { ["blob"] = blob });
            if (put != null && IsOk(put))
            {
                lock (_gate)
                {
                    _state.LastPushAt = Now();
                    _state.LastPushFingerprint = fingerprint;
                    if (put["updatedAt"] is JsonValue uv && uv.TryGetValue<string>(out var upStr))
                    {
                        _state.LastServerUpdatedAt = upStr;
                    }
                    SaveStateLocked();
                }
            }
            else
            {
                SetError(put?["error"]?.GetValue<string>() ?? "Cloud push failed.");
            }
        }

        /// <summary>Stable content hash of a sync payload (change detection).</summary>
        internal static string Fingerprint(JsonObject payload)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString()))).ToLowerInvariant();
        }

        /// <summary>
        /// Merges a decrypted cloud payload into the desktop SQLite database,
        /// table by table, unioning rows by id. When <paramref name="cloudNewer"/>
        /// is true, cloud rows win conflicts; otherwise local rows win.
        /// Changed tables are written back through DbBridge.SaveTable.
        /// </summary>
        private void MergeCloudIntoDesktop(JsonObject cloudPayload, bool cloudNewer)
        {
            var bridge = App.Bridge;
            if (bridge == null) return;

            JsonObject? cloudTables = cloudPayload["tables"] as JsonObject;
            if (cloudTables == null) return;

            var local = JsonNode.Parse(bridge.LoadAll()) as JsonObject;
            if (local == null) return;

            foreach (var table in TableNames)
            {
                if (!cloudTables.TryGetPropertyValue(table, out var cloudNode) || cloudNode is not JsonArray cloudRows)
                {
                    continue;
                }
                var localRows = local[table] as JsonArray ?? new JsonArray();

                var merged = MergeRows(localRows, cloudRows, cloudNewer);
                if (merged == null) continue;   // identical on both sides

                bridge.SaveTable(table, merged.ToJsonString());
            }
        }

        /// <summary>Union of both row sets by "id"; conflicts go to the newer side.</summary>
        internal static JsonArray? MergeRows(JsonArray localRows, JsonArray cloudRows, bool cloudNewer)
        {
            var localById = new Dictionary<long, JsonNode?>();
            foreach (var row in localRows)
            {
                if (row != null && IdOf(row) is long id) localById[id] = row;
            }
            var cloudById = new Dictionary<long, JsonNode?>();
            foreach (var row in cloudRows)
            {
                if (row != null && IdOf(row) is long id) cloudById[id] = row;
            }

            var result = new JsonArray();
            bool changed = false;

            // Cloud rows keep their order first.
            foreach (var row in cloudRows)
            {
                if (row == null) continue;
                var id = IdOf(row);
                if (id is long idVal && localById.TryGetValue(idVal, out var localRow) && localRow != null)
                {
                    if (JsonNode.DeepEquals(localRow, row))
                    {
                        result.Add(localRow.DeepClone());          // identical on both sides
                    }
                    else
                    {
                        result.Add((cloudNewer ? row : localRow).DeepClone());   // conflict: newer side wins
                        changed = true;
                    }
                }
                else
                {
                    result.Add(row.DeepClone());                    // cloud-only row
                    changed = true;
                }
            }
            // Local-only rows appended.
            foreach (var row in localRows)
            {
                if (row == null) continue;
                var id = IdOf(row);
                if (id is long idVal && !cloudById.ContainsKey(idVal))
                {
                    result.Add(row.DeepClone());
                    changed = true;
                }
            }

            if (!changed && result.Count == localRows.Count) return null;   // nothing to do
            return result;
        }

        private static long? IdOf(JsonNode row)
        {
            if (row is JsonObject obj && obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<double>(out var d)) return (long)d;
            }
            return null;
        }

        // ------------------------------------------------------------- crypto

        internal static string DeriveAuthHex(string nameLower, string password)
        {
            var bytes = Pbkdf2(password, Encoding.UTF8.GetBytes("auth|" + nameLower));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        internal static byte[] DeriveEncKey(string nameLower, string password)
        {
            return Pbkdf2(password, Encoding.UTF8.GetBytes("enc|" + nameLower));
        }

        private static byte[] Pbkdf2(string password, byte[] salt)
        {
            using var derive = new Rfc2898DeriveBytes(password, salt, KdfIterations, HashAlgorithmName.SHA256);
            return derive.GetBytes(32);
        }

        /// <summary>AES-256-GCM; output "b64(iv):b64(ciphertext+tag)" like the web.</summary>
        internal static string EncodeBlob(JsonObject payload, byte[] key)
        {
            var plain = Encoding.UTF8.GetBytes(payload.ToJsonString());
            var iv = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(iv, plain, cipher, tag);
            var combined = new byte[cipher.Length + tag.Length];
            Buffer.BlockCopy(cipher, 0, combined, 0, cipher.Length);
            Buffer.BlockCopy(tag, 0, combined, cipher.Length, tag.Length);
            return Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(combined);
        }

        internal static JsonObject? DecodeBlob(string blob, byte[] key)
        {
            try
            {
                var parts = blob.Split(':');
                if (parts.Length != 2) return null;
                var iv = Convert.FromBase64String(parts[0]);
                var combined = Convert.FromBase64String(parts[1]);
                if (combined.Length < 16) return null;
                var cipher = new byte[combined.Length - 16];
                var tag = new byte[16];
                Buffer.BlockCopy(combined, 0, cipher, 0, cipher.Length);
                Buffer.BlockCopy(combined, cipher.Length, tag, 0, tag.Length);
                var plain = new byte[cipher.Length];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(iv, cipher, tag, plain);
                return JsonNode.Parse(Encoding.UTF8.GetString(plain)) as JsonObject;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------- HTTP

        private static async Task<JsonObject?> PostJson(string path, Dictionary<string, object> body)
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var res = await Http.PostAsync(SyncServer + path, content);
            return await ToJsonObject(res);
        }

        private static async Task<JsonObject?> CloudApi(HttpMethod method, string path, string? token, Dictionary<string, object>? body = null)
        {
            using var req = new HttpRequestMessage(method, SyncServer + path);
            if (!string.IsNullOrEmpty(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            if (body != null)
            {
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            var res = await Http.SendAsync(req);
            return await ToJsonObject(res);
        }

        private static async Task<JsonObject?> ToJsonObject(HttpResponseMessage res)
        {
            var text = await res.Content.ReadAsStringAsync();
            try
            {
                return JsonNode.Parse(text) as JsonObject;
            }
            catch
            {
                return new JsonObject { ["ok"] = false, ["error"] = "Bad response from sync server." };
            }
        }

        private static bool IsOk(JsonObject res)
        {
            return res.TryGetPropertyValue("ok", out var okNode)
                && okNode is JsonValue v
                && v.TryGetValue<bool>(out var b)
                && b;
        }

        // ------------------------------------------------------------- state

        private byte[]? CurrentEncKey()
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(_state.EncKey)) return null;
                try { return Convert.FromBase64String(_state.EncKey); }
                catch { return null; }
            }
        }

        private void LoadState()
        {
            try
            {
                if (File.Exists(_statePath))
                {
                    var loaded = JsonSerializer.Deserialize<CloudState>(File.ReadAllText(_statePath));
                    if (loaded != null) _state = loaded;
                }
            }
            catch
            {
                // A corrupt state file just means sync starts disabled.
                _state = new CloudState();
            }
        }

        private void SaveState()
        {
            lock (_gate)
            {
                SaveStateLocked();
            }
        }

        private void SaveStateLocked()
        {
            try
            {
                File.WriteAllText(_statePath, JsonSerializer.Serialize(_state));
            }
            catch
            {
                // Never break the app over state persistence.
            }
        }

        private void StopTimers()
        {
            _pullTimer?.Stop();
            _pullTimer?.Dispose();
            _pullTimer = null;
        }

        private void SetError(string message)
        {
            lock (_gate)
            {
                _state.LastError = message;
                SaveStateLocked();
            }
        }

        private static string Fail(string error)
        {
            return JsonSerializer.Serialize(new { ok = false, error });
        }

        private static string Now()
        {
            return DateTime.UtcNow.ToString("o");
        }

        private static string Friendly(Exception ex)
        {
            if (ex is HttpRequestException || ex is TaskCanceledException)
            {
                return "Could not reach the sync server (network?).";
            }
            return ex.Message;
        }
    }
}
