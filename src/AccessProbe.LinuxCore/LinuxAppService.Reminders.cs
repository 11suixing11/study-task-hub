namespace AccessProbe.Linux;
public sealed partial class LinuxAppService
{
    private readonly object notificationGate=new();
    private readonly HashSet<string> notificationClaims=new(StringComparer.Ordinal);
    public IReadOnlyList<NotificationView> TakeDueNotifications()
    {
        EnsureOpen();lock(notificationGate)
        {
            var due=store.TakeDue(utcNow());
            foreach(var n in due)notificationClaims.Add(n.Plan.Id);
            return due.Select(n=>new NotificationView(n.Plan.Id,n.Task.Id,ReminderPlanner.KindName(n.Plan.Kind)+" · "+n.Task.Title,
                PlatformCatalog.Name(n.Task.PlatformId)+" · "+n.Task.CourseName+" · "+ToView(n.Task,utcNow()).DeadlineLabel)).ToArray();
        }
    }
    public void AcknowledgeNotifications(IReadOnlyList<string> ids,bool success,string? errorCode=null)
    {
        EnsureOpen();CheckNotificationIds(ids);
        errorCode=NotificationError(success,errorCode);
        lock(notificationGate){var owned=ids.Where(notificationClaims.Contains).ToArray();store.RecordLinuxNotificationRequest(owned,success,errorCode,utcNow());foreach(var id in owned)notificationClaims.Remove(id);}
    }
    public void ReleaseNotifications(IReadOnlyList<string> ids)
    {
        EnsureOpen();CheckNotificationIds(ids);
        lock(notificationGate){var owned=ids.Where(notificationClaims.Contains).ToArray();store.ReleaseReminderClaims(owned);foreach(var id in owned)notificationClaims.Remove(id);}
    }
    private static void CheckNotificationIds(IReadOnlyList<string> ids)
    {if(ids is null||ids.Count>100||ids.Any(id=>id is null||id.Length!=64||!id.All(char.IsAsciiHexDigit)))throw new TaskValidationException("NOTIFY002","提醒标识无效。");}
    private void ReleaseAllNotificationClaims(){lock(notificationGate){store.ReleaseReminderClaims(notificationClaims.ToArray());notificationClaims.Clear();}}
}
