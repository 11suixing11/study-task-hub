using AccessProbe;
using AccessProbe.Linux;
using PlatformMetadataTestData;
namespace LinuxChecks;
static class BackupChecks
{
    public static Task RunAsync()
    {
        using var root=new TestRoot();using var service=new LinuxAppService(root.Path);
        var input=new TaskInput("备份任务","pta","数学","unknown",null,null,"https://pintia.cn/course-class",null,"","原备注",null);
        var task=service.CreateTask(input).View.Task;
        service.CaptureBrowserSession(new("pta","https://pintia.cn/course-class",[PtaFixtures.Cookie()],null,null,null));
        var path=Path.Combine(root.Path,"test.studybackup");const string password="test-password-only";
        var backup=service.CreateBackup(path,password);Check.Equal(1,backup.TaskCount);Check.Equal(1,backup.SessionCount);
        service.EditTask(task.Id,task.Revision,input with{Notes="备份后修改"});
        Check.Equal("BACKUP003",Check.Throws<TaskValidationException>(()=>service.InspectBackup(path,"wrong-password")).Code);
        Check.Equal("备份后修改",service.GetTask(task.Id).View.Task.Notes);
        var preview=service.InspectBackup(path,password);Check.Equal(backup.Fingerprint,preview.Fingerprint);
        var profiles=Path.Combine(root.Path,"profiles");Directory.CreateDirectory(profiles);File.WriteAllText(Path.Combine(profiles,"local-fixture"),"old browser cache");
        var result=service.RestoreBackup(path,password,preview.Fingerprint);
        Check.Equal("原备注",service.GetTask(task.Id).View.Task.Notes);Check.Equal(input.Link,service.GetTask(task.Id).Link);
        Check.True(service.GetBrowserSession("pta") is not null);Check.True(File.Exists(result.RollbackBackupPath));
        Check.True(!Directory.Exists(profiles));Check.True(File.Exists(Path.Combine(result.RollbackDirectory,"profiles","local-fixture")));
        Check.Throws<TaskValidationException>(()=>service.CreateBackup(path,password));
        var corrupt=File.ReadAllBytes(path);corrupt[^1]^=1;var bad=Path.Combine(root.Path,"bad.studybackup");File.WriteAllBytes(bad,corrupt);File.SetUnixFileMode(bad,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        Check.Equal("BACKUP003",Check.Throws<TaskValidationException>(()=>service.RestoreBackup(bad,password,preview.Fingerprint)).Code);
        Check.Equal("原备注",service.GetTask(task.Id).View.Task.Notes);
        var other=service.CreateBackup(Path.Combine(root.Path,"other.studybackup"),password);
        Check.Equal("BACKUP004",Check.Throws<TaskValidationException>(()=>service.RestoreBackup(other.Path,password,preview.Fingerprint)).Code);
        using var reminderRoot=new TestRoot();var now=DateTimeOffset.UtcNow;
        using(var reminderService=new LinuxAppService(reminderRoot.Path,()=>now))
        {
            reminderService.CreateTask(new("领取中的提醒","pta","","known",null,now.AddHours(1).ToString("O"),null,null,"","",null));
            Check.True(reminderService.TakeDueNotifications().Count>0);
            var claimed=reminderService.CreateBackup(Path.Combine(reminderRoot.Path,"claimed.studybackup"),password);
            reminderService.RestoreBackup(claimed.Path,password,claimed.Fingerprint);
            Check.True(reminderService.TakeDueNotifications().Count>0,"Restored transient leases blocked reminders");
        }
        using var crashRoot=new TestRoot();string originalId;
        using(var original=new LinuxAppService(crashRoot.Path))originalId=original.CreateTask(input).View.Task.Id;
        var operation=Path.Combine(crashRoot.Path,".restore-"+new string('A',32));var rollback=Path.Combine(operation,"rollback");LinuxDataPaths.EnsurePrivateDirectory(operation);LinuxDataPaths.EnsurePrivateDirectory(rollback);
        File.Copy(Path.Combine(crashRoot.Path,"tasks.db"),Path.Combine(rollback,"tasks.db"));File.Copy(Path.Combine(crashRoot.Path,"protection.key"),Path.Combine(rollback,"protection.key"));
        File.WriteAllText(Path.Combine(crashRoot.Path,".restore-journal"),Path.GetFileName(operation));
        File.WriteAllBytes(Path.Combine(crashRoot.Path,"tasks.db"),[1,2,3]);
        using(var recovered=new LinuxAppService(crashRoot.Path))Check.Equal(input.Link,recovered.GetTask(originalId).Link);
        Check.True(!File.Exists(Path.Combine(crashRoot.Path,".restore-journal")));
        return Task.CompletedTask;
    }
}
