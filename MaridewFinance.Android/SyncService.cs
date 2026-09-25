using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using Android.Runtime;
using AndroidX.WebKit;
using Java.Interop;
using Java.Lang;
using Java.Util.Concurrent;

namespace MaridewFinance.AndroidApp
{
    /// <summary>
    /// Keeps sync alive while the app is closed: a foreground service that
    /// hosts a hidden WebView running the SAME page storage as the visible
    /// app (same IndexedDB origin) and calls the bridge's headless
    /// __maridewSync.sync() on a timer. Each tick pulls the cloud, merges it,
    /// and pushes local tables back ONLY when they differ from the cloud, so
    /// entries flow both ways while the app is closed without churning the
    /// cloud's updatedAt on idle devices. New incoming entries raise a
    /// notification; successful pushes just refresh the quiet foreground-
    /// service notification with the last sync time.
    ///
    /// The tick is driven by AlarmManager (setExactAndAllowWhileIdle when
    /// permitted, inexact otherwise) rather than an in-process Handler, so it
    /// survives Doze and does not depend on the process staying scheduled.
    /// </summary>
    [Service(Name = "com.maridew.finance.SyncService", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
    [Register("com.maridew.finance.SyncService")]
    public class SyncService : Service
    {
        private const string ChannelId = "maridew_sync";
        private const int NotifyId = 1001;
        private const long SyncIntervalMs = 5 * 60 * 1000;      // every 5 minutes
        private const long TickGraceMs = 4000;                  // page load + evaluate slack
        private const int MaxSilentFailures = 6;                // ~30 min before one heads-up

        private WebView? _webView;
        private Handler? _handler;
        private Java.Lang.Runnable? _tickRunnable;
        private bool _pageReady;
        private bool _ticking;
        private int _consecutiveFailures;

        // The interface object is injected as "MaridewSyncBridge"; the page calls
        // MaridewSyncBridge.deliverResult(json) with each sync result.
        private const string SyncJs = "(window.__maridewSync && window.__maridewSync.sync ? window.__maridewSync.sync() : Promise.resolve(JSON.stringify({ok:false,reason:'no-bridge'})))" +
            ".then(function(s){ MaridewSyncBridge.deliverResult(s); })" +
            ".catch(function(e){ MaridewSyncBridge.deliverResult(JSON.stringify({ok:false,reason:'error',error:String(e && e.message || e)})); })";

        public override IBinder? OnBind(Intent? intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            StartForegroundWithText("Background sync active - entries arrive automatically.");

            _handler = new Handler(Looper.MainLooper!);

            var assetLoader = new WebViewAssetLoader.Builder();
            assetLoader.AddPathHandler("/assets/", new WebViewAssetLoader.AssetsPathHandler(this));
            var loader = assetLoader.Build()!;

            _webView = new WebView(this);
            var s = _webView.Settings!;
            s.JavaScriptEnabled = true;
            s.DomStorageEnabled = true;
            s.DatabaseEnabled = true;
            s.CacheMode = CacheModes.NoCache;
            _webView.SetWebViewClient(new SyncWebViewClient(loader, () => _pageReady = true));

            _webView.AddJavascriptInterface(new Deliver(this), "MaridewSyncBridge");
            _webView.LoadUrl("https://appassets.androidplatform.net/assets/wwwroot/sync.html");

            ScheduleNextAlarm(SyncIntervalMs / 6);   // first tick ~50s after start
            ScheduleTick(0);                          // and start the in-process fallback loop
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
            => StartCommandResult.Sticky;

        public override void OnDestroy()
        {
            _tickRunnable?.Dispose();
            _handler?.RemoveCallbacksAndMessages(null);
            _webView?.Destroy();
            _webView = null!;
            base.OnDestroy();
        }

        // ------------------------------------------------------------ ticking

        /// <summary>
        /// AlarmManager backup for the in-process loop: fires
        /// SyncTickReceiver even when the process is frozen in Doze, which
        /// starts this service (idempotent) and lets it tick immediately.
        /// </summary>
        internal void ScheduleNextAlarm(long delayMs)
        {
            try
            {
                var am = (AlarmManager)GetSystemService(AlarmService)!;
                var pi = PendingIntent.GetBroadcast(this, 0, new Intent(this, typeof(SyncTickReceiver)),
                    PendingIntentFlags.UpdateCurrent | (Build.VERSION.SdkInt >= BuildVersionCodes.S
                        ? PendingIntentFlags.Immutable : 0));
                var trigger = Java.Lang.JavaSystem.CurrentTimeMillis() + delayMs;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S &&
                    am.CanScheduleExactAlarms())
                {
                    am.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, trigger, pi);
                }
                else
                {
                    am.SetAndAllowWhileIdle(AlarmType.RtcWakeup, trigger, pi);
                }
            }
            catch (System.Exception ex)
            {
                // Alarms are a redundancy, not the primary tick; never fatal.
                global::Android.Util.Log.Warn("maridew", "alarm scheduling failed: " + ex.Message);
            }
        }

        private void ScheduleTick(long delayMs)
        {
            if (_handler is null) return;
            if (_tickRunnable is not null) _handler.RemoveCallbacks(_tickRunnable);
            _tickRunnable = new Runnable(() => Tick());
            _handler.PostDelayed(_tickRunnable, delayMs);
        }

        private void Tick()
        {
            if (_webView is null || _handler is null) return;
            if (_ticking) { ScheduleNextTick(); return; }   // previous tick still running
            _ticking = true;
            _pageReady = false;
            _webView.Reload();
            // The client flips _pageReady on finish; a small grace covers it.
            _handler.PostDelayed(new Runnable(() =>
            {
                if (_webView is null) { _ticking = false; return; }
                if (_pageReady)
                {
                    // Hold a wakelock for the tick so Doze cannot freeze the
                    // WebView fetch halfway; released in OnSyncResult.
                    TickWakeLock.Acquire(this);
                    _webView.EvaluateJavascript(SyncJs, null);
                }
                else
                {
                    _ticking = false;
                }
                ScheduleNextTick();
            }), TickGraceMs);
        }

        private void ScheduleNextTick()
        {
            ScheduleTick(SyncIntervalMs);
            ScheduleNextAlarm(SyncIntervalMs + 30_000);   // alarm slightly after the loop's own tick
        }

        private void OnSyncResult(string? json)
        {
            _ticking = false;
            TickWakeLock.Release(this);
            global::Android.Util.Log.Info("maridew", "sync result: " + (json ?? "<null>"));
            try
            {
                if (string.IsNullOrEmpty(json)) return;
                var j = new Org.Json.JSONObject(json);
                if (!j.OptBoolean("ok", false))
                {
                    NoteFailure("Sync failed: " + (j.OptString("reason", "unknown") ?? "unknown"));
                    return;   // no-session/no-token/network: quietly retry next tick
                }

                _consecutiveFailures = 0;   // a clean round trip, idle or not

                // Pushed local changes: refresh the quiet foreground-service
                // notification ("Last synced HH:mm") - no user-facing alert,
                // uploads are the device's own edits.
                if (j.OptBoolean("pushed", false))
                {
                    UpdateForegroundText("Background sync active - last synced " + System.DateTime.Now.ToString("HH:mm") + ".");
                }

                // Pulled remote changes: raise a user notification.
                if (!j.OptBoolean("changed", false)) return;
                var added = j.OptJSONArray("added");
                var parts = new List<string>();
                if (added is not null)
                {
                    for (int i = 0; i < added.Length(); i++) parts.Add(added.GetString(i)!);
                }
                var removed = j.OptInt("removed", 0);
                var text = parts.Count > 0
                    ? "New entries: " + string.Join(", ", parts) + (removed > 0 ? $" ({removed} removed)" : "")
                    : "Your data was updated by another device.";
                Notify(text);
            }
            catch (System.Exception ex)
            {
                // Never let notification formatting kill the service.
                global::Android.Util.Log.Error("maridew", "sync handling failed: " + ex);
            }
        }

        /// <summary>
        /// After several consecutive failed ticks, say so once instead of
        /// failing silently forever (e.g. expired token after a password
        /// change, or the sync server being down).
        /// </summary>
        private void NoteFailure(string detail)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures != MaxSilentFailures) return;   // exactly once per failure streak
            var pi = PendingIntent.GetActivity(this, 0, PackageManager!.GetLaunchIntentForPackage(PackageName!)!,
                PendingIntentFlags.UpdateCurrent | (Build.VERSION.SdkInt >= BuildVersionCodes.M ? PendingIntentFlags.Immutable : 0));
            var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? new Notification.Builder(this, ChannelId)
                : new Notification.Builder(this);
            builder.SetSmallIcon(global::MaridewFinance.Android.Resource.Mipmap.ic_launcher)
                .SetContentTitle("Maridew Finance")
                .SetContentText(detail + " - open the app to re-check sync.")
                .SetAutoCancel(true)
                .SetContentIntent(pi);
            ((NotificationManager)GetSystemService(NotificationService)!).Notify(2003, builder.Build());
        }

        private void Notify(string text)
        {
            var pi = PendingIntent.GetActivity(this, 0, PackageManager!.GetLaunchIntentForPackage(PackageName!)!,
                PendingIntentFlags.UpdateCurrent | (Build.VERSION.SdkInt >= BuildVersionCodes.M ? PendingIntentFlags.Immutable : 0));
            Notification.Builder builder;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                builder = new Notification.Builder(this, ChannelId);
            }
            else
            {
                builder = new Notification.Builder(this);
            }
            builder.SetSmallIcon(global::MaridewFinance.Android.Resource.Mipmap.ic_launcher)
                .SetContentTitle("Maridew Finance")
                .SetContentText(text)
                .SetAutoCancel(true)
                .SetContentIntent(pi);
            ((NotificationManager)GetSystemService(NotificationService)!).Notify(2002, builder.Build());
        }

        private void StartForegroundWithText(string text) => PostForeground(text);

        // (Re)posts the ongoing foreground-service notification; calling this
        // again with the same id simply updates its text.
        private void UpdateForegroundText(string text) => PostForeground(text);

        private void PostForeground(string text)
        {
            Notification.Builder builder;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(ChannelId, "Background sync", NotificationImportance.Min);
                channel.Description = "Keeps Maridew Finance synced while the app is closed.";
                ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
                builder = new Notification.Builder(this, ChannelId);
            }
            else
            {
                builder = new Notification.Builder(this);
            }
            builder.SetSmallIcon(global::MaridewFinance.Android.Resource.Mipmap.ic_launcher)
                .SetContentTitle("Maridew Finance")
                .SetContentText(text)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            {
                StartForeground(NotifyId, builder.Build(), ForegroundService.TypeSpecialUse);
            }
            else
            {
                StartForeground(NotifyId, builder.Build());
            }
        }

        /// <summary>Short wakelock so a tick's network work survives Doze.</summary>
        private static class TickWakeLock
        {
            private const string Tag = "maridew:tick";
            private static PowerManager.WakeLock? _wl;

            public static void Acquire(Context ctx)
            {
                try
                {
                    if (_wl == null)
                    {
                        var pm = (PowerManager)ctx.GetSystemService(PowerService)!;
                        _wl = pm.NewWakeLock(WakeLockFlags.Partial, Tag);
                        _wl.SetReferenceCounted(false);
                    }
                    _wl.Acquire(60_000);   // hard cap: a tick should take seconds
                }
                catch (System.Exception ex)
                {
                    global::Android.Util.Log.Warn("maridew", "wakelock acquire failed: " + ex.Message);
                }
            }

            public static void Release(Context ctx)
            {
                try { _wl?.Release(); } catch { /* already released */ }
            }
        }

        /// <summary>JS bridge: the sync promise delivers its JSON here.</summary>
        private class Deliver : Java.Lang.Object
        {
            private readonly SyncService _owner;
            public Deliver(SyncService owner) { _owner = owner; }

            [JavascriptInterface]
            [Export("deliverResult")]
            public void DeliverResult(string? json)
            {
                global::Android.Util.Log.Info("maridew", "deliver called, len=" + (json?.Length ?? 0));
                if (json is null) return;
                var text = json;
                _owner._handler!.Post(() => _owner.OnSyncResult(text));
            }
        }

        private class SyncWebViewClient : WebViewClient
        {
            private readonly WebViewAssetLoader _loader;
            private readonly System.Action _onReady;

            public SyncWebViewClient(WebViewAssetLoader loader, System.Action onReady)
            {
                _loader = loader;
                _onReady = onReady;
            }

            public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
                => _loader.ShouldInterceptRequest(request?.Url);

            public override void OnPageFinished(WebView? view, string? url)
            {
                base.OnPageFinished(view, url);
                _onReady();
            }
        }
    }
}
