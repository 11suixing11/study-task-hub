using Android.App;
using Android.Content;
using Android.OS;
using AccessProbe;

namespace AccessProbe.Mobile;

public static class MobileReminderScheduler
{
    public const string Channel = "learning_tasks";
    private const int TestNotificationId = -8002;
    internal static Intent CreateOpenIntent(Context context, string taskId)
    {
        var intent = new Intent(context, typeof(MainActivity));
        intent.PutExtra("task_id", taskId); intent.SetData(Android.Net.Uri.Parse("aggregator-task://local/" + taskId));
        return intent;
    }
    public static void Schedule(Context context)
    {
        EnsureChannel(context);
        using var manager = (AlarmManager?)context.GetSystemService(Context.AlarmService);
        using var intent = new Intent(context, typeof(MobileReminderReceiver));
        using var pending = PendingIntent.GetBroadcast(context, 8001, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        if (manager is null || pending is null) return;
        // Android batches this alarm in the background. The app checks saved plans when awakened.
        manager.SetInexactRepeating(AlarmType.RtcWakeup, Java.Lang.JavaSystem.CurrentTimeMillis() + 60_000, 15 * 60_000, pending);
    }

    private static void EnsureChannel(Context context)
    {
        using var notifications = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        using var channel = new NotificationChannel(Channel, "课程任务提醒", NotificationImportance.Default);
        // Creating the same channel preserves the user's existing channel settings, including a disabled channel.
        notifications?.CreateNotificationChannel(channel);
    }

    public static MobileNotificationStatus ReadStatus(Context context)
    {
        try
        {
            using var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            return ReadStatus(context, manager);
        }
        catch { return MobileNotificationStatus.For(MobileNotificationStatusKind.Unavailable); }
    }

    private static MobileNotificationStatus ReadStatus(Context context, NotificationManager? manager)
    {
        try
        {
            var permitted = Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu
                || context.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) == Android.Content.PM.Permission.Granted;
            using var channel = manager?.GetNotificationChannel(Channel);
            return MobileNotificationStatus.FromSignals(permitted, manager?.AreNotificationsEnabled() == true,
                channel is not null, channel?.Importance != NotificationImportance.None, manager is not null);
        }
        catch { return MobileNotificationStatus.For(MobileNotificationStatusKind.Unavailable); }
    }

    public static Intent CreateSystemSettingsIntent(Context context, bool channel = false)
    {
        var intent = new Intent(channel ? Android.Provider.Settings.ActionChannelNotificationSettings
            : Android.Provider.Settings.ActionAppNotificationSettings);
        intent.PutExtra(Android.Provider.Settings.ExtraAppPackage, context.PackageName);
        if (channel) intent.PutExtra(Android.Provider.Settings.ExtraChannelId, Channel);
        return intent;
    }

    public static Intent CreateApplicationSettingsIntent(Context context) =>
        new(Android.Provider.Settings.ActionApplicationDetailsSettings, Android.Net.Uri.Parse("package:" + context.PackageName));

    public static MobileNotificationRequestResult RequestTestNotification(Context context)
    {
        try
        {
            EnsureChannel(context);
            using var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            return MobileNotificationRequests.Attempt(ReadStatus(context, manager), () =>
            {
                using var open = new Intent(context, typeof(MainActivity));
                using var pending = PendingIntent.GetActivity(context, TestNotificationId, open,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                if (pending is null) throw new InvalidOperationException("notification_intent_unavailable");
                using var builder = new Notification.Builder(context, Channel);
                using var notification = builder.SetSmallIcon(Resource.Drawable.ic_notification)!
                    .SetContentTitle("聚合平台 · 提醒测试")!
                    .SetContentText("看到这条通知表示目前允许接收课程任务提醒。后台提醒仍受系统调度影响。")!
                    .SetAutoCancel(true)!.SetContentIntent(pending)!.Build();
                manager!.Notify(TestNotificationId, notification);
            }, true);
        }
        catch { return new(MobileNotificationRequestOutcome.Failed, "测试提醒未能交给系统，请检查系统通知设置后重试。"); }
    }

    public static void Check(Context context)
    {
        var store = MobileData.Open(context);
        using var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        CheckStoredReminders(store, DateTimeOffset.UtcNow, () => ReadStatus(context, manager), item =>
        {
            using var open = CreateOpenIntent(context, item.Task.Id);
            using var pending = PendingIntent.GetActivity(context, item.Plan.Id.GetHashCode(StringComparison.Ordinal), open,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            if (pending is null) throw new InvalidOperationException("notification_intent_unavailable");
            using var builder = new Notification.Builder(context, Channel);
            using var notification = builder.SetSmallIcon(Resource.Drawable.ic_notification)!.SetContentTitle(item.Task.Title)!
                .SetContentText(PlatformCatalog.Name(item.Task.PlatformId) + " · " + ReminderPlanner.KindName(item.Plan.Kind))!
                .SetAutoCancel(true)!.SetContentIntent(pending)!.Build();
            manager!.Notify(item.Plan.Id.GetHashCode(StringComparison.Ordinal), notification);
        });
    }

    // The isolated Android checks inject system signals and a request callback; they never post a notification.
    internal static void CheckStoredReminders(TaskStore store, DateTimeOffset now,
        Func<MobileNotificationStatus> readStatus, Action<DueReminder> request)
    {
        var due = store.TakeDue(now);
        for (var i = 0; i < due.Count; i++)
        {
            MobileNotificationStatus availability;
            try { availability = readStatus(); }
            catch { availability = MobileNotificationStatus.For(MobileNotificationStatusKind.Unavailable); }
            if (!availability.CanRequest)
            {
                store.ReleaseReminderClaims(due.Skip(i).Select(item => item.Plan.Id).ToArray());
                return;
            }
            var item = due[i];
            var result = MobileNotificationRequests.Attempt(availability, () => request(item));
            // A permission/channel change during the request must leave the plan eligible for retry.
            try { availability = readStatus(); }
            catch { availability = MobileNotificationStatus.For(MobileNotificationStatusKind.Unavailable); }
            if (!availability.CanRequest)
            {
                store.ReleaseReminderClaims(due.Skip(i).Select(remaining => remaining.Plan.Id).ToArray());
                return;
            }
            store.RecordNotificationRequest([item.Plan.Id], result.Requested, now);
        }
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class MobileReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return; var pending = GoAsync();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { MobileReminderScheduler.Check(context); }
            catch { Android.Util.Log.Warn("LearningReminders", "reminder_check_failed"); }
            finally { pending?.Finish(); }
        });
    }
}

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted])]
public sealed class MobileBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent) { if (context is not null && intent?.Action == Intent.ActionBootCompleted) MobileReminderScheduler.Schedule(context); }
}
