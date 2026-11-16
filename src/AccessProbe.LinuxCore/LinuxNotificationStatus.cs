namespace AccessProbe.Linux;
public sealed record NotificationHistoryView(string Id,string TaskId,string Title,string Kind,string State,int Attempts,DateTimeOffset? AcceptedAtUtc,string? ErrorCode);
public sealed record NotificationTestResult(bool Accepted,string? ErrorCode,DateTimeOffset AtUtc);
public sealed record NotificationStatusPage(bool Enabled,NotificationTestResult? Test,IReadOnlyList<NotificationHistoryView> History);
public sealed partial class LinuxAppService
{
    public NotificationStatusPage GetNotificationStatus()
    {
        EnsureOpen();var tasks=store.LoadTasks().ToDictionary(t=>t.Id);
        return new(store.LoadSettings().NotificationsEnabled,store.LoadLinuxNotificationTest(),store.LoadReminderHistory().Select(h=>new NotificationHistoryView(
            h.Plan.Id,h.Plan.TaskId,tasks.GetValueOrDefault(h.Plan.TaskId)?.Title??"历史任务",ReminderPlanner.KindName(h.Plan.Kind),h.State,h.Attempts,h.RequestedAtUtc,h.ErrorCode)).ToArray());
    }
    public int RetryFailedNotifications(){EnsureOpen();return store.RetryLinuxNotifications();}
    public void RecordNotificationTest(bool accepted,string? errorCode){EnsureOpen();store.SaveLinuxNotificationTest(new(accepted,NotificationError(accepted,errorCode),utcNow()));}
    private static string? NotificationError(bool success,string? code)
    {
        if(success)return null;
        if(code is null)return "NOTIFY001";
        if(code is not("NOTIFY001" or "NOTIFY003" or "NOTIFY004"))throw new TaskValidationException("NOTIFY002","通知结果无效。");
        return code;
    }
}
