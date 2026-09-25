using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using Android.OS;
using Android.Views;
using Android.Webkit;
using AndroidX.AppCompat.App;
using AndroidX.WebKit;

namespace MaridewFinance.AndroidApp
{
    /// <summary>
    /// Hosts the shared Maridew dashboard (the exact wwwroot the desktop app
    /// and the GitHub Pages edition use) inside the system WebView.
    ///
    /// androidx.webkit.WebViewAssetLoader serves the bundled assets over a real
    /// https:// appassets.androidplatform.net origin, so IndexedDB and
    /// WebCrypto (cloud-sync encryption) work exactly as in the browser.
    /// </summary>
    [Activity(
        Label = "@string/app_name",
        Icon = "@mipmap/ic_launcher",
        Theme = "@style/MaridewTheme",
        MainLauncher = true,
        Name = "com.maridew.finance.MainActivity",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
        WindowSoftInputMode = SoftInput.AdjustResize)]
    [Register("com.maridew.finance.MainActivity")]
    public class MainActivity : AppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var assetLoaderBuilder = new WebViewAssetLoader.Builder();
            assetLoaderBuilder.AddPathHandler("/assets/", new WebViewAssetLoader.AssetsPathHandler(this));
            var assetLoader = assetLoaderBuilder.Build()!;

            var webView = new WebView(this);
#if DEBUG
            // Lets automated tests drive the page via Chrome DevTools over adb
            // (chrome_devtools_remote). Stripped from release builds.
            WebView.SetWebContentsDebuggingEnabled(true);
#endif
            var settings = webView.Settings!;
            settings.JavaScriptEnabled = true;
            settings.DomStorageEnabled = true;          // IndexedDB + localStorage (web-bridge storage)
            settings.DatabaseEnabled = true;
            settings.AllowFileAccess = false;           // assets go through the https origin instead
            settings.AllowContentAccess = false;
            settings.CacheMode = CacheModes.Default;
            settings.MediaPlaybackRequiresUserGesture = false;
            settings.MixedContentMode = MixedContentHandling.NeverAllow;

            // Cookies persist sign-in state for the sync worker fetches.
            CookieManager.Instance!.SetAcceptCookie(true);
            CookieManager.Instance!.SetAcceptThirdPartyCookies(webView, false);

            webView.SetWebViewClient(new MaridewWebViewClient(this, assetLoader));
            webView.SetWebChromeClient(new WebChromeClient());

            // Keep sync alive while the app is closed (idempotent: the system
            // delivers to the existing running service).
            try { StartForegroundService(new Intent(this, typeof(SyncService))); }
            catch { /* e.g. foreground-service restrictions; the app still syncs while open */ }

            // One-time, polite prompt to exempt the app from battery
            // optimization so the 5-minute tick survives Doze and OEM savers.
            PromptBatteryOptimizationOnce();

            // Assets are embedded under Assets/wwwroot (see the csproj Link), so
            // the /assets/ handler maps /assets/wwwroot/* onto them.
            webView.LoadUrl("https://appassets.androidplatform.net/assets/wwwroot/index.html");

            SetContentView(webView);
        }

        /// <summary>
        /// Android (and especially OEM skins) freeze or kill background
        /// services to save power. The system dialog lets the user opt out for
        /// THIS app only; we ask once and remember the answer in prefs.
        /// </summary>
        private void PromptBatteryOptimizationOnce()
        {
            try
            {
                var pm = (PowerManager)GetSystemService(PowerService)!;
                var prefs = GetSharedPreferences("maridew", FileCreationMode.Private)!;
                if (!pm.IsIgnoringBatteryOptimizations(PackageName) &&
                    prefs.GetBoolean("batteryPrompted", false) == false)
                {
                    prefs.Edit()!.PutBoolean("batteryPrompted", true)!.Apply();
                    StartActivity(new Intent(global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings));
                }
            }
            catch (System.Exception ex)
            {
                // A missing settings screen on some OEMs must never break launch.
                global::Android.Util.Log.Warn("maridew", "battery prompt failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Routes every request through WebViewAssetLoader (https origin) and
        /// keeps external links (docs, sync server) out of the app WebView.
        /// </summary>
        private sealed class MaridewWebViewClient : WebViewClient
        {
            private readonly Activity _activity;
            private readonly WebViewAssetLoader _assetLoader;

            public MaridewWebViewClient(Activity activity, WebViewAssetLoader assetLoader)
            {
                _activity = activity;
                _assetLoader = assetLoader;
            }

            public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
                => _assetLoader.ShouldInterceptRequest(request?.Url);

            public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
            {
                var url = request?.Url?.ToString() ?? string.Empty;
                // Keep the asset origin inside; open anything else externally.
                if (url.StartsWith("https://appassets.androidplatform.net/", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                try
                {
                    var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
                    _activity.StartActivity(intent);
                }
                catch (ActivityNotFoundException)
                {
                    // Nothing on the device can open it - ignore.
                }
                return true;
            }
        }
    }
}
