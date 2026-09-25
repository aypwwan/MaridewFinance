using Android.App;
using Android.Content;
using Android.Runtime;

namespace MaridewFinance.AndroidApp
{
    /// <summary>
    /// Fired by AlarmManager when the in-process tick loop may be frozen
    /// (Doze, OEM killers). Starts the sync service idempotently - the
    /// service's OnCreate restarts both the loop and the next alarm. If the
    /// process is already alive and ticking, this is a harmless no-op that
    /// just re-anchors the alarm schedule.
    /// </summary>
    [BroadcastReceiver(
        Name = "com.maridew.finance.SyncTickReceiver",
        Enabled = true,
        Exported = false)]
    public class SyncTickReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            try
            {
                if (context is null) return;
                var svc = new Intent(context, typeof(SyncService));
                context.StartForegroundService(svc);
            }
            catch (System.Exception ex)
            {
                global::Android.Util.Log.Warn("maridew", "tick receiver start failed: " + ex.Message);
            }
        }
    }
}
