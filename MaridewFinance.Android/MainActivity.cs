using Android.App;
using Android.Content;
using Android.Content.PM;
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
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
        WindowSoftInputMode = SoftInput.AdjustResize)]
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
            // Assets are embedded under Assets/wwwroot (see the csproj Link), so
            // the /assets/ handler maps /assets/wwwroot/* onto them.
            webView.LoadUrl("https://appassets.androidplatform.net/assets/wwwroot/index.html");

            SetContentView(webView);
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
