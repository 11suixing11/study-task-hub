using AccessProbe.Linux;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace AccessProbe;
public sealed partial class TaskStore
{
    private static string SyncReportKey(string platform,string stage)=>"linux-sync-report:"+platform+":"+stage;
    private static LinuxSyncReport? ReadSyncReport(SqliteConnection connection,SqliteTransaction? transaction,string platform,string stage)
    {
        using var command=Command(connection,transaction,"SELECT value FROM settings WHERE key=$key;",("$key",SyncReportKey(platform,stage)));
        if(command.ExecuteScalar() is not string json)return null;
        try
        {
            var value=JsonSerializer.Deserialize<LinuxSyncReport>(json)??throw new JsonException();
            if(value.Platform!=platform||value.Stage!=stage||value.Metrics is null||value.Problems is null||value.Courses is null)throw new JsonException();
            return value;
        }
        catch(JsonException){throw new TaskValidationException("DB002","同步报告格式不正确，请先备份任务库。");}
    }
    internal SyncReportPage LoadLinuxSyncReports(string platform)
    {
        CheckRecordingPlatform(platform);lock(gate){using var connection=Open();using var transaction=connection.BeginTransaction();return new(ReadSyncReport(connection,transaction,platform,"catalog"),ReadSyncReport(connection,transaction,platform,"tasks"));}
    }
    internal void SaveLinuxSyncReport(LinuxSyncReport report)
    {
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();var old=ReadSyncReport(connection,transaction,report.Platform,report.Stage);
            var recording=ReadCourseRecording(connection,transaction,report.Platform);var tasks=ReadRecordingTasks(connection,transaction,report.Platform);
            var courses=RecordingCourses(recording,tasks).Select(course=>
            {
                var selected=CourseRecordingRules.IsSelected(recording,course.Key);var local=tasks.Where(t=>CourseRecordingRules.TaskCourseKey(t)==course.Key).ToArray();
                var observed=local.Count(t=>t.Source!=TaskSource.Manual&&t.CollectedAtUtc>=report.StartedAtUtc&&t.CollectedAtUtc<=report.FinishedAtUtc);
                var successful=report.Stage=="tasks"&&report.Complete&&selected&&course.Available;
                var previous=old?.Courses.FirstOrDefault(c=>c.Key==course.Key)?.LastSuccessfulAtUtc;
                var coverage=!course.Available?"手动或历史课程":!selected?"未选择":report.Stage=="catalog"?"仅刷新目录":successful?"本轮已完整读取":observed>0?"已有读取结果，覆盖待核对":"本轮覆盖未确认";
                return new SyncCourseReport(course.Key,course.Name,selected,course.Available,local.Length,observed,coverage,successful?report.FinishedAtUtc:previous);
            }).ToArray();
            report=report with{LastSuccessfulAtUtc=report.Complete?report.FinishedAtUtc:old?.LastSuccessfulAtUtc,Courses=courses};
            Execute(connection,transaction,"INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",("$key",SyncReportKey(report.Platform,report.Stage)),("$value",JsonSerializer.Serialize(report)));
            transaction.Commit();
        }
        Changed?.Invoke();
    }
}
