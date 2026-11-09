using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AccessProbe;

public sealed partial class TaskStore
{
    public static string MetadataTaskKey(PlatformCollectedTask row)
    {
        var platform = MetadataPlatform.Get(row.PlatformId);
        if (!MetadataTaskNavigation.IsId(row.AccountId) || !MetadataTaskNavigation.IsId(row.CourseId)
            || !MetadataTaskNavigation.IsId(row.RemoteId) || !(row.PlatformId == "zhihuishu" && row.CourseKind is "flip" or "shared" or "smart"
                || row.PlatformId == "ucampus" && row.CourseKind == "legacy"
                || row.PlatformId == "pta" && row.CourseKind == "class" && MetadataPlatformParser.PtaId(row.AccountId)
                    && MetadataPlatformParser.PtaId(row.CourseId) && MetadataPlatformParser.PtaId(row.RemoteId))
            || !(row.PlatformId == "pta" ? row.Kind == "problem-set" : row.Kind is "homework" or "exam" || row.PlatformId == "ucampus" && row.Kind == "unit"))
            throw new TaskValidationException(platform.CodePrefix + "003", "无法确认任务的账号、课程或任务归属，未保存本批数据。");
        return platform.Id + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { row.AccountId, row.CourseKind, row.CourseId, row.Kind, row.RemoteId }))));
    }
    public ImportSummary ImportMetadataTasks(string platformId, IReadOnlyList<PlatformCollectedTask> rows, DateTimeOffset now,
        CancellationToken cancellation = default, long? recordingRevision = null)
    {
        var platform = MetadataPlatform.Get(platformId); var added = 0; var updated = 0; var duplicates = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested(); using var connection = Open(); using var transaction = connection.BeginTransaction();
            var notifications = ReadSettings(connection, transaction).NotificationsEnabled;
            var recording = ReadCourseRecording(connection, transaction, platformId);
            if (recordingRevision is { } revision && recording.Revision != revision) throw new CourseRecordingChangedException();
            foreach (var row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                if (row.PlatformId != platformId) throw new TaskValidationException(platform.CodePrefix + "003", "任务平台归属不一致。");
                var key = MetadataTaskKey(row); var courseKey = CourseRecordingRules.MetadataKey(platformId, row.AccountId, row.CourseId, row.CourseKind);
                if (!CourseRecordingRules.IsSelected(recording, courseKey)) continue;
                if (!seen.Add(key)) { duplicates++; continue; }
                if (row.NoDeadline && (!(platformId == "pta" || platformId == "ucampus" && row.Kind == "unit") || row.DeadlineUtc is not null))
                    throw new TaskValidationException(platform.CodePrefix + "003", "任务截止时间标记不一致。");
                var deadlineKind = row.NoDeadline ? DeadlineKind.None : row.DeadlineUtc is not null ? DeadlineKind.Known : DeadlineKind.Unknown;
                TaskRules.Validate(new(row.Title, platformId, row.CourseName, deadlineKind, row.BeginUtc, row.DeadlineUtc, row.Link, row.ProgressPercent, "", "", null));
                if (row.State is { } state && !Enum.IsDefined(state) || row.Link is not null && !MetadataTaskNavigation.Matches(row))
                    throw new TaskValidationException(platform.CodePrefix + "003", "任务状态或官方入口的归属无法确认，未保存本批数据。");
                var current = Find(connection, transaction, "source_key", key);
                if (current is not null && (current.Source != platform.Source || current.PlatformId != platformId))
                    throw new TaskValidationException("DB002", "任务来源发生冲突，请保留任务库并查看故障说明。");
                // A recent reminder is supplemental. It cannot replace a previously confirmed
                // course task list result when the authoritative list is temporarily unavailable.
                if (current is { AutomaticMetadataVersion: >= 2 } && row.MetadataVersion < 2) continue;
                var task = current ?? new TaskItem { Source = platform.Source, SourceKey = key, PlatformId = platformId,
                    CreatedAtUtc = now.ToUniversalTime(), State = TaskState.NeedsReview };
                var deadlineOverride = task.Overrides.HasFlag(TaskOverrides.Deadline);
                task = task with
                {
                    Title = task.Overrides.HasFlag(TaskOverrides.Title) ? task.Title : row.Title.Trim(),
                    CourseName = task.Overrides.HasFlag(TaskOverrides.Course) ? task.CourseName : row.CourseName.Trim(),
                    State = task.Overrides.HasFlag(TaskOverrides.State) ? task.State : row.State ?? TaskState.NeedsReview,
                    PlatformTitle = row.Title, PlatformStatusText = row.PlatformStatusText, PlatformStateEvidence = row.StateEvidence,
                    AutomaticStatusExplanation = row.StatusExplanation, RecordingCourseKey = courseKey,
                    CollectedLearningEvidence = row.LearningEvidence,
                    // Each unit row describes the current required checkpoint set; an unknown
                    // or elective-only result must not reuse a formerly completed percentage.
                    ProgressPercent = task.Overrides.HasFlag(TaskOverrides.Progress) ? task.ProgressPercent
                        : platformId == "ucampus" && row.Kind == "unit" ? row.ProgressPercent : row.ProgressPercent ?? task.ProgressPercent,
                    BeginUtc = task.Overrides.HasFlag(TaskOverrides.Begin) ? task.BeginUtc : row.BeginUtc?.ToUniversalTime(),
                    DeadlineKind = deadlineOverride ? task.DeadlineKind : deadlineKind,
                    DeadlineUtc = deadlineOverride ? task.DeadlineUtc : row.DeadlineUtc?.ToUniversalTime(),
                    DeadlineEvidence = deadlineOverride ? task.DeadlineEvidence : row.DeadlineUtc is not null ? DeadlineEvidence.PlatformDate : DeadlineEvidence.Unknown,
                    Link = task.Overrides.HasFlag(TaskOverrides.Link) ? task.Link : row.Link,
                    LinkIssue = task.Overrides.HasFlag(TaskOverrides.Link) ? task.LinkIssue : row.Link is null ? platform.CodePrefix + "003" : null,
                    CollectedNavigationKey = MetadataTaskNavigation.Key(platformId, row.Link) ?? "",
                    DisplayedTimeText = row.TimeText, PlatformListTimeText = row.TimeText,
                    PlatformListTimeObservedAtUtc = now.ToUniversalTime(), CollectedAtUtc = now.ToUniversalTime(),
                    UpdatedAtUtc = now.ToUniversalTime(), AutomaticMetadataVersion = row.MetadataVersion, Revision = current is null ? 1 : current.Revision + 1
                };
                Write(connection, transaction, task); Reconcile(connection, transaction, task, now);
                if (current is null)
                { added++; if (notifications && TaskRules.IsActive(task)) InsertPlan(connection, transaction, ReminderPlanner.NewTask(task, now)); }
                else updated++;
            }
            cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        if (added + updated > 0) Changed?.Invoke(); return new(added, updated, 0, duplicates);
    }
    public MetadataSyncSettings LoadMetadataSettings(string platformId)
    {
        var platform = MetadataPlatform.Get(platformId);
        lock (gate)
        {
            using var connection = Open(); using var command = Command(connection, null, "SELECT value FROM settings WHERE key=$key;", ("$key", "platform:" + platformId));
            if (command.ExecuteScalar() is not string json) return new();
            try
            {
                var settings = JsonSerializer.Deserialize<MetadataSyncSettings>(json) ?? throw new JsonException();
                if (settings.LastSummary is { } summary && (summary.PlatformId != platformId || summary.Problems is null)) throw new JsonException();
                return settings;
            }
            catch (JsonException) { throw new TaskValidationException("DB002", platform.Name + "连接设置格式不正确，请先备份任务库。"); }
        }
    }
    public void SaveMetadataSettings(string platformId, MetadataSyncSettings settings)
    {
        _ = MetadataPlatform.Get(platformId);
        if (settings.LastSummary is { } summary && summary.PlatformId != platformId) throw new TaskValidationException("SYNC003", "同步结果平台归属不一致。");
        lock (gate) { using var connection = Open(); WriteMetadataSettings(connection, null, platformId, settings); }
        Changed?.Invoke();
    }
    public void RecordMetadataSyncResult(MetadataSyncSummary summary, string outcome, DateTimeOffset now, CancellationToken cancellation = default)
    {
        _ = MetadataPlatform.Get(summary.PlatformId);
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested(); var settings = LoadMetadataSettings(summary.PlatformId) with
            { LastSummary = summary, LastSyncOutcome = outcome, LastSyncAtUtc = now.ToUniversalTime() };
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            WriteMetadataSettings(connection, transaction, summary.PlatformId, settings); cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        Changed?.Invoke();
    }
    private static void WriteMetadataSettings(SqliteConnection connection, SqliteTransaction? transaction, string platformId, MetadataSyncSettings settings)
        => Execute(connection, transaction, "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            ("$key", "platform:" + platformId), ("$value", JsonSerializer.Serialize(settings)));
}
