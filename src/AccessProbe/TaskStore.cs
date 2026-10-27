using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AccessProbe;

public sealed partial class TaskStore
{
    private readonly object gate = new();
    private readonly string connectionString;
    private readonly ILocalLinkProtector protector;
    public string DatabasePath { get; }
    public event Action? Changed;

    public TaskStore(string databasePath, ILocalLinkProtector? linkProtector = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        protector = linkProtector ?? new WindowsLinkProtector();
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true,
            Pooling = false, DefaultTimeout = 5
        }.ToString();
        using var connection = Open();
        using var version = Command(connection, null, "PRAGMA user_version;");
        var schemaVersion = Convert.ToInt32(version.ExecuteScalar());
        if (schemaVersion > 1) throw new TaskValidationException("DB003", "任务库版本较新，请使用对应版本的程序打开。");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS tasks(
                id TEXT PRIMARY KEY, revision INTEGER NOT NULL,
                source_key TEXT UNIQUE, payload TEXT NOT NULL, protected_link BLOB);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS reminders(
                id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                kind INTEGER NOT NULL, due_utc INTEGER NOT NULL, deadline_utc INTEGER,
                state TEXT NOT NULL DEFAULT 'scheduled', requested_utc INTEGER,
                lease_until_utc INTEGER, next_attempt_utc INTEGER,
                attempts INTEGER NOT NULL DEFAULT 0, error_code TEXT);
            CREATE INDEX IF NOT EXISTS reminders_due ON reminders(state, due_utc);
            PRAGMA user_version = 1;
            """);
        transaction.Commit();
        using var integrity = Command(connection, null, "PRAGMA quick_check;");
        if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new TaskValidationException("DB002", "任务库检查未通过，请先备份数据库后查看故障说明。");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Key, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Key, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private TaskItem ReadItem(SqliteDataReader reader)
    {
        TaskItem task;
        try
        {
            task = JsonSerializer.Deserialize<TaskItem>(reader.GetString(2))
                ?? throw new JsonException();
            if (task.Id != reader.GetString(0) || task.Revision != reader.GetInt32(1)
                || !Enum.IsDefined(task.State) || !Enum.IsDefined(task.Source))
                throw new JsonException();
        }
        catch (JsonException) { throw new TaskValidationException("DB002", "任务数据格式不正确，请先备份数据库。"); }
        if (reader.IsDBNull(3)) return task;
        try
        {
            var link = protector.Unprotect((byte[])reader.GetValue(3));
            if (!TaskRules.IsSafeLink(link)) return task with { LinkIssue = "LINK001" };
            return task with { Link = link };
        }
        catch (CryptographicException) { return task with { LinkIssue = "LINK002" }; }
    }

    private TaskItem? Find(SqliteConnection connection, SqliteTransaction? transaction, string field, string value)
    {
        // Field is selected only by internal callers; user input is always a bound parameter.
        using var command = Command(connection, transaction,
            $"SELECT id,revision,payload,protected_link FROM tasks WHERE {field}=$value;", ("$value", value));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    public IReadOnlyList<TaskItem> LoadTasks()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT id,revision,payload,protected_link FROM tasks ORDER BY id;");
            using var reader = command.ExecuteReader();
            var tasks = new List<TaskItem>();
            while (reader.Read()) tasks.Add(ReadItem(reader));
            return tasks;
        }
    }

    public TaskItem? Get(string id)
    {
        lock (gate) { using var connection = Open(); return Find(connection, null, "id", id); }
    }

    private void Write(SqliteConnection connection, SqliteTransaction transaction, TaskItem task)
    {
        byte[]? link = task.Link is not null ? protector.Protect(task.Link) : null;
        // Preserve encrypted bytes if this Windows account cannot decrypt them. Other edits must not destroy the link.
        if (task.LinkIssue == "LINK002" && task.Link is null)
        {
            using var oldLink = Command(connection, transaction, "SELECT protected_link FROM tasks WHERE id=$id;", ("$id", task.Id));
            link = oldLink.ExecuteScalar() as byte[];
        }
        Execute(connection, transaction, """
            INSERT INTO tasks(id,revision,source_key,payload,protected_link) VALUES($id,$revision,$key,$payload,$link)
            ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,source_key=excluded.source_key,
                payload=excluded.payload,protected_link=excluded.protected_link;
            """, ("$id", task.Id), ("$revision", task.Revision), ("$key", task.SourceKey),
            ("$payload", JsonSerializer.Serialize(task)), ("$link", link));
    }

    public TaskItem Create(TaskDraft draft, DateTimeOffset now)
    {
        var task = TaskRules.Create(draft, now);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            Write(connection, transaction, task); Reconcile(connection, transaction, task, now); transaction.Commit();
        }
        Changed?.Invoke();
        return task;
    }

    private TaskItem Mutate(string id, int expectedRevision, DateTimeOffset now, Func<TaskItem, TaskItem> change)
    {
        TaskItem result;
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var current = Find(connection, transaction, "id", id) ?? throw new TaskValidationException("TASK004", "任务已不存在，请刷新清单。");
            if (current.Revision != expectedRevision)
                throw new TaskValidationException("TASK004", "任务刚被更新，请重新打开编辑窗口后操作。");
            result = change(current) with { Revision = current.Revision + 1, UpdatedAtUtc = now.ToUniversalTime() };
            Write(connection, transaction, result); Reconcile(connection, transaction, result, now); transaction.Commit();
        }
        Changed?.Invoke();
        return result;
    }

    public TaskItem Edit(string id, int revision, TaskDraft draft, DateTimeOffset now) =>
        Mutate(id, revision, now, task => TaskRules.ApplyDraft(task, draft, now));

    public TaskItem SetState(string id, int revision, TaskState state, DateTimeOffset now) =>
        Mutate(id, revision, now, task => TaskRules.Transition(task, state) with
            { Overrides = TaskRules.IsCollected(task) ? task.Overrides | TaskOverrides.State : task.Overrides });

    public int RefreshAutomaticMetadata(DateTimeOffset now)
    {
        var updated = 0;
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            using var command = Command(connection, transaction, "SELECT id,revision,payload,protected_link FROM tasks;");
            var rows = new List<TaskItem>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add(ReadItem(reader));
            foreach (var task in rows.Where(task => task.Source == TaskSource.Chaoxing && task.AutomaticMetadataVersion < 1))
            {
                var observedAt = task.CollectedAtUtc ?? now;
                var listText = task.PlatformListTimeText.Length > 0 ? task.PlatformListTimeText : task.DisplayedTimeText;
                var refreshed = ChaoxingTaskMetadata.Apply(task, listText, observedAt) with
                    { Revision = task.Revision + 1, UpdatedAtUtc = now.ToUniversalTime() };
                Write(connection, transaction, refreshed); Reconcile(connection, transaction, refreshed, now); updated++;
            }
            transaction.Commit();
        }
        if (updated > 0) Changed?.Invoke();
        return updated;
    }

    public void Delete(string id, int revision)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, "DELETE FROM tasks WHERE id=$id AND revision=$revision;",
                ("$id", id), ("$revision", revision));
            if (command.ExecuteNonQuery() != 1) throw new TaskValidationException("TASK004", "任务已更新或已删除，请刷新清单。");
        }
        Changed?.Invoke();
    }

    // An observed local key, scoped by course, class, cpi and work ID. It is not proof of a cross-platform identity.
    public static string? ObservedWorkKey(WorkTaskLocation? location)
    {
        if (location is null || string.IsNullOrEmpty(location.Cpi)) return null;
        var scope = string.Join("|", location.CourseId, location.ClassId, location.Cpi, location.WorkId);
        return "chaoxing:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }

    public ImportSummary ImportWorks(IReadOnlyList<WorkListTask> rows, string courseName, DateTimeOffset now, CancellationToken cancellation = default,
        long? recordingRevision = null)
    {
        if (courseName.Length > 200) throw new TaskValidationException("TASK001", "课程名最多 200 字。");
        var added = 0; var updated = 0; var unresolved = 0; var duplicates = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var notifications = ReadSettings(connection, transaction).NotificationsEnabled;
            var recording = ReadCourseRecording(connection, transaction, "chaoxing");
            if (recordingRevision is { } revision && recording.Revision != revision) throw new CourseRecordingChangedException();
            foreach (var row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                var verified = row.Location is null ? null : WorkListParser.ParseTaskLocation(row.Location.PageUri.OriginalString);
                var key = ObservedWorkKey(verified);
                if (key is not null && !seen.Add(key)) { duplicates++; continue; }
                if (key is null) unresolved++;
                if (string.IsNullOrWhiteSpace(row.Title) || row.Title.Length > 300)
                    throw new TaskValidationException("TASK001", "作业名称无效或超过 300 字，未保存本批数据。");
                var current = key is null ? null : Find(connection, transaction, "source_key", key);
                var task = current ?? new TaskItem
                {
                    Source = TaskSource.Chaoxing, SourceKey = key, CreatedAtUtc = now.ToUniversalTime(),
                    Title = row.Title, CourseName = string.IsNullOrWhiteSpace(courseName) ? "课程待补充" : courseName.Trim()
                };
                var recordingKey = verified is not null ? CourseRecordingRules.ChaoxingKey(verified.CourseId, verified.ClassId, verified.Cpi)
                    : CourseRecordingRules.TaskCourseKey(task);
                if (!CourseRecordingRules.IsSelected(recording, recordingKey)) continue;
                var detailText = task.DetailTimeText.Length > 0 ? task.DetailTimeText
                    : task.DisplayedTimeText.StartsWith("作答时间", StringComparison.Ordinal) ? task.DisplayedTimeText : "";
                task = task with
                {
                    Title = task.Overrides.HasFlag(TaskOverrides.Title) ? task.Title : row.Title,
                    CourseName = task.Overrides.HasFlag(TaskOverrides.Course) || string.IsNullOrWhiteSpace(courseName) ? task.CourseName : courseName.Trim(),
                    PlatformTitle = row.Title, PlatformStatusText = row.PlatformStatusText,
                    RecordingCourseKey = recordingKey,
                    DetailTimeText = detailText, DisplayedTimeText = detailText.Length > 0 ? detailText : row.DisplayedTimeText, CollectedAtUtc = now.ToUniversalTime(),
                    UpdatedAtUtc = now.ToUniversalTime(), Revision = current is null ? 1 : current.Revision + 1,
                    Link = task.Overrides.HasFlag(TaskOverrides.Link) ? task.Link : verified?.PageUri.OriginalString,
                    LinkIssue = task.Overrides.HasFlag(TaskOverrides.Link) ? task.LinkIssue : null
                };
                task = ChaoxingTaskMetadata.Apply(task, row.DisplayedTimeText, row.TimeObservedAtUtc ?? now);
                Write(connection, transaction, task); Reconcile(connection, transaction, task, now);
                if (current is null)
                {
                    added++;
                    if (notifications && TaskRules.IsActive(task)) InsertPlan(connection, transaction, ReminderPlanner.NewTask(task, now));
                }
                else updated++;
            }
            cancellation.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        Changed?.Invoke();
        return new(added, updated, unresolved, duplicates);
    }

    public TaskItem SaveDetail(WorkTaskLocation location, WorkDetailParseResult detail, DateTimeOffset now, CancellationToken cancellation = default,
        long? recordingRevision = null)
    {
        var key = ObservedWorkKey(WorkListParser.ParseTaskLocation(location.PageUri.OriginalString));
        if (key is null || detail.AnswerWindow is null)
            throw new TaskValidationException("LINK003", "无法把详情关联到已保存作业，请先读取并保存课程作业列表。");
        TaskItem result;
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var task = Find(connection, transaction, "source_key", key)
                ?? throw new TaskValidationException("LINK003", "清单中还没有这条作业，请先保存列表。");
            if (recordingRevision is { } revision && ReadCourseRecording(connection, transaction, "chaoxing").Revision != revision)
                throw new CourseRecordingChangedException();
            result = task with
            {
                DisplayedTimeText = detail.DisplayedWindowText, DetailTimeText = detail.DisplayedWindowText,
                DetailCollectedAtUtc = now.ToUniversalTime(), DetailCheckedAtUtc = now.ToUniversalTime(), CollectedAtUtc = now.ToUniversalTime(),
                UpdatedAtUtc = now.ToUniversalTime(), Revision = task.Revision + 1
            };
            result = ChaoxingTaskMetadata.Apply(result, task.PlatformListTimeText,
                task.PlatformListTimeObservedAtUtc ?? task.CollectedAtUtc ?? now, refreshWindow: true);
            Write(connection, transaction, result); Reconcile(connection, transaction, result, now);
            cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        Changed?.Invoke();
        return result;
    }

    public AppSettings LoadSettings()
    {
        lock (gate) { using var connection = Open(); return ReadSettings(connection, null); }
    }

    public TaskItem RecordDetailCheck(WorkTaskLocation location, DateTimeOffset now, CancellationToken cancellation = default,
        long? recordingRevision = null)
    {
        var key = ObservedWorkKey(location) ?? throw new TaskValidationException("LINK003", "无法确认任务详情归属。");
        TaskItem result;
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var task = Find(connection, transaction, "source_key", key)
                ?? throw new TaskValidationException("LINK003", "任务已删除，未保存详情检查结果。");
            if (recordingRevision is { } revision && ReadCourseRecording(connection, transaction, "chaoxing").Revision != revision)
                throw new CourseRecordingChangedException();
            result = task with { DetailCheckedAtUtc = now.ToUniversalTime(), Revision = task.Revision + 1 };
            Write(connection, transaction, result);
            cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        Changed?.Invoke();
        return result;
    }

    public void RecordSyncResult(ChaoxingSyncSummary summary, string outcome, DateTimeOffset now, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var settings = ReadSettings(connection, transaction) with
            { LastSyncAtUtc = now.ToUniversalTime(), LastSyncOutcome = outcome, LastSyncSummary = summary };
            Execute(connection, transaction, "INSERT INTO settings(key,value) VALUES('app',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                ("$value", JsonSerializer.Serialize(settings)));
            cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        Changed?.Invoke();
    }

    private static AppSettings ReadSettings(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT value FROM settings WHERE key='app';");
        if (command.ExecuteScalar() is not string json) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(json) ?? throw new JsonException(); }
        catch (JsonException) { throw new TaskValidationException("DB002", "设置格式不正确，请备份任务库后查看故障说明。"); }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (gate)
        {
            using var connection = Open();
            Execute(connection, null, "INSERT INTO settings(key,value) VALUES('app',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                ("$value", JsonSerializer.Serialize(settings)));
        }
        Changed?.Invoke();
    }

    private static void InsertPlan(SqliteConnection connection, SqliteTransaction transaction, ReminderPlan plan) =>
        Execute(connection, transaction, """
            INSERT OR IGNORE INTO reminders(id,task_id,kind,due_utc,deadline_utc)
            VALUES($id,$task,$kind,$due,$deadline);
            """, ("$id", plan.Id), ("$task", plan.TaskId), ("$kind", (int)plan.Kind),
            ("$due", plan.DueAtUtc.ToUnixTimeMilliseconds()), ("$deadline", plan.DeadlineUtc?.ToUnixTimeMilliseconds()));

    private static void Reconcile(SqliteConnection connection, SqliteTransaction transaction, TaskItem task, DateTimeOffset now)
    {
        var recording = ReadCourseRecording(connection, transaction, task.PlatformId);
        var recorded = CourseRecordingRules.IsSelected(recording, CourseRecordingRules.TaskCourseKey(task));
        var plans = recorded ? ReminderPlanner.TimedPlans(task, now) : [];
        using var command = Command(connection, transaction, "SELECT id,kind FROM reminders WHERE task_id=$task AND state!='requested';", ("$task", task.Id));
        var obsolete = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (!recorded || !TaskRules.IsActive(task)
                    || (ReminderKind)reader.GetInt32(1) != ReminderKind.NewTask && !plans.Any(plan => plan.Id == reader.GetString(0)))
                    obsolete.Add(reader.GetString(0));
        foreach (var id in obsolete) Execute(connection, transaction, "DELETE FROM reminders WHERE id=$id;", ("$id", id));
        foreach (var plan in plans) InsertPlan(connection, transaction, plan);
    }

    private static ReminderPlan ReadPlan(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        (ReminderKind)reader.GetInt32(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
        reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));

    public IReadOnlyList<DueReminder> TakeDue(DateTimeOffset now, int maximum = 10)
    {
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            if (!ReadSettings(connection, transaction).NotificationsEnabled) return [];
            using var command = Command(connection, transaction, """
                SELECT id,task_id,kind,due_utc,deadline_utc,attempts FROM reminders
                WHERE state!='requested' AND due_utc<=$now
                AND (lease_until_utc IS NULL OR lease_until_utc<=$now)
                AND (next_attempt_utc IS NULL OR next_attempt_utc<=$now)
                ORDER BY due_utc,id LIMIT $limit;
                """, ("$now", now.ToUnixTimeMilliseconds()), ("$limit", Math.Clamp(maximum, 1, 100)));
            var plans = new List<(ReminderPlan Plan, int Attempts)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) plans.Add((ReadPlan(reader), reader.GetInt32(5)));
            var result = new List<DueReminder>();
            foreach (var (plan, attempts) in plans)
            {
                var task = Find(connection, transaction, "id", plan.TaskId);
                if (task is null || !ReminderPlanner.IsCurrent(plan, task, now)
                    || !CourseRecordingRules.IsSelected(ReadCourseRecording(connection, transaction, task.PlatformId), CourseRecordingRules.TaskCourseKey(task)))
                { Execute(connection, transaction, "DELETE FROM reminders WHERE id=$id;", ("$id", plan.Id)); continue; }
                Execute(connection, transaction, "UPDATE reminders SET state='processing',lease_until_utc=$lease,attempts=attempts+1 WHERE id=$id;",
                    ("$lease", now.AddMinutes(2).ToUnixTimeMilliseconds()), ("$id", plan.Id));
                result.Add(new(plan, task, attempts + 1));
            }
            transaction.Commit();
            return result;
        }
    }

    public void RecordNotificationRequest(IReadOnlyList<string> ids, bool success, DateTimeOffset now)
    {
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            foreach (var id in ids)
                Execute(connection, transaction, """
                    UPDATE reminders SET state=$state,requested_utc=$requested,lease_until_utc=NULL,
                        next_attempt_utc=$retry,error_code=$error WHERE id=$id AND state='processing';
                    """, ("$state", success ? "requested" : "scheduled"),
                    ("$requested", success ? now.ToUnixTimeMilliseconds() : null),
                    ("$retry", success ? null : now.AddMinutes(1).ToUnixTimeMilliseconds()),
                    ("$error", success ? null : "NOTIFY001"), ("$id", id));
            transaction.Commit();
        }
    }

    public void ReleaseReminderClaims(IReadOnlyList<string> ids)
    {
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            foreach (var id in ids)
                Execute(connection, transaction, """
                    UPDATE reminders SET state='scheduled',lease_until_utc=NULL,attempts=MAX(0,attempts-1)
                    WHERE id=$id AND state='processing';
                    """, ("$id", id));
            transaction.Commit();
        }
    }

    public IReadOnlyList<ReminderHistory> LoadReminderHistory()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT id,task_id,kind,due_utc,deadline_utc,state,requested_utc,error_code,attempts FROM reminders ORDER BY due_utc DESC LIMIT 500;");
            using var reader = command.ExecuteReader();
            var items = new List<ReminderHistory>();
            while (reader.Read()) items.Add(new(ReadPlan(reader), reader.GetString(5),
                reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8)));
            return items;
        }
    }
}
