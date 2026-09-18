using System.Windows;

namespace MaridewFinance.App
{
    public partial class App : Application
    {
        public static DbBridge? Bridge { get; private set; }
        public static AuthService? Auth { get; private set; }
        public static UpdateService? Updater { get; private set; }
        public static string? CurrentUserName { get; private set; }

        /// <summary>Set by the bridge when the dashboard requests sign-out-to-login.</summary>
        public static bool ReloginRequested { get; set; }

        /// <summary>Set by the bridge when the dashboard requests the lock screen.</summary>
        public static bool LockRequested { get; set; }

        /// <summary>Set by the lock window when the user chooses to sign out from the lock screen.</summary>
        public static bool LockSignOutRequested { get; set; }

        /// <summary>
        /// Shows the lock screen over the dashboard. The password is verified
        /// by the lock window itself (via AuthService); when cancelled the
        /// dashboard stays open — the user can still close it or lock later.
        /// </summary>
        public static void LaunchLockWindow()
        {
            if (Bridge == null || Bridge.CurrentUserId == 0 || Auth == null) return;
            var lockWindow = new LockWindow(Auth, Bridge.CurrentUserId, CurrentUserName ?? "Account Holder");
            lockWindow.Owner = Current?.MainWindow;
            lockWindow.ShowDialog();

            // "Sign Out" on the lock screen abandons the session: close the
            // dashboard so MainWindow.Closed routes to the login screen. The
            // unlock path leaves the dashboard untouched.
            if (App.LockSignOutRequested)
            {
                Current?.MainWindow?.Close();
            }
        }

        /// <summary>
        /// Re-reads the signed-in user's lock settings (after a restore or
        /// import replaced the database). True when auto-lock is enabled.
        /// </summary>
        public static bool ReapplyLockSettings()
        {
            try
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Bridge!.LoadSettings())
                               ?? new Dictionary<string, string>();
                return settings.TryGetValue("autoLockEnabled", out var enabled)
                    && enabled.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void OnStartup(object sender, StartupEventArgs e)
        {
            Auth = new AuthService();
            Bridge = new DbBridge();
            Updater = new UpdateService();
            ShowLoginAndOpenMain();
        }

        /// <summary>
        /// Shows the login dialog, then opens the dashboard scoped to the
        /// signed-in user. Called at startup and after a sign-out.
        /// </summary>
        public static void ShowLoginAndOpenMain()
        {
            var login = new LoginWindow(Auth!);
            if (login.ShowDialog() != true)
            {
                Current!.Shutdown();
                return;
            }

            Bridge!.SetCurrentUser(login.SignedInUserId);
            CurrentUserName = Auth!.GetUsername(login.SignedInUserId) ?? "Account Holder";

            var main = new MainWindow();
            Current!.MainWindow = main;
            main.Show();
        }

        private void OnExit(object sender, ExitEventArgs e)
        {
            Bridge?.SignOut();
        }
    }
}
