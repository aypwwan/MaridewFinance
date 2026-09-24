using Android.App;
using Android.Content;
using Android.Runtime;

namespace MaridewFinance.AndroidApp
{
    /// <summary>Restarts background sync after the device reboots.</summary>
    [BroadcastReceiver(
        Name = "com.maridew.finance.BootReceiver",
        Enabled = true,
        Exported = true,
        Permission = "android.permission.RECEIVE_BOOT_COMPLETED")]
    [IntentFilter(new[] { Intent.ActionBootCompleted })]
    [Register("com.maridew.finance.BootReceiver")]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != Intent.ActionBootCompleted || context is null) return;
            try
            {
                context.StartForegroundService(new Intent(context, typeof(SyncService)));
            }
            catch
            {
                // Some OEMs throttle boot receivers; the service also starts
                // whenever the user next opens the app.
            }
        }
    }
}
