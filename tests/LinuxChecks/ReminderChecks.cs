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
        return Task.CompletedTask;
    }
}
