using AccessProbe.Linux;
using System.Text.Json;
namespace AccessProbe;

public sealed partial class TaskStore
{
    internal (CourseRecordingSettings,LinuxCoursePreference?,IReadOnlyList<TaskItem>) LoadLinuxCourseSelection(string platform,AcademicSemester semester)
    {
        CheckRecordingPlatform(platform);
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();
            var current=ReadCourseRecording(connection,transaction,platform);
            var tasks=ReadRecordingTasks(connection,transaction,platform);
            var active=ReadLinuxCoursePreference(connection,transaction,"linux-course-selection:"+platform,platform);
            var preference=ReadLinuxCoursePreference(connection,transaction,PeriodKey(platform,semester),platform);
            if(preference is not null&&preference.Semester!=semester)
                throw new TaskValidationException("DB002","学期选课档案与所属学期不一致，请先备份任务库。");
            if(active?.Semester==semester)
                preference=active with{SelectedKeys=current.SelectedCourseKeys.ToArray(),RecordAll=current.RecordAllCourses};
            return(current with{Courses=RecordingCourses(current,tasks)},preference,tasks);
        }
    }
    internal CourseRecordingSettings SaveLinuxCourseSelection(ReviewedCourseSelection input,DateTimeOffset now)
    {
        if(input is null||input.Semester is null||input.Keys is null||input.CustomCourses is null||input.CustomCourses.Length>50)
            throw new TaskValidationException("COURSE001","选课字段不完整或自定义课程过多。");
        CheckRecordingPlatform(input.Platform);input.Semester.Validate();
        CourseRecordingSettings result;
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();
            var current=ReadCourseRecording(connection,transaction,input.Platform);
            if(current.Revision!=input.Revision)throw new TaskValidationException("COURSE002","课程选择已更新，输入已保留，请重新加载课程。");
            var tasks=ReadRecordingTasks(connection,transaction,input.Platform);
            var courses=RecordingCourses(current,tasks).ToDictionary(c=>c.Key,StringComparer.Ordinal);
            var keys=input.Keys.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            if(keys.Any(k=>!courses.ContainsKey(k)))throw new TaskValidationException("COURSE001","选中的课程不属于当前目录。");
            var reviewed=(input.ReviewedKeys??courses.Keys.ToArray()).ToHashSet(StringComparer.Ordinal);
            if(reviewed.Any(k=>!courses.ContainsKey(k))||keys.Any(k=>!reviewed.Contains(k)))throw new TaskValidationException("COURSE001","选课审查范围无效。");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var custom in input.CustomCourses)
            {
                if(custom is null||string.IsNullOrWhiteSpace(custom.Name)||custom.Name.Trim().Length>200||!names.Add(custom.Name.Trim()))
                    throw new TaskValidationException("COURSE001","自定义课程名称不能为空、超过 200 字或重复。");
                var name=custom.Name.Trim();var key=CourseRecordingRules.TaskCourseKey(new TaskItem{PlatformId=input.Platform,Source=TaskSource.Manual,CourseName=name});
                if(courses.ContainsKey(key))throw new TaskValidationException("COURSE001","该手动课程已存在，请直接勾选。");
                courses.Add(key,new(key,name,"自定义 · "+input.Semester.Label,false));reviewed.Add(key);
                if(custom.Selected)keys.Add(key);
            }
            result=current with{Revision=checked(current.Revision+1),SelectionConfirmed=true,RecordAllCourses=false,
                HideUnselectedTasks=true,SelectedCourseKeys=keys.ToArray(),Courses=courses.Values.ToArray()};
            WriteCourseRecording(connection,transaction,input.Platform,result);
            var previous=ReadLinuxCoursePreference(connection,transaction,"linux-course-selection:"+input.Platform,input.Platform);
            if(previous is not null&&current.SelectionConfirmed)
                WriteLinuxCoursePreference(connection,transaction,PeriodKey(input.Platform,previous.Semester),previous with{SelectedKeys=current.SelectedCourseKeys.ToArray(),RecordAll=current.RecordAllCourses});
            var preference=new LinuxCoursePreference(input.Semester,reviewed.ToArray(),keys.ToArray());
            WriteLinuxCoursePreference(connection,transaction,PeriodKey(input.Platform,input.Semester),preference);
            Execute(connection,transaction,"INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                ("$key","linux-course-selection:"+input.Platform),("$value",JsonSerializer.Serialize(preference)));
            foreach(var task in tasks)Reconcile(connection,transaction,task,now);
            transaction.Commit();
        }
        Changed?.Invoke();return result;
    }
    private static string PeriodKey(string platform,AcademicSemester semester)=>$"linux-course-period:{platform}:{semester.YearStart}:{semester.Term}";
    private static LinuxCoursePreference? ReadLinuxCoursePreference(Microsoft.Data.Sqlite.SqliteConnection connection,Microsoft.Data.Sqlite.SqliteTransaction transaction,string key,string platform)
    {
        using var command=Command(connection,transaction,"SELECT value FROM settings WHERE key=$key;",("$key",key));
        if(command.ExecuteScalar() is not string json)return null;
        try
        {
            var value=JsonSerializer.Deserialize<LinuxCoursePreference>(json)??throw new JsonException();
            if(value.Semester is null||value.ReviewedKeys is null||value.ReviewedKeys.Any(k=>!CourseRecordingRules.IsKey(platform,k))
                ||value.SelectedKeys is not null&&value.SelectedKeys.Any(k=>!CourseRecordingRules.IsKey(platform,k)))throw new JsonException();
            value.Semester.Validate();return value;
        }
        catch(Exception error)when(error is JsonException or TaskValidationException)
        {throw new TaskValidationException("DB002","学期选课偏好格式不正确，请先备份任务库。");}
    }
    private static void WriteLinuxCoursePreference(Microsoft.Data.Sqlite.SqliteConnection connection,Microsoft.Data.Sqlite.SqliteTransaction transaction,string key,LinuxCoursePreference value)
        =>Execute(connection,transaction,"INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",("$key",key),("$value",JsonSerializer.Serialize(value)));
}
