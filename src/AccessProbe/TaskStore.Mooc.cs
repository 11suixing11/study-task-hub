using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AccessProbe;

public sealed partial class TaskStore
{
    public static string MoocTaskKey(MoocTask row)
    {
        if (!MoocPlatform.IsId(row.AccountId) || !MoocPlatform.IsId(row.CourseId) || !MoocPlatform.IsId(row.TermId)
            || !MoocPlatform.IsId(row.RemoteId) || row.Kind is not ("homework" or "homework_oj" or "quiz" or "exam" or "exam_object" or "exam_subject" or "exam_oj" or "exam_new")
            || row.ExamId.Length > 0 && !MoocPlatform.IsId(row.ExamId))
            throw new TaskValidationException("MOOC003", "无法确认任务的账号、课程、学期或任务归属，未保存本批数据。");
        return "mooc:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { row.AccountId, row.CourseId, row.TermId, row.Kind, row.RemoteId, row.ExamId }))));
    }

    public ImportSummary ImportMoocTasks(IReadOnlyList<MoocTask> rows, DateTimeOffset now, CancellationToken cancellation = default,
        long? recordingRevision = null)
    {
        var added = 0; var updated = 0; var duplicates = 0; var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var notifications = ReadSettings(connection, transaction).NotificationsEnabled;
            var recording = ReadCourseRecording(connection, transaction, "mooc");
            if (recordingRevision is { } revision && recording.Revision != revision) throw new CourseRecordingChangedException();
            foreach (var row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                var key = MoocTaskKey(row);
                var recordingKey = CourseRecordingRules.MoocKey(row.AccountId, row.CourseId, row.TermId);
                if (!CourseRecordingRules.IsSelected(recording, recordingKey)) continue;
                if (!seen.Add(key)) { duplicates++; continue; }
                var deadlineKind = row.DeadlineUtc is not null ? DeadlineKind.Known : DeadlineKind.Unknown;
                TaskRules.Validate(new(row.Title, "mooc", row.CourseName, deadlineKind, row.BeginUtc, row.DeadlineUtc, row.Link, null, row.ScoreText, "", null));
                if (row.Link is not null && !MoocTaskNavigation.IsOfficialTaskLink(row.Link, row.CourseId, row.TermId, row.RemoteId, row.Kind, row.ExamId))
                    throw new TaskValidationException("MOOC003", "任务入口与采集任务的课程、学期和编号不一致，未保存本批数据。");
                var current = Find(connection, transaction, "source_key", key);
                if (current is not null && (current.Source != TaskSource.Mooc || current.PlatformId != "mooc"))
                    throw new TaskValidationException("DB002", "采集任务来源发生冲突，请保留数据库并查看故障说明。");
                var task = current ?? new TaskItem { Source = TaskSource.Mooc, SourceKey = key, PlatformId = "mooc", CreatedAtUtc = now.ToUniversalTime(), State = TaskState.NeedsReview };
                var deadlineOverride = task.Overrides.HasFlag(TaskOverrides.Deadline);
                task = task with
                {
                    Title = task.Overrides.HasFlag(TaskOverrides.Title) ? task.Title : row.Title.Trim(),
                    CourseName = task.Overrides.HasFlag(TaskOverrides.Course) ? task.CourseName : row.CourseName.Trim(),
                    State = task.Overrides.HasFlag(TaskOverrides.State) ? task.State : row.State ?? TaskState.NeedsReview,
                    PlatformTitle = row.Title, PlatformStatusText = row.PlatformStatusText, PlatformStateEvidence = row.StateEvidence,
                    RecordingCourseKey = recordingKey,
                    AutomaticStatusExplanation = row.StatusExplanation,
                    BeginUtc = task.Overrides.HasFlag(TaskOverrides.Begin) ? task.BeginUtc : row.BeginUtc?.ToUniversalTime(),
                    DeadlineKind = deadlineOverride ? task.DeadlineKind : deadlineKind,
                    DeadlineUtc = deadlineOverride ? task.DeadlineUtc : row.DeadlineUtc?.ToUniversalTime(),
                    DeadlineEvidence = deadlineOverride ? task.DeadlineEvidence : row.DeadlineUtc is not null ? DeadlineEvidence.PlatformDate : DeadlineEvidence.Unknown,
                    ScoreText = task.Overrides.HasFlag(TaskOverrides.Score) ? task.ScoreText : row.ScoreText,
                    ProgressPercent = task.Overrides.HasFlag(TaskOverrides.Progress) ? task.ProgressPercent : null,
                    Link = task.Overrides.HasFlag(TaskOverrides.Link) ? task.Link : row.Link,
                    LinkIssue = task.Overrides.HasFlag(TaskOverrides.Link) ? task.LinkIssue : row.Link is null ? "MOOC003" : null,
                    CollectedNavigationKey = row.Link is not null ? MoocTaskNavigation.Key(row.Link) ?? "" : "",
                    DisplayedTimeText = row.TimeText, PlatformListTimeText = row.TimeText, PlatformListTimeObservedAtUtc = now.ToUniversalTime(),
                    CollectedAtUtc = now.ToUniversalTime(), UpdatedAtUtc = now.ToUniversalTime(), AutomaticMetadataVersion = 1,
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

    public MoocSyncSettings LoadMoocSettings()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT value FROM settings WHERE key='platform:mooc';");
            if (command.ExecuteScalar() is not string json) return new();
            try { return JsonSerializer.Deserialize<MoocSyncSettings>(json) ?? throw new JsonException(); }
            catch (JsonException) { throw new TaskValidationException("DB002", "MOOC 连接设置格式不正确，请先备份任务库。"); }
        }
    }

    public void SaveMoocSettings(MoocSyncSettings settings)
    {
        lock (gate) { using var connection = Open(); WriteMoocSettings(connection, null, settings); }
        Changed?.Invoke();
    }

    public void RecordMoocSyncResult(MoocSyncSummary summary, string outcome, DateTimeOffset now, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            var settings = LoadMoocSettings() with { LastSummary = summary, LastSyncOutcome = outcome, LastSyncAtUtc = now.ToUniversalTime() };
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            WriteMoocSettings(connection, transaction, settings); cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        Changed?.Invoke();
    }

    private static void WriteMoocSettings(SqliteConnection connection, SqliteTransaction? transaction, MoocSyncSettings settings)
        => Execute(connection, transaction, "INSERT INTO settings(key,value) VALUES('platform:mooc',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            ("$value", JsonSerializer.Serialize(settings)));
}
