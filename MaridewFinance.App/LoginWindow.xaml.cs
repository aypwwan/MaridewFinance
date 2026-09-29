using System;
using System.Windows;
using System.Windows.Media;

namespace MaridewFinance.App
{
    public partial class LoginWindow : Window
    {
        private readonly AuthService _auth;

        // signin / create / reset — "reset" is the Forgot Password flow for
        // LOCAL accounts. Cloud sync passwords cannot be reset anywhere: they
        // derive the encryption key, so only a password that hashes to the
        // same key can decrypt the stored data. Reset mode says exactly that
        // once the username is known, instead of letting people think a local
        // reset will also restore their cloud sync.
        private string _mode = "signin";

        private static readonly SolidColorBrush IndigoBrush = new(Color.FromRgb(0x4F, 0x46, 0xE5));
        private static readonly SolidColorBrush TabIdleBrush = new(Color.FromRgb(0x1E, 0x29, 0x3B));
        private static readonly SolidColorBrush TabTextBrush = new(Color.FromRgb(0xCB, 0xD5, 0xE1));
        private static readonly SolidColorBrush RoseBrush = new(Color.FromRgb(0xFB, 0x71, 0x85));
        private static readonly SolidColorBrush EmeraldBrush = new(Color.FromRgb(0x34, 0xD3, 0x99));

        public int SignedInUserId { get; private set; }

        public LoginWindow(AuthService auth)
        {
            InitializeComponent();
            _auth = auth;
            ApplyMode();
            Loaded += (_, _) => UsernameBox.Focus();
        }

        private void OnSwitchMode(object sender, RoutedEventArgs e)
        {
            _mode = (sender as FrameworkElement)?.Tag as string ?? "signin";
            ApplyMode();
        }

        private void OnForgotPassword(object sender, RoutedEventArgs e)
        {
            _mode = "reset";
            ApplyMode();
        }

        private void ApplyMode()
        {
            var creating = _mode == "create";
            var resetting = _mode == "reset";

            ModeHeading.Text = resetting ? "Reset your password" : "";
            ModeSubtext.Text = resetting
                ? "Enter your username and a new password. Your data stays exactly as it is."
                : "";
            ModeHeading.Visibility = ModeSubtext.Visibility = resetting ? Visibility.Visible : Visibility.Collapsed;

            ConfirmLabel.Visibility = ConfirmBox.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;

            PrimaryButton.Content = _mode switch
            {
                "create" => "Create Account",
                "reset" => "Reset Password",
                _ => "Sign In"
            };

            TabSignIn.Background = _mode == "signin" ? IndigoBrush : TabIdleBrush;
            TabSignIn.Foreground = _mode == "signin" ? SystemColors.ControlTextBrush : TabTextBrush;
            TabCreate.Background = _mode == "create" ? IndigoBrush : TabIdleBrush;
            TabCreate.Foreground = _mode == "create" ? SystemColors.ControlTextBrush : TabTextBrush;

            ShowError(null);
        }

        private void OnPrimary(object sender, RoutedEventArgs e)
        {
            var username = UsernameBox.Text;
            var password = PasswordBox.Password;

            if (_mode == "create" && password != ConfirmBox.Password)
            {
                ShowError("Passwords do not match.");
                return;
            }

            if (_mode == "reset")
            {
                if (string.IsNullOrEmpty(password))
                {
                    ShowError("Enter a new password.");
                    return;
                }
                var reset = _auth.ResetPassword(username, password);
                if (!reset.Ok)
                {
                    ShowError(reset.Error);
                    return;
                }

                // Cloud-linked accounts keep their sync session — the cloud
                // password never had to match this one. Explain the boundary
                // instead of pretending sync was reset too.
                string? note = null;
                try
                {
                    if (App.CloudSync != null)
                    {
                        var status = System.Text.Json.JsonDocument.Parse(App.CloudSync.GetStatus()).RootElement;
                        var cloudUser = status.GetProperty("username").GetString();
                        var enabled = status.GetProperty("enabled").GetBoolean();
                        if (enabled && !string.IsNullOrEmpty(cloudUser))
                        {
                            note = $"Password reset. This device is still linked to the cloud account \"{cloudUser}\" — " +
                                   "if that account uses a different password, enter it under Settings > Cloud Sync. " +
                                   "The sync server itself can never reset a password (your data is encrypted with it).";
                        }
                    }
                }
                catch
                {
                    // Status parsing must never block a successful reset.
                }

                if (note != null)
                {
                    ShowInfo(note);
                }
                SignedInUserId = reset.UserId;
                DialogResult = true;
                return;
            }

            var result = _mode == "create"
                ? _auth.CreateAccount(username, password)
                : _auth.SignIn(username, password);

            if (!result.Ok)
            {
                ShowError(result.Error);
                return;
            }

            if (_mode == "create")
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
            ShowMessage(message, RoseBrush);
        }

        private void ShowInfo(string message)
        {
            ShowMessage(message, EmeraldBrush);
        }

        private void ShowMessage(string message, Brush brush)
        {
            ErrorText.Foreground = brush;
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
