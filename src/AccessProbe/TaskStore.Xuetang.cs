using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AccessProbe;

public sealed partial class TaskStore
{
    public static string XuetangTaskKey(XuetangTask row)
    {
        if (!XuetangPlatform.IsId(row.AccountId) || !XuetangPlatform.IsId(row.ClassroomId)
            || !XuetangPlatform.IsSign(row.ProductSign) || !XuetangPlatform.IsSign(row.CourseSign)
            || !XuetangPlatform.IsId(row.RemoteId) || row.Kind is not ("exercise" or "exam"))
            throw new TaskValidationException("XT003", "无法确认学堂任务的账号、课程或任务归属，未保存本批数据。");
        return XuetangPlatform.Id + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { row.AccountId, row.ClassroomId, row.ProductSign, row.CourseSign, row.Kind, row.RemoteId }))));
    }

    public ImportSummary ImportXuetangTasks(IReadOnlyList<XuetangTask> rows, DateTimeOffset now,
        CancellationToken cancellation = default, long? recordingRevision = null)
    {
        var added = 0; var updated = 0; var duplicates = 0; var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var notifications = ReadSettings(connection, transaction).NotificationsEnabled;
            var recording = ReadCourseRecording(connection, transaction, XuetangPlatform.Id);
            if (recordingRevision is { } revision && recording.Revision != revision) throw new CourseRecordingChangedException();
            foreach (var row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                var key = XuetangTaskKey(row);
                var recordingKey = CourseRecordingRules.XuetangKey(row.AccountId, row.ClassroomId, row.ProductSign, row.CourseSign);
                if (!CourseRecordingRules.IsSelected(recording, recordingKey)) continue;
                if (!seen.Add(key)) { duplicates++; continue; }
                var deadlineKind = row.DeadlineUtc is not null ? DeadlineKind.Known : DeadlineKind.Unknown;
                TaskRules.Validate(new(row.Title, XuetangPlatform.Id, row.CourseName, deadlineKind, row.BeginUtc,
                    row.DeadlineUtc, row.Link, row.ProgressPercent, "", "", null));
                if (row.State is { } state && !Enum.IsDefined(state)) throw new TaskValidationException("XT003", "学堂任务状态无法识别。");
                if (row.Link is not null && !XuetangTaskNavigation.IsOfficialTaskLink(row.Link, row.ProductSign,
                    row.CourseSign, row.ClassroomId, row.RemoteId, row.Kind))
                    throw new TaskValidationException("XT003", "学堂在线入口与采集任务归属不一致，未保存本批数据。");
                var current = Find(connection, transaction, "source_key", key);
                if (current is not null && (current.Source != TaskSource.Xuetang || current.PlatformId != XuetangPlatform.Id))
                    throw new TaskValidationException("DB002", "学堂任务来源发生冲突，请保留任务库并查看故障说明。");
                var task = current ?? new TaskItem { Source = TaskSource.Xuetang, SourceKey = key,
                    PlatformId = XuetangPlatform.Id, CreatedAtUtc = now.ToUniversalTime(), State = TaskState.NeedsReview };
                var deadlineOverride = task.Overrides.HasFlag(TaskOverrides.Deadline);
                task = task with
                {
                    Title = task.Overrides.HasFlag(TaskOverrides.Title) ? task.Title : row.Title.Trim(),
                    CourseName = task.Overrides.HasFlag(TaskOverrides.Course) ? task.CourseName : row.CourseName.Trim(),
                    State = task.Overrides.HasFlag(TaskOverrides.State) ? task.State : row.State ?? TaskState.NeedsReview,
                    PlatformTitle = row.Title, PlatformStatusText = row.PlatformStatusText, PlatformStateEvidence = row.StateEvidence,
                    AutomaticStatusExplanation = row.StatusExplanation, RecordingCourseKey = recordingKey,
                    BeginUtc = task.Overrides.HasFlag(TaskOverrides.Begin) ? task.BeginUtc : row.BeginUtc?.ToUniversalTime(),
                    DeadlineKind = deadlineOverride ? task.DeadlineKind : deadlineKind,
                    DeadlineUtc = deadlineOverride ? task.DeadlineUtc : row.DeadlineUtc?.ToUniversalTime(),
                    DeadlineEvidence = deadlineOverride ? task.DeadlineEvidence : row.DeadlineUtc is not null ? DeadlineEvidence.PlatformDate : DeadlineEvidence.Unknown,
                    ProgressPercent = task.Overrides.HasFlag(TaskOverrides.Progress) ? task.ProgressPercent : row.ProgressPercent,
                    Link = task.Overrides.HasFlag(TaskOverrides.Link) ? task.Link : row.Link,
                    LinkIssue = task.Overrides.HasFlag(TaskOverrides.Link) ? task.LinkIssue : row.Link is null ? "XT003" : null,
                    CollectedNavigationKey = row.Link is null ? "" : XuetangTaskNavigation.Key(row.Link) ?? "",
                    DisplayedTimeText = row.TimeText, PlatformListTimeText = row.TimeText,
                    PlatformListTimeObservedAtUtc = now.ToUniversalTime(), CollectedAtUtc = now.ToUniversalTime(),
                    UpdatedAtUtc = now.ToUniversalTime(), AutomaticMetadataVersion = 1,
                    Revision = current is null ? 1 : current.Revision + 1
                };
                Write(connection, transaction, task); Reconcile(connection, transaction, task, now);
                if (current is null)
                {
                    added++;
                    if (notifications && TaskRules.IsActive(task)) InsertPlan(connection, transaction, ReminderPlanner.NewTask(task, now));
                }
                else updated++;
            }
            cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        if (added + updated > 0) Changed?.Invoke();
        return new(added, updated, 0, duplicates);
    }

    public XuetangSyncSettings LoadXuetangSettings()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT value FROM settings WHERE key='platform:xuetang';");
            if (command.ExecuteScalar() is not string json) return new();
            try { return JsonSerializer.Deserialize<XuetangSyncSettings>(json) ?? throw new JsonException(); }
            catch (JsonException) { throw new TaskValidationException("DB002", "学堂在线连接设置格式不正确，请先备份任务库。"); }
        }
    }
    public void SaveXuetangSettings(XuetangSyncSettings settings)
    {
        lock (gate) { using var connection = Open(); WriteXuetangSettings(connection, null, settings); }
        Changed?.Invoke();
    }
    public void RecordXuetangSyncResult(XuetangSyncSummary summary, string outcome, DateTimeOffset now, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            var settings = LoadXuetangSettings() with { LastSummary = summary, LastSyncOutcome = outcome, LastSyncAtUtc = now.ToUniversalTime() };
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            WriteXuetangSettings(connection, transaction, settings); cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        Changed?.Invoke();
    }
    private static void WriteXuetangSettings(SqliteConnection connection, SqliteTransaction? transaction, XuetangSyncSettings settings)
        => Execute(connection, transaction, "INSERT INTO settings(key,value) VALUES('platform:xuetang',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            ("$value", JsonSerializer.Serialize(settings)));
}
