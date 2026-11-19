using AccessProbe.Linux;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace AccessProbe;

public sealed partial class TaskStore
{
    private static Dictionary<string,ManualCourse> ReadManualCourses(SqliteConnection connection,SqliteTransaction transaction,string platform,CourseRecordingSettings recording,IReadOnlyList<TaskItem> tasks,DateTimeOffset now)
    {
        using var command=Command(connection,transaction,"SELECT value FROM settings WHERE key=$key;",("$key","linux-manual-courses:"+platform));
        Dictionary<string,ManualCourse> result;
        try
        {
            var items=command.ExecuteScalar() is string json?JsonSerializer.Deserialize<ManualCourse[]>(json)??throw new JsonException():[];
            if(items.Any(c=>c is null||!CourseRecordingRules.IsKey(platform,c.Key)||string.IsNullOrWhiteSpace(c.Name)||c.Name.Length>200||c.Semester is null))throw new JsonException();
            foreach(var item in items)item.Semester.Validate();
            if(items.Select(c=>c.Key).Distinct().Count()!=items.Length)throw new JsonException();
            result=items.ToDictionary(c=>c.Key,StringComparer.Ordinal);
        }
        catch(Exception error)when(error is JsonException or TaskValidationException)
        {throw new TaskValidationException("DB002","自定义课程登记损坏，请先备份任务库。");}
        foreach(var course in RecordingCourses(recording,tasks).Where(c=>c.Detail.StartsWith("自定义 · ",StringComparison.Ordinal)||c.Detail=="手动录入"))
            if(!result.ContainsKey(course.Key))result.Add(course.Key,new(course.Key,course.Name,CourseSelectionRules.ParseSemester(course.Detail)??AcademicSemester.Current(now)));
        return result;
    }
    private static void WriteManualCourses(SqliteConnection connection,SqliteTransaction transaction,string platform,IEnumerable<ManualCourse> courses)
        =>Execute(connection,transaction,"INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",("$key","linux-manual-courses:"+platform),("$value",JsonSerializer.Serialize(courses.Select(c=>c with{TaskCount=0}).ToArray())));
    internal ManualCoursePage LoadLinuxManualCourses(string platform,DateTimeOffset now)
    {
        CheckRecordingPlatform(platform);lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();
            var recording=ReadCourseRecording(connection,transaction,platform);var tasks=ReadRecordingTasks(connection,transaction,platform);
            return ManualPage(recording,ReadManualCourses(connection,transaction,platform,recording,tasks,now),tasks);
        }
    }
    private static ManualCoursePage ManualPage(CourseRecordingSettings recording,Dictionary<string,ManualCourse> courses,IReadOnlyList<TaskItem> tasks)
        =>new(recording.Revision,courses.Values.Select(c=>c with{TaskCount=tasks.Count(t=>t.Source==TaskSource.Manual&&CourseRecordingRules.TaskCourseKey(t)==c.Key)})
            .OrderBy(c=>c.Archived).ThenBy(c=>c.Name,StringComparer.CurrentCulture).ToArray());
    internal ManualCoursePage EditLinuxManualCourse(ManualCourseEdit input,DateTimeOffset now)
    {
        if(input is null||input.Semester is null||string.IsNullOrWhiteSpace(input.Name)||input.Name.Trim().Length>200)throw new TaskValidationException("COURSE001","课程名称最多 200 字，请选择有效学期。");
        input.Semester.Validate();return ChangeManualCourse(input.Platform,input.Key,input.Revision,input,now);
    }
    internal ManualCoursePage RemoveLinuxManualCourse(string platform,string key,long revision,DateTimeOffset now)=>ChangeManualCourse(platform,key,revision,null,now);
    private ManualCoursePage ChangeManualCourse(string platform,string key,long revision,ManualCourseEdit? edit,DateTimeOffset now)
    {
        CheckRecordingPlatform(platform);ManualCoursePage page;
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();var recording=ReadCourseRecording(connection,transaction,platform);
            if(recording.Revision!=revision)throw new TaskValidationException("COURSE002","课程已更新，请重新加载后操作。");
            var tasks=ReadRecordingTasks(connection,transaction,platform);var courses=ReadManualCourses(connection,transaction,platform,recording,tasks,now);
            if(!courses.TryGetValue(key,out var current))throw new TaskValidationException("COURSE001","该自定义课程已不存在。");
            var related=tasks.Where(t=>t.Source==TaskSource.Manual&&CourseRecordingRules.TaskCourseKey(t)==key).ToArray();
            if(edit is null&&related.Length>0)throw new TaskValidationException("COURSE003","课程仍有历史任务，请归档课程以保留任务。");
            if(edit is not null&&courses.Values.Any(c=>c.Key!=key&&string.Equals(c.Name,edit.Name.Trim(),StringComparison.OrdinalIgnoreCase)))throw new TaskValidationException("COURSE001","该名称的自定义课程已存在。");
            var catalog=RecordingCourses(recording,tasks).ToDictionary(c=>c.Key,StringComparer.Ordinal);
            if(edit is null){courses.Remove(key);catalog.Remove(key);}
            else
            {
                current=current with{Name=edit.Name.Trim(),Semester=edit.Semester,Archived=edit.Archived};courses[key]=current;
                catalog[key]=new(key,current.Name,"自定义 · "+current.Semester.Label+(current.Archived?" · 已归档":""),false);
            }
            var selected=recording.RecordAllCourses?catalog.Keys.ToArray():recording.SelectedCourseKeys.ToArray();
            if(edit is null||edit.Archived)selected=selected.Where(k=>k!=key).ToArray();
            recording=recording with{Revision=checked(recording.Revision+1),Courses=catalog.Values.ToArray(),SelectedCourseKeys=selected,RecordAllCourses=false};
            WriteManualCourses(connection,transaction,platform,courses.Values);WriteCourseRecording(connection,transaction,platform,recording);
            if(edit is null||edit.Archived)RemoveManualChoices(connection,transaction,platform,key,catalog.Keys.ToArray());
            foreach(var task in related)
            {
                var updated=task with{RecordingCourseKey=key,CourseName=current.Name,Revision=checked(task.Revision+1),UpdatedAtUtc=now.ToUniversalTime()};
                Write(connection,transaction,updated);Reconcile(connection,transaction,updated,now);
            }
            transaction.Commit();page=ManualPage(recording,courses,ReadRecordingTasks(connection,null,platform));
        }
        Changed?.Invoke();return page;
    }
    private static void RemoveManualChoices(SqliteConnection connection,SqliteTransaction transaction,string platform,string key,string[] catalogKeys)
    {
        using var command=Command(connection,transaction,"SELECT key FROM settings WHERE key=$active OR key LIKE $period;",("$active","linux-course-selection:"+platform),("$period","linux-course-period:"+platform+":%"));
        var settingKeys=new List<string>();using(var reader=command.ExecuteReader())while(reader.Read())settingKeys.Add(reader.GetString(0));
        foreach(var settingKey in settingKeys)
        {
            var preference=ReadLinuxCoursePreference(connection,transaction,settingKey,platform)!;
            WriteLinuxCoursePreference(connection,transaction,settingKey,preference with{SelectedKeys=(preference.RecordAll?catalogKeys:preference.SelectedKeys??[]).Where(k=>k!=key).ToArray(),RecordAll=false,ReviewedKeys=preference.ReviewedKeys.Union([key]).ToArray()});
        }
    }
    internal TaskItem SaveLinuxTask(string? id,int revision,TaskDraft draft,string? courseKey,DateTimeOffset now)
    {
        TaskItem result;
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();
            var current=id is null?null:Find(connection,transaction,"id",id)??throw new TaskValidationException("TASK004","任务已不存在，请刷新清单。");
            if(current is not null&&current.Revision!=revision)throw new TaskValidationException("TASK004","任务刚被更新，请重新打开编辑窗口后操作。");
            var recording=ReadCourseRecording(connection,transaction,draft.PlatformId);
            var courses=ReadManualCourses(connection,transaction,draft.PlatformId,recording,[],now);
            var manual=current is null||current.Source==TaskSource.Manual;
            ManualCourse? chosen=null;
            if(!string.IsNullOrEmpty(courseKey))
            {
                if(!manual||!courses.TryGetValue(courseKey,out chosen)||chosen.Archived)throw new TaskValidationException("COURSE001","请选择当前平台未归档的自定义课程。");
            }
            else if(manual)chosen=courses.Values.FirstOrDefault(c=>!c.Archived&&string.Equals(c.Name,draft.CourseName.Trim(),StringComparison.OrdinalIgnoreCase));
            if(chosen is not null)draft=draft with{CourseName=chosen.Name};
            result=current is null?TaskRules.Create(draft,now):TaskRules.ApplyDraft(current,draft,now) with{Revision=checked(current.Revision+1)};
            if(manual)result=result with{RecordingCourseKey=chosen?.Key??(current is not null&&current.PlatformId==draft.PlatformId&&current.CourseName==draft.CourseName.Trim()?current.RecordingCourseKey:"")};
            Write(connection,transaction,result);Reconcile(connection,transaction,result,now);transaction.Commit();
        }
        Changed?.Invoke();return result;
    }
}
