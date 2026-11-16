using AccessProbe;
using AccessProbe.Linux;
namespace LinuxChecks;
static class ManualCourseChecks
{
    public static Task RunAsync()
    {
        using var root=new TestRoot();var now=DateTimeOffset.Parse("2026-11-14T10:00:00+08:00");
        using var service=new LinuxAppService(root.Path,()=>now);
        var plan=service.GetCourseSelectionPlan("pta");
        service.SaveReviewedCourseSelection(new("pta",plan.Revision,plan.Semester,[],[new("数学")]));
        var legacy=service.CreateTask(new("旧任务","pta","数学","unknown",null,null,null,null,"","",null));
        var page=service.GetManualCourses("pta");var key=page.Courses.Single().Key;
        page=service.UpdateManualCourse(new("pta",key,page.Revision,"高等数学",new(2026,"spring"),false));
        var task=service.GetTask(legacy.View.Task.Id).View.Task;
        Check.Equal(key,task.RecordingCourseKey);Check.Equal("高等数学",task.CourseName);Check.Equal(legacy.View.Task.Revision+1,task.Revision);
        Check.Equal(new AcademicSemester(2026,"spring"),page.Courses.Single().Semester);
        Check.Throws<TaskValidationException>(()=>service.EditTask(task.Id,legacy.View.Task.Revision,new("旧编辑","pta","数学","unknown",null,null,null,null,"","",null)));
        var bound=service.CreateTask(new("绑定任务","pta","错误旧名称","unknown",null,null,null,null,"","",null){RecordingCourseKey=key});
        Check.Equal("高等数学",bound.View.Task.CourseName);Check.Equal(key,bound.View.Task.RecordingCourseKey);
        Check.Equal("COURSE003",Check.Throws<TaskValidationException>(()=>service.RemoveManualCourse("pta",key,page.Revision)).Code);
        page=service.UpdateManualCourse(new("pta",key,page.Revision,"高等数学",new(2026,"spring"),true));
        Check.Equal(0,service.QueryTasks(new()).Total);Check.Equal(2,service.QueryTasks(new(IncludeUnselected:true)).Total);
        Check.True(!service.GetCourseSelectionPlan("pta").Courses.Any(c=>c.Course.Key==key));
        task=service.GetTask(task.Id).View.Task;
        var archivedEdit=service.EditTask(task.Id,task.Revision,new(task.Title,"pta",task.CourseName,"unknown",null,null,null,null,"","归档备注",null));
        Check.Equal(key,archivedEdit.View.Task.RecordingCourseKey);
        Check.Throws<TaskValidationException>(()=>service.CreateTask(new("归档禁止","pta","","unknown",null,null,null,null,"","",null){RecordingCourseKey=key}));
        page=service.UpdateManualCourse(new("pta",key,page.Revision,"高等数学",new(2026,"spring"),false));
        Check.True(!page.Courses.Single().Archived);Check.Equal(2,page.Courses.Single().TaskCount);
        plan=service.GetCourseSelectionPlan("pta");
        service.SaveReviewedCourseSelection(new("pta",plan.Revision,plan.Semester,[key],[new("数学")]));
        page=service.GetManualCourses("pta");var empty=page.Courses.Single(c=>c.Name=="数学");
        Check.True(empty.Key!=key,"Renamed course identity was reused by a newly added course");
        page=service.RemoveManualCourse("pta",empty.Key,page.Revision);Check.Equal(1,page.Courses.Count);
        Check.Throws<TaskValidationException>(()=>service.UpdateManualCourse(new("pta",key,page.Revision-1,"不应保存",new(2026,"spring"),false)));
        using var restarted=new LinuxAppService(root.Path,()=>now);
        Check.Equal(key,restarted.GetManualCourses("pta").Courses.Single().Key);Check.Equal("高等数学",restarted.GetTask(task.Id).View.Task.CourseName);
        using var legacyRoot=new TestRoot();using var legacyService=new LinuxAppService(legacyRoot.Path,()=>now);using var protector=new LinuxDataProtector(legacyRoot.Path);
        var oldStore=new TaskStore(Path.Combine(legacyRoot.Path,"tasks.db"),protector);
        var oldTask=oldStore.Create(new("旧版手动任务","fif","口语课",DeadlineKind.Known,null,now.AddHours(1),null,null,"","",null),now);
        var oldKey=CourseRecordingRules.TaskCourseKey(oldTask);
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldStore.DatabasePath}"))
        {
            connection.Open();using var command=connection.CreateCommand();
            command.CommandText="INSERT INTO settings(key,value) VALUES('course-recording:fif',$value);";
            command.Parameters.AddWithValue("$value",System.Text.Json.JsonSerializer.Serialize(new CourseRecordingSettings{SelectionConfirmed=true,HideUnselectedTasks=true,SelectedCourseKeys=[oldKey],Courses=[new(oldKey,"口语课","自定义 · 2026-2027学年上学期",false)]}));command.ExecuteNonQuery();
        }
        var oldPage=legacyService.GetManualCourses("fif");
        Check.Equal(oldKey,oldPage.Courses.Single().Key);Check.Equal(new AcademicSemester(2026,"autumn"),oldPage.Courses.Single().Semester);
        oldPage=legacyService.UpdateManualCourse(new("fif",oldKey,oldPage.Revision,"高级口语",new(2026,"autumn"),true));
        Check.Equal(oldKey,legacyService.GetTask(oldTask.Id).View.Task.RecordingCourseKey);Check.Equal(0,legacyService.TakeDueNotifications().Count);
        return Task.CompletedTask;
    }
}
