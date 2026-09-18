using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MaridewFinance.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;

            // Closing the dashboard signs out; it either returns to the
            // login screen (sign-out button) or exits the app (window X).
            // A sign-out from the lock screen lands here the same way.
            Closed += (_, _) =>
            {
                App.Bridge?.SignOut();
                if (App.LockSignOutRequested)
                {
                    App.LockSignOutRequested = false;
                    App.LockRequested = false;
                    App.ShowLoginAndOpenMain();
                }
                else if (App.ReloginRequested)
                {
                    App.ReloginRequested = false;
                    App.ShowLoginAndOpenMain();
                }
                else
                {
                    Application.Current.Shutdown();
                }
            };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Store the WebView2 user data folder next to the app so the app
                // does not require write access to Program Files.
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MaridewFinance", "WebView2");

                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                await Browser.EnsureCoreWebView2Async(env);

                // Expose the app's SQLite bridge (scoped to the signed-in user)
                // and the auto-update service to the dashboard JavaScript as
                // window.chrome.webview.hostObjects.dbBridge / .updateBridge
                Browser.CoreWebView2.AddHostObjectToScript("dbBridge", App.Bridge!);
                Browser.CoreWebView2.AddHostObjectToScript("updateBridge", App.Updater!);

                // Load the bundled dashboard (copied to the output directory as wwwroot/index.html)
                var htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");

                if (File.Exists(htmlPath))
                {
                    Browser.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    MessageBox.Show(
                        $"Could not find the application UI at:\n{htmlPath}\n\n" +
                        "Make sure the wwwroot folder is copied to the output directory.",
                        "Maridew Finance - Startup Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }

                Browser.CoreWebView2.NavigationCompleted += (s, args) => LoadingText.Visibility = Visibility.Collapsed;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show(
                    "The Microsoft Edge WebView2 Runtime is required to run this application.\n\n" +
                    "Download it from:\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                    "WebView2 Runtime Missing",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start Maridew Finance:\n{ex.Message}",
                    "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
