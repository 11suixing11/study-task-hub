using Android.App;
using Android.Content;
using Android.OS;
using AccessProbe;

namespace AccessProbe.Mobile;

public static class MobileReminderScheduler
{
    public const string Channel = "learning_tasks";
    public static void Schedule(Context context)
    {
        using var manager = (AlarmManager?)context.GetSystemService(Context.AlarmService);
        using var intent = new Intent(context, typeof(MobileReminderReceiver));
        using var pending = PendingIntent.GetBroadcast(context, 8001, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        if (manager is null || pending is null) return;
        // Android batches this alarm in the background. The app checks saved plans when awakened.
        manager.SetInexactRepeating(AlarmType.RtcWakeup, Java.Lang.JavaSystem.CurrentTimeMillis() + 60_000, 15 * 60_000, pending);
        var notifications = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        notifications?.CreateNotificationChannel(new NotificationChannel(Channel, "课程任务提醒", NotificationImportance.Default));
    }
    public static void Check(Context context)
    {
        var store = MobileData.Open(context); var due = store.TakeDue(DateTimeOffset.UtcNow);
        if (due.Count == 0) return;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        var available = manager?.AreNotificationsEnabled() == true;
        if (!available) { store.ReleaseReminderClaims(due.Select(item => item.Plan.Id).ToArray()); return; }
        foreach (var item in due)
        {
            var success = false;
            try
            {
                using var open = new Intent(context, typeof(MainActivity));
                using var pending = PendingIntent.GetActivity(context, 8002, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                using var builder = new Notification.Builder(context, Channel);
                using var notification = builder.SetSmallIcon(Resource.Drawable.ic_notification)!.SetContentTitle(item.Task.Title)!
                    .SetContentText(PlatformCatalog.Name(item.Task.PlatformId) + " · " + ReminderPlanner.KindName(item.Plan.Kind))!
                    .SetAutoCancel(true)!.SetContentIntent(pending)!.Build();
                manager!.Notify(item.Plan.Id.GetHashCode(StringComparison.Ordinal), notification); success = true;
            }
            catch { }
            store.RecordNotificationRequest([item.Plan.Id], success, DateTimeOffset.UtcNow);
        }
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class MobileReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return; var pending = GoAsync();
        _ = System.Threading.Tasks.Task.Run(() => { try { MobileReminderScheduler.Check(context); } finally { pending?.Finish(); } });
    }
}

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted])]
public sealed class MobileBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent) { if (context is not null && intent?.Action == Intent.ActionBootCompleted) MobileReminderScheduler.Schedule(context); }
}
