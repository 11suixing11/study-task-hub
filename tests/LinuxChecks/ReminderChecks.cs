using AccessProbe.Linux;
namespace LinuxChecks;
static class ReminderChecks
{
    public static Task RunAsync()
    {
        using var root=new TestRoot();var now=DateTimeOffset.Parse("2026-11-13T12:00:00Z");
        using var service=new LinuxAppService(root.Path,()=>now);
        var input=new TaskInput("到期提醒","pta","","known",null,"2026-11-14T12:00:00Z",null,null,"","",null);
        var task=service.CreateTask(input).View.Task;
        var due=service.TakeDueNotifications();Check.Equal(1,due.Count);
        Check.Equal(task.Id,due[0].TaskId);
        Check.Equal(0,service.TakeDueNotifications().Count);
        service.AcknowledgeNotifications(due.Select(n=>n.Id).ToArray(),false);
        Check.Equal(0,service.TakeDueNotifications().Count);
        now=now.AddMinutes(2);Check.Equal(1,service.TakeDueNotifications().Count);
        service.ReleaseNotifications(due.Select(n=>n.Id).ToArray());
        service.SetTaskState(task.Id,task.Revision,"completed");
        Check.Equal(0,service.TakeDueNotifications().Count);
        service.SaveSettings(service.LoadSettings() with{NotificationsEnabled=false});
        service.CreateTask(input with{Title="关闭提醒"});
        Check.Equal(0,service.TakeDueNotifications().Count);
        using var statusRoot=new TestRoot();using var statusService=new LinuxAppService(statusRoot.Path,()=>now);
        statusService.CreateTask(input with{DeadlineIso=now.AddHours(24).ToString("O")});
        var statusDue=statusService.TakeDueNotifications();
        statusService.AcknowledgeNotifications(statusDue.Select(n=>n.Id).ToArray(),false,"NOTIFY003");
        Check.Equal("NOTIFY003",statusService.GetNotificationStatus().History.Single(h=>h.Id==statusDue[0].Id).ErrorCode);
        Check.Equal(1,statusService.RetryFailedNotifications());
        Check.Equal(1,statusService.TakeDueNotifications().Count);
        statusService.AcknowledgeNotifications(statusDue.Select(n=>n.Id).ToArray(),true);
        Check.Equal("requested",statusService.GetNotificationStatus().History.Single(h=>h.Id==statusDue[0].Id).State);
        Check.Equal(0,statusService.RetryFailedNotifications());
        statusService.RecordNotificationTest(false,"NOTIFY004");
        Check.Equal("NOTIFY004",statusService.GetNotificationStatus().Test!.ErrorCode);
        return Task.CompletedTask;
    }
}
