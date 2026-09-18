using System;
using System.Windows;
using System.Windows.Media;

namespace MaridewFinance.App
{
    /// <summary>
    /// Modal lock screen shown when the dashboard auto-locks (idle timeout)
    /// or the user clicks "Lock Now". Verifies the account password without
    /// touching the signed-in session; also offers a full sign-out.
    /// </summary>
    public partial class LockWindow : Window
    {
        private readonly AuthService _auth;
        private readonly int _userId;

        public LockWindow(AuthService auth, int userId, string username)
        {
            InitializeComponent();
            _auth = auth;
            _userId = userId;
            LockedUserText.Text = $"Signed in as {username}";
            Loaded += (_, _) => PasswordBox.Focus();
        }

        private void OnUnlock(object sender, RoutedEventArgs e)
        {
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                ShowError("Enter your password to unlock.");
                return;
            }

            if (_auth.VerifyPassword(_userId, password))
            {
                ShowError(null);
                DialogResult = true;
                return;
            }

            ShowError("Incorrect password. Try again.");
            PasswordBox.Clear();
            PasswordBox.Focus();
        }

        private void OnSignOut(object sender, RoutedEventArgs e)
        {
            // Abandon the session entirely: closes the dashboard, returns to
            // the login screen (mirrors the dashboard's sign-out flow).
            DialogResult = false;
            App.LockSignOutRequested = true;
        }

        private void ShowError(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                ErrorText.Visibility = Visibility.Collapsed;
                return;
            }
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
