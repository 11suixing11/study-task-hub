using AccessProbe;
using AccessProbe.Linux;

namespace LinuxChecks;
static class TaskServiceChecks
{
    public static Task RunAsync()
    {
        using var root = new TestRoot();
        var now = new DateTimeOffset(2026,11,13,12,0,0,TimeSpan.Zero);
        using var service = new LinuxAppService(root.Path,()=>now,TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
        var input = new TaskInput("线代作业","chaoxing","数学","known",null,"2026-11-14T20:00:00+08:00","https://example.invalid/work",null,"","原备注",null);
        var created = service.CreateTask(input);
        Check.Equal(TaskSource.Manual,created.View.Task.Source);
        Check.Equal(now.AddDays(1),created.View.Task.DeadlineUtc!.Value);
        Check.True(created.View.DeadlineLabel.Contains("2026-11-14 20:00"));
        var edited=service.EditTask(created.View.Task.Id,created.View.Task.Revision,input with { Notes="修订备注" });
        Check.Equal("TASK004",Check.Throws<TaskValidationException>(()=>service.EditTask(created.View.Task.Id,created.View.Task.Revision,input)).Code);
        Check.Throws<TaskValidationException>(()=>service.DeleteTask(created.View.Task.Id,created.View.Task.Revision));
        using var restarted = new LinuxAppService(root.Path,()=>now);
        Check.Equal("修订备注",restarted.GetTask(created.View.Task.Id).View.Task.Notes);
        Check.Equal(1,restarted.QueryTasks(new(Search:"线代")).FilteredTotal);
        Check.Equal(0,restarted.QueryTasks(new(Search:"物理")).FilteredTotal);
        Check.Equal(1,restarted.QueryTasks(new()).Summary.DueSoon);
        Check.Throws<TaskValidationException>(()=>service.CreateTask(input with { Title=" " }));
        Check.Throws<TaskValidationException>(()=>service.CreateTask(input with { Title=new string('a',301) }));
        Check.Throws<TaskValidationException>(()=>service.CreateTask(input with { ProgressPercent=101 }));
        Check.Throws<TaskValidationException>(()=>service.CreateTask(input with { DeadlineIso="2026-11-14T20:00:00" }));
        Check.Throws<TaskValidationException>(()=>service.CreateTask(input with { DeadlineIso=null }));
        Check.Throws<TaskValidationException>(()=>service.CreateTask(input with { BeginIso="2026-12-14T20:00:00Z" }));
        Check.Throws<TaskValidationException>(()=>service.QueryTasks(new(State:"garbage")));
        Check.Throws<TaskValidationException>(()=>service.QueryTasks(new(Limit:101)));
        var completed=service.SetTaskState(edited.View.Task.Id,edited.View.Task.Revision,"completed");
        Check.Equal(1,service.QueryTasks(new()).Summary.Completed);
        Check.Equal(0,service.QueryTasks(new()).Summary.DueSoon);
        service.DeleteTask(completed.View.Task.Id,completed.View.Task.Revision);
        Check.Equal(0,restarted.QueryTasks(new()).Total);
        service.SaveSettings(new(false) {AutomaticSyncEnabled=false});
        Check.True(!restarted.LoadSettings().NotificationsEnabled && !restarted.LoadSettings().AutomaticSyncEnabled);
        var large=service.CreateTask(input with {Title=new string('字',300), DeadlineKind="unknown",DeadlineIso=null});
        Check.Equal(300,large.View.Task.Title.Length);
        var before=service.QueryTasks(new()).SnapshotVersion;
        service.CreateTask(input with {Title="其他平台",PlatformId="pta"});
        Check.True(service.QueryTasks(new()).SnapshotVersion>before);
        Check.Equal(1,service.QueryTasks(new(Platform:"pta")).FilteredTotal);
        for(var i=0;i<5000;i++)service.CreateTask(input with {Title="分页任务 "+i,DeadlineKind="none",DeadlineIso=null,Link=null});
        var first=service.QueryTasks(new(Search:"分页任务",Sort:"updated"));
        var second=service.QueryTasks(new(Search:"分页任务",Sort:"updated",Offset:100));
        Check.Equal(5000,first.FilteredTotal); Check.Equal(100,first.Items.Count);
        Check.True(!first.Items.Select(t=>t.Task.Id).Intersect(second.Items.Select(t=>t.Task.Id)).Any());
        Check.Equal(5001,first.PlatformCounts["chaoxing"]);
        return Task.CompletedTask;
    }
}
