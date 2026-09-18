using System;
using System.Windows;
using System.Windows.Media;

namespace MaridewFinance.App
{
    public partial class LoginWindow : Window
    {
        private readonly AuthService _auth;
        private bool _createMode;

        public int SignedInUserId { get; private set; }

        public LoginWindow(AuthService auth)
        {
            InitializeComponent();
            _auth = auth;
            StyleLoginMode();
            Loaded += (_, _) => UsernameBox.Focus();
        }

        private void OnSwitchMode(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            _createMode = tag == "create";
            StyleLoginMode();
        }

        private void StyleLoginMode()
        {
            ConfirmLabel.Visibility = ConfirmBox.Visibility = _createMode ? Visibility.Visible : Visibility.Collapsed;
            PrimaryButton.Content = _createMode ? "Create Account" : "Sign In";
            TabSignIn.Background = _createMode ? new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)) : new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
            TabSignIn.Foreground = SystemColors.ControlTextBrush;
            TabCreate.Background = _createMode ? new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5)) : new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
            ShowError(null);
        }

        private void OnPrimary(object sender, RoutedEventArgs e)
        {
            var username = UsernameBox.Text;
            var password = PasswordBox.Password;

            if (_createMode && password != ConfirmBox.Password)
            {
                ShowError("Passwords do not match.");
                return;
            }

            var result = _createMode
                ? _auth.CreateAccount(username, password)
                : _auth.SignIn(username, password);

            if (!result.Ok)
            {
                ShowError(result.Error);
                return;
            }

            if (_createMode)
            {
                // First account on a pre-accounts database adopts existing rows.
                if (_auth.UserCount() == 1)
                {
                    _auth.AssignOrphanData(result.UserId);
                }
            }

            SignedInUserId = result.UserId;
            DialogResult = true;
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
