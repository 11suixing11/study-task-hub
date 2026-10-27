using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AccessProbe;

public sealed partial class TaskStore
{
    public static string CollectedTaskKey(CollectedTask row)
    {
        var platform = RainPlatform.Find(row.PlatformId);
        var host = platform.CheckHost(row.Host);
        if (!RainAddressPolicy.IsId(row.AccountId) || !RainAddressPolicy.IsId(row.ClassroomId)
            || !RainAddressPolicy.IsId(row.RemoteId) || row.Kind is not ("exercise" or "quiz" or "exam"))
            throw new TaskValidationException("RAIN003", "无法确认采集任务的账号、班级或任务归属，未保存本批数据。");
        var scope = JsonSerializer.Serialize(new[] { host, row.AccountId, row.ClassroomId, row.Kind, row.RemoteId });
        return row.PlatformId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }

    public ImportSummary ImportCollectedTasks(IReadOnlyList<CollectedTask> rows, DateTimeOffset now,
        CancellationToken cancellation = default, long? recordingRevision = null)
    {
        var added = 0; var updated = 0; var duplicates = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var notifications = ReadSettings(connection, transaction).NotificationsEnabled;
            foreach (var row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                var key = CollectedTaskKey(row);
                var recording = ReadCourseRecording(connection, transaction, row.PlatformId);
                if (recordingRevision is { } revision && recording.Revision != revision) throw new CourseRecordingChangedException();
                var recordingKey = CourseRecordingRules.RainKey(row.PlatformId, row.Host, row.AccountId, row.ClassroomId);
                if (!CourseRecordingRules.IsSelected(recording, recordingKey)) continue;
                if (!seen.Add(key)) { duplicates++; continue; }
                TaskRules.Validate(new(row.Title, row.PlatformId, row.CourseName, row.DeadlineKind,
                    row.BeginUtc, row.DeadlineUtc, row.Link, row.ProgressPercent, row.ScoreText, "", null));
                if (row.Link is not null && !RainTaskNavigation.IsOfficialTaskLink(row.PlatformId, row.Link, row.ClassroomId, row.RemoteId, row.Kind))
                    throw new TaskValidationException("RAIN003", "作业入口与采集任务归属不一致，未保存本批数据。");
                var current = Find(connection, transaction, "source_key", key);
                if (current is not null && (current.Source != TaskSource.RainClassroom || current.PlatformId != row.PlatformId))
                    throw new TaskValidationException("DB002", "采集任务来源发生冲突，请保留数据库并查看故障说明。");
                var task = current ?? new TaskItem
                {
                    Source = TaskSource.RainClassroom, SourceKey = key, PlatformId = row.PlatformId,
                    CreatedAtUtc = now.ToUniversalTime(), Title = row.Title, CourseName = row.CourseName
                };
                var state = task.Overrides.HasFlag(TaskOverrides.State) || row.State is null ? task.State : row.State.Value;
                var deadlineOverridden = task.Overrides.HasFlag(TaskOverrides.Deadline);
                task = task with
                {
                    Title = task.Overrides.HasFlag(TaskOverrides.Title) ? task.Title : row.Title.Trim(),
                    CourseName = task.Overrides.HasFlag(TaskOverrides.Course) ? task.CourseName : row.CourseName.Trim(),
                    State = state, PlatformTitle = row.Title, PlatformStatusText = row.PlatformStatusText,
                    RecordingCourseKey = recordingKey,
                    PlatformStateEvidence = row.StateEvidence, AutomaticStatusExplanation = row.StatusExplanation,
                    DeadlineKind = deadlineOverridden ? task.DeadlineKind : row.DeadlineKind,
                    DeadlineUtc = deadlineOverridden ? task.DeadlineUtc : row.DeadlineUtc?.ToUniversalTime(),
                    DeadlineEvidence = deadlineOverridden ? task.DeadlineEvidence
                        : row.DeadlineUtc is not null ? DeadlineEvidence.PlatformDate : DeadlineEvidence.Unknown,
                    BeginUtc = task.Overrides.HasFlag(TaskOverrides.Begin) ? task.BeginUtc : row.BeginUtc?.ToUniversalTime(),
                    ProgressPercent = task.Overrides.HasFlag(TaskOverrides.Progress) ? task.ProgressPercent : row.ProgressPercent,
                    ScoreText = task.Overrides.HasFlag(TaskOverrides.Score) ? task.ScoreText : row.ScoreText,
                    Link = task.Overrides.HasFlag(TaskOverrides.Link) ? task.Link : row.Link,
                    LinkIssue = task.Overrides.HasFlag(TaskOverrides.Link) ? task.LinkIssue : null,
                    CollectedNavigationKey = row.Link is null ? "" : RainTaskNavigation.Key(row.PlatformId, row.Link) ?? "",
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

    public PlatformSyncSettings LoadPlatformSettings(string platformId)
    {
        var platform = RainPlatform.Find(platformId);
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT value FROM settings WHERE key=$key;", ("$key", "platform:" + platformId));
            if (command.ExecuteScalar() is not string json) return new() { Host = platform.DefaultHost };
            try
            {
                var settings = JsonSerializer.Deserialize<PlatformSyncSettings>(json) ?? throw new JsonException();
                platform.CheckHost(settings.Host);
                return settings;
            }
            catch (JsonException) { throw new TaskValidationException("DB002", "平台连接设置格式不正确，请先备份任务库。"); }
        }
    }

    public void SavePlatformSettings(string platformId, PlatformSyncSettings settings)
    {
        settings = settings with { Host = RainPlatform.Find(platformId).CheckHost(settings.Host) };
        lock (gate)
        {
            using var connection = Open(); WritePlatformSettings(connection, null, platformId, settings);
        }
        Changed?.Invoke();
    }

    public void RecordPlatformSyncResult(RainSyncSummary summary, string outcome, DateTimeOffset now,
        CancellationToken cancellation = default)
    {
        RainPlatform.Find(summary.PlatformId);
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            var settings = LoadPlatformSettings(summary.PlatformId) with
            { LastSyncAtUtc = now.ToUniversalTime(), LastSyncOutcome = outcome, LastSummary = summary };
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            WritePlatformSettings(connection, transaction, summary.PlatformId, settings);
            cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        Changed?.Invoke();
    }

    private static void WritePlatformSettings(SqliteConnection connection, SqliteTransaction? transaction,
        string platformId, PlatformSyncSettings settings) => Execute(connection, transaction,
        "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
        ("$key", "platform:" + platformId), ("$value", JsonSerializer.Serialize(settings)));
}
