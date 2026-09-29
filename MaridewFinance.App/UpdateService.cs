using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace MaridewFinance.App
{
    /// <summary>
    /// Auto-update service exposed to the dashboard JavaScript as
    /// window.chrome.webview.hostObjects.updateBridge.
    ///
    /// Checks a JSON update feed for a newer version, downloads the signed
    /// Inno Setup installer, verifies its SHA-256 checksum, then hands off to
    /// a small batch script that runs the installer silently (Windows asks for
    /// permission via UAC since the installer installs per-machine) and
    /// relaunches the app afterwards.
    ///
    /// Update feed (host it over HTTPS anywhere - GitHub Releases, a static
    /// host, a CDN):
    ///   {
    ///     "version": "1.0.1",
    ///     "url": "https://example.com/downloads/MaridewFinanceSetup-1.0.1.exe",
    ///     "sha256": "<hex digest of the installer>",
    ///     "notes": "What's new in this version"
    ///   }
    ///
    /// The feed URL defaults to DefaultFeedUrl and can be overridden by
    /// placing a file named "update-feed-url.txt" (containing just the URL)
    /// in the app data folder: %LOCALAPPDATA%\MaridewFinance\
    ///
    /// HTTP is performed by PowerShell (Invoke-WebRequest), which ships with
    /// every supported Windows version, so the app needs no networking
    /// dependencies. The dashboard polls GetUpdateStatus() while work is in
    /// flight; every bridge method returns immediately (work happens in
    /// background PowerShell processes + a 1-second poll timer).
    /// </summary>
    public class UpdateService
    {
        /// <summary>Keep in sync with the &lt;Version&gt; in MaridewFinance.App.csproj.</summary>
        public const string CurrentVersion = "1.0.8";

        /// <summary>
        /// Where the update feed lives. When GitHubRepo is set (owner/repo),
        /// the feed is served from that repository's latest release:
        ///   https://github.com/&lt;owner&gt;/&lt;repo&gt;/releases/latest/download/latest.json
        /// (which redirects to the current release's assets). Alternatively,
        /// override with update-feed-url.txt in the app data folder.
        /// </summary>
        // static readonly rather than const: an empty const would let the
        // compiler fold `GitHubRepo != ""` to false and flag the repo-feed
        // branch in FeedUrl() as unreachable (CS0162).
        //
        // TO ENABLE UPDATES: set this to your public repo ("owner/repo"),
        // then rebuild the installer - see installer\AUTO-UPDATE.md for the
        // full one-time setup and the publish-update.ps1 release flow.
        private static readonly string GitHubRepo = "aypwwan/MaridewFinance";

        /// <summary>Placeholder used until GitHubRepo is filled in above; installs without a configured feed report "no feed" and skip the network.</summary>
        private const string UnconfiguredFeedUrl = "https://github.com/YOUR-USERNAME/MaridewFinance/releases/latest/download/latest.json";

        private readonly string _dataDir;
        private readonly string _updateDir;
        private readonly object _lock = new();
        private readonly System.Timers.Timer _pollTimer;
        private readonly System.Timers.Timer? _autoCheckTimer;

        // Latest known state, serialized for the dashboard.
        private string _state = "unknown";   // unknown|idle|checking|none|available|downloading|ready|installing|error
        private string _latestVersion = "";
        private string _notes = "";
        private string _message = "";
        private string _installerPath = "";

        // Automatic updates (default on): check at startup and every 6 hours,
        // download + checksum-verify in the background, then install silently
        // when the app next closes - so a session is never interrupted. The
        // user can still install immediately from Settings at any time.
        private bool _autoUpdateEnabled;
        private bool _autoCheck;               // true while the current check was auto-started
        private bool _autoDownload;            // true while the current download was auto-started
        private const int AutoCheckIntervalHours = 6;

        public UpdateService()
        {
            _dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaridewFinance");
            _updateDir = Path.Combine(_dataDir, "updates");
            Directory.CreateDirectory(_updateDir);
            _state = "idle";
            _message = "Ready to check for updates.";

            // If the previous session exited through the auto-updater, say so
            // once - the user should never wonder why the app restarted.
            try
            {
                if (File.Exists(PendingMarkerPath))
                {
                    _message = "Maridew Finance was updated to " + CurrentVersion + " when the app last closed.";
                    File.Delete(PendingMarkerPath);
                }
            }
            catch { /* cosmetic only */ }

            // One long-lived poller: advances the feed/download phases as the
            // background PowerShell scripts produce their output files.
            _pollTimer = new System.Timers.Timer(1000)
            {
                AutoReset = true
            };
            _pollTimer.Elapsed += (_, _) => Poll();
            _pollTimer.Start();

            _autoUpdateEnabled = ReadAutoUpdateSetting();
            if (_autoUpdateEnabled)
            {
                // First check shortly after launch, then every few hours. The
                // delay lets the app finish opening before the feed is hit.
                _autoCheckTimer = new System.Timers.Timer(TimeSpan.FromHours(AutoCheckIntervalHours).TotalMilliseconds)
                {
                    AutoReset = true
                };
                _autoCheckTimer.Elapsed += (_, _) => AutoCheck();
                _autoCheckTimer.Start();
                new System.Threading.Timer(_ => AutoCheck(), null, 5000, Timeout.Infinite);
            }
        }

        // ------------------------------------------------- automatic updates

        /// <summary>True when automatic background updates are turned on.</summary>
        public bool AutoUpdateEnabled { get { lock (_lock) return _autoUpdateEnabled; } }

        /// <summary>Bridge: current auto-update preference for the settings UI.</summary>
        public bool GetAutoUpdateEnabled() => AutoUpdateEnabled;

        /// <summary>Bridge: turn automatic updates on/off (persisted immediately).</summary>
        public void SetAutoUpdateEnabled(bool enabled)
        {
            lock (_lock)
            {
                _autoUpdateEnabled = enabled;
            }
            WriteAutoUpdateSetting(enabled);
            if (enabled)
            {
                AutoCheck();   // pick up any pending update right away
            }
        }

        /// <summary>
        /// Pending-install marker: written the moment an exit-time
        /// auto-install is staged, read+cleared on the next launch. If it is
        /// present at startup, the previous session closed with a verified
        /// update waiting and the silent installer was launched - so the first
        /// status message explains what happened instead of leaving the user
        /// to guess why the app restarted.
        /// </summary>
        private string PendingMarkerPath => Path.Combine(_updateDir, "pending-auto-install.txt");

        /// <summary>One automatic check cycle; quiet when the feed is unreachable.</summary>
        private void AutoCheck()
        {
            bool enabled;
            lock (_lock) { enabled = _autoUpdateEnabled; }
            if (!enabled) return;
            lock (_lock)
            {
                if (_state == "checking" || _state == "downloading" || _state == "installing") return;
                _autoCheck = true;
            }
            CheckForUpdates();
        }

        /// <summary>
        /// Called by the host when the app is closing. With auto-update on and
        /// a verified installer waiting, stages and launches the silent
        /// installer so the update applies during shutdown and the app
        /// relaunches afterwards. The checksum is re-verified here: the
        /// installer has been sitting on disk since it was downloaded.
        /// </summary>
        public void OnAppExiting()
        {
            string installer;
            lock (_lock)
            {
                if (!_autoUpdateEnabled || _state != "ready" || _installerPath == "") return;
                installer = _installerPath;
            }
            try
            {
                var expected = JsonField(File.ReadAllText(Path.Combine(_updateDir, "latest.json")), "sha256");
                if (expected == "" || !File.Exists(installer)) return;
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(installer)));
                if (!string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase)) return;

                var bat = WriteUpdateBat(installer);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = bat,
                    UseShellExecute = true
                });
                // Staged: the silent installer is running and this instance is
                // about to exit. The marker tells the next launch to say so.
                try { File.WriteAllText(PendingMarkerPath, "staged " + DateTime.Now.ToString("s")); } catch { }
            }
            catch
            {
                // Never block app shutdown over the update; the user can
                // still install manually next session.
            }
        }

        /// <summary>
        /// Shared installer staging: writes run-update.bat (silent install,
        /// then relaunch). Used by the manual install button and by the
        /// exit-time automatic install.
        /// </summary>
        private string WriteUpdateBat(string installerPath)
        {
            var exePath = Path.Combine(AppContext.BaseDirectory, "MaridewFinance.exe");
            var bat = Path.Combine(_updateDir, "run-update.bat");
            var content = "@echo off\r\n"
                + "start \"\" /wait \"" + installerPath + "\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART\r\n"
                + "start \"\" \"" + exePath + "\"\r\n";
            File.WriteAllText(bat, content);
            return bat;
        }

        // ------------------------------------------------------- preferences

        private string SettingsPath => Path.Combine(_dataDir, "settings.json");

        private bool ReadAutoUpdateSetting()
        {
            try
            {
                var json = File.ReadAllText(SettingsPath);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("autoUpdate", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.False)
                {
                    return false;
                }
            }
            catch
            {
                // Missing/unreadable file: default stays enabled.
            }
            return true;
        }

        private void WriteAutoUpdateSetting(bool enabled)
        {
            try
            {
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, "{\"autoUpdate\":" + (enabled ? "true" : "false") + "}");
                File.Move(tmp, SettingsPath, overwrite: true);
            }
            catch
            {
                // Preference persistence is best-effort; the in-memory value
                // still applies for this session.
            }
        }

        // ----------------- Bridge API (called from JavaScript) ------------

        /// <summary>Starts a background update check. Returns immediately.</summary>
        public string CheckForUpdates()
        {
            // No feed configured (placeholder baked in, no override file):
            // skip the network work entirely and say so plainly.
            if (!IsFeedConfigured())
            {
                lock (_lock)
                {
                    _state = "none";
                    _latestVersion = "";
                    _notes = "";
                    _message = "Updates are not configured for this install (no update feed set).";
                    _installerPath = "";
                }
                return "not-configured";
            }
            lock (_lock)
            {
                if (_state == "checking" || _state == "downloading" || _state == "installing")
                {
                    return "busy";
                }
                _state = "checking";
                _latestVersion = "";
                _notes = "";
                _message = "Checking for updates...";
                _installerPath = "";
            }
            try
            {
                var feedFile = Path.Combine(_updateDir, "latest.json");
                try { File.Delete(feedFile); } catch { /* not there yet */ }
                WriteScript("fetch-feed.ps1", FetchFeedScript(feedFile, IsFeedConfigured()));
                StartPowerShell(Path.Combine(_updateDir, "fetch-feed.ps1"));
            }
            catch (Exception ex)
            {
                SetError("Could not start the update check: " + ex.Message);
            }
            return "started";
        }

        /// <summary>Downloads the available update in the background.</summary>
        public string DownloadUpdate()
        {
            lock (_lock)
            {
                if (_state != "available")
                {
                    return "nothing-to-download";
                }
                _state = "downloading";
                _message = "Downloading version " + _latestVersion + "...";
                _installerPath = Path.Combine(_updateDir, "MaridewFinanceSetup-" + _latestVersion + ".exe");
            }
            try
            {
                var feedFile = Path.Combine(_updateDir, "latest.json");
                var url = JsonField(File.ReadAllText(feedFile), "url");
                if (url == "")
                {
                    SetError("The update feed did not provide a download URL.");
                    return "error";
                }
                var hashFile = Path.Combine(_updateDir, "installer.sha256");
                try { File.Delete(hashFile); } catch { /* not there yet */ }
                WriteScript("download.ps1", DownloadScript(url, _installerPath, hashFile));
                StartPowerShell(Path.Combine(_updateDir, "download.ps1"));
            }
            catch (Exception ex)
            {
                SetError("Could not start the download: " + ex.Message);
                return "error";
            }
            return "started";
        }

        /// <summary>
        /// Installs the downloaded update: writes a batch script that runs the
        /// installer silently, then relaunches the app, and closes this
        /// instance so the installer can replace the files.
        /// </summary>
        public string InstallUpdate()
        {
            lock (_lock)
            {
                if (_state != "ready" || _installerPath == "")
                {
                    return "not-ready";
                }
                _state = "installing";
                _message = "Installing update...";
            }
            try
            {
                var bat = WriteUpdateBat(_installerPath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = bat,
                    UseShellExecute = true
                });

                // Give the batch a moment to start, then close the app so the
                // installer can replace the running files.
                var exitTimer = new System.Timers.Timer(2500)
                {
                    AutoReset = false
                };
                exitTimer.Elapsed += (_, _) =>
                    Application.Current?.Dispatcher.Invoke(() => Application.Current?.Shutdown());
                exitTimer.Start();
                return "installing";
            }
            catch (Exception ex)
            {
                SetError("Could not start the installer: " + ex.Message);
                return "error";
            }
        }

        /// <summary>Serializes the current update state for the dashboard.</summary>
        public string GetUpdateStatus()
        {
            lock (_lock)
            {
                return JsonSerializer.Serialize(new
                {
                    state = _state,
                    current = CurrentVersion,
                    latest = _latestVersion,
                    notes = _notes,
                    message = _message,
                    autoUpdate = _autoUpdateEnabled
                });
            }
        }

        // ----------------- Background pipeline ---------------------------

        private void Poll()
        {
            lock (_lock)
            {
                try
                {
                    if (_state == "checking") PollFeed();
                    else if (_state == "downloading") PollDownload();
                }
                catch
                {
                    // Files may still be settling; retry on the next tick.
                }
            }
        }

        private void PollFeed()
        {
            var feedFile = Path.Combine(_updateDir, "latest.json");
            if (!File.Exists(feedFile)) return;
            var json = File.ReadAllText(feedFile);

            var error = JsonField(json, "error");
            if (error != "")
            {
                SetError(error);
                return;
            }
            var version = JsonField(json, "version");
            var url = JsonField(json, "url");
            if (version == "" || url == "")
            {
                SetError("The update feed did not contain a valid version and download URL.");
                return;
            }

            _notes = JsonField(json, "notes");
            if (CompareVersions(version, CurrentVersion) <= 0)
            {
                _state = "none";
                _message = "You are up to date (version " + CurrentVersion + ").";
                return;
            }
            _state = "available";
            _latestVersion = version;
            _message = _autoCheck
                ? "Version " + version + " will be installed automatically next time you close the app."
                : "Version " + version + " is available.";
            _autoCheck = false;
            // With automatic updates enabled, fetch the installer in the
            // background right away so it is already verified and waiting by
            // the time the app closes (or when the user clicks Download/
            // Install themselves). With it off, downloading stays a manual
            // click - the preference means no surprise 50 MB downloads.
            if (_autoUpdateEnabled)
            {
                _autoDownload = true;
                DownloadUpdate();
            }
        }

        private void PollDownload()
        {
            if (_installerPath == "") return;
            if (!File.Exists(_installerPath)) return; // still downloading (.part)

            var hashFile = Path.Combine(_updateDir, "installer.sha256");
            if (!File.Exists(hashFile)) return;
            var actual = File.ReadAllText(hashFile).Trim();
            if (actual == "ERROR")
            {
                SetError("Download failed - check your internet connection and try again.");
                return;
            }
            if (actual == "")
            {
                return;
            }

            var expected = JsonField(File.ReadAllText(Path.Combine(_updateDir, "latest.json")), "sha256");
            // Refuse to install unless the feed's checksum matches. An empty
            // hash in the feed is treated as a broken feed, not a pass: the
            // auto-update chain is a supply-chain surface, so verification is
            // mandatory, never optional.
            if (expected == "")
            {
                SetError("The update feed did not include a checksum — refusing to install it. Please download the installer manually.");
                return;
            }
            if (!string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                SetError("Downloaded file failed the checksum check. Please try again.");
                return;
            }
            _state = "ready";
            _message = _autoDownload
                ? "Version " + _latestVersion + " is verified and will install next time you close the app."
                : "Update downloaded and verified. Ready to install.";
            _autoDownload = false;
        }

        // ----------------- Helpers ----------------------------------------

        private void SetError(string msg)
        {
            lock (_lock)
            {
                _state = "error";
                _message = msg;
            }
        }

        private string FeedUrl()
        {
            var url = OverrideFeedUrl();
            if (url != "") return url;
            if (GitHubRepo != "")
            {
                return "https://github.com/" + GitHubRepo + "/releases/latest/download/latest.json";
            }
            return UnconfiguredFeedUrl;
        }

        /// <summary>The configured feed URL override, or "" when none is set.</summary>
        private string OverrideFeedUrl()
        {
            try
            {
                var overrideFile = Path.Combine(_dataDir, "update-feed-url.txt");
                if (File.Exists(overrideFile))
                {
                    var url = File.ReadAllText(overrideFile).Trim();
                    if (url.Length > 0) return url;
                }
            }
            catch
            {
                // Fall through to the default feed.
            }
            return "";
        }

        /// <summary>
        /// True when this install has a real feed: an override file, or the
        /// GitHubRepo constant filled in. Without one, the placeholder URL can
        /// never serve a real feed - that is a neutral "not configured" state,
        /// not an error.
 /// </summary>
        private bool IsFeedConfigured()
        {
            if (OverrideFeedUrl() != "") return true;
            return GitHubRepo != "" && !GitHubRepo.Contains("YOUR-USERNAME");
        }

        private void WriteScript(string name, string body)
        {
            File.WriteAllText(Path.Combine(_updateDir, name), body);
        }

        private void StartPowerShell(string scriptPath)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"",
                UseShellExecute = true
            });
        }

        private string FetchFeedScript(string feedFile, bool feedConfigured)
        {
            // Without a configured feed the placeholder URL can never work;
            // report that neutrally instead of as a connection error.
            var failBody = feedConfigured
                ? "('Could not reach the update feed: ' + $_.Exception.Message)"
                : "'No update feed is configured for this install.'";
            return "$ErrorActionPreference = 'Stop'\r\n"
                + "try {\r\n"
                + "    Invoke-WebRequest -Uri '" + FeedUrl() + "' -UseBasicParsing -TimeoutSec 20 -MaximumRedirection 5 -OutFile '" + feedFile + "'\r\n"
                + "} catch {\r\n"
                + "    $msg = " + failBody + " -replace '\"', \"'\"\r\n"
                + "    [System.IO.File]::WriteAllText('" + feedFile + "', ('{\"error\": \"' + $msg + '\"}'))\r\n"
                + "    exit 1\r\n"
                + "}\r\n";
        }

        private string DownloadScript(string url, string installerPath, string hashFile)
        {
            return "$ErrorActionPreference = 'Stop'\r\n"
                + "try {\r\n"
                + "    Invoke-WebRequest -Uri '" + url + "' -UseBasicParsing -TimeoutSec 600 -OutFile '" + installerPath + ".part'\r\n"
                + "    Move-Item -Force '" + installerPath + ".part' '" + installerPath + "'\r\n"
                + "    $h = (Get-FileHash -Path '" + installerPath + "' -Algorithm SHA256).Hash\r\n"
                + "    [System.IO.File]::WriteAllText('" + hashFile + "', $h)\r\n"
                + "} catch {\r\n"
                + "    [System.IO.File]::WriteAllText('" + hashFile + "', 'ERROR')\r\n"
                + "    exit 1\r\n"
                + "}\r\n";
        }

        /// <summary>Reads a string field out of a tiny JSON document (no dependency).</summary>
        private static string JsonField(string json, string name)
        {
            var key = "\"" + name + "\"";
            var i = json.IndexOf(key);
            if (i < 0) return "";
            var colon = json.IndexOf(':', i + key.Length);
            if (colon < 0) return "";
            var open = json.IndexOf('"', colon);
            if (open < 0) return "";
            var close = json.IndexOf('"', open + 1);
            if (close < 0) return "";
            return json.Substring(open + 1, close - open - 1);
        }

        /// <summary>Compares dotted numeric versions; returns &gt;0 if a is newer.</summary>
        public static int CompareVersions(string a, string b)
        {
            var pa = a.Split('.');
            var pb = b.Split('.');
            var n = Math.Max(pa.Length, pb.Length);
            for (var i = 0; i < n; i++)
            {
                var x = i < pa.Length ? ToInt(pa[i]) : 0;
                var y = i < pb.Length ? ToInt(pb[i]) : 0;
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
        }

        private static int ToInt(string s)
        {
            try { return Convert.ToInt32(s.Trim()); }
            catch { return 0; }
        }
    }
}