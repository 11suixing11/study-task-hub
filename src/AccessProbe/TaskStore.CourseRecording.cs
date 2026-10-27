using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AccessProbe;

public sealed partial class TaskStore
{
    private static void CheckRecordingPlatform(string platformId)
    {
        if (!PlatformCatalog.Sites.Any(site => site.Id == platformId))
            throw new TaskValidationException("COURSE001", "请选择有效的平台。");
    }

    private static CourseRecordingSettings ReadCourseRecording(SqliteConnection connection, SqliteTransaction? transaction, string platformId)
    {
        using var command = Command(connection, transaction, "SELECT value FROM settings WHERE key=$key;", ("$key", "course-recording:" + platformId));
        if (command.ExecuteScalar() is not string json) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<CourseRecordingSettings>(json) ?? throw new JsonException();
            if (settings.Revision < 0 || settings.Courses is null || settings.SelectedCourseKeys is null
                || settings.SelectedCourseKeys.Any(key => !CourseRecordingRules.IsKey(platformId, key))
                || settings.Courses.Any(course => course is null || !CourseRecordingRules.IsKey(platformId, course.Key)
                    || string.IsNullOrWhiteSpace(course.Name) || course.Name.Length > 200 || course.Detail is null || course.Detail.Length > 200)
                || settings.Courses.Select(course => course.Key).Distinct(StringComparer.Ordinal).Count() != settings.Courses.Count)
                throw new JsonException();
            return settings;
        }
        catch (JsonException) { throw new TaskValidationException("DB002", "记录课程设置格式不正确，请先备份任务库。"); }
    }

    private static void WriteCourseRecording(SqliteConnection connection, SqliteTransaction transaction, string platformId, CourseRecordingSettings settings)
        => Execute(connection, transaction, "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            ("$key", "course-recording:" + platformId), ("$value", JsonSerializer.Serialize(settings)));

    public CourseRecordingSettings LoadCourseRecording(string platformId)
    {
        CheckRecordingPlatform(platformId);
        lock (gate) { using var connection = Open(); return ReadCourseRecording(connection, null, platformId); }
    }

    private IReadOnlyList<TaskItem> ReadRecordingTasks(SqliteConnection connection, SqliteTransaction? transaction, string platformId)
    {
        using var command = Command(connection, transaction, "SELECT id,revision,payload,protected_link FROM tasks WHERE json_extract(payload,'$.PlatformId')=$platform;", ("$platform", platformId));
        using var reader = command.ExecuteReader(); var tasks = new List<TaskItem>();
        while (reader.Read()) tasks.Add(ReadItem(reader));
        return tasks;
    }

    private static IReadOnlyList<RecordingCourse> RecordingCourses(CourseRecordingSettings settings, IReadOnlyList<TaskItem> tasks)
    {
        var courses = settings.Courses.ToDictionary(course => course.Key, StringComparer.Ordinal);
        foreach (var group in tasks.GroupBy(CourseRecordingRules.TaskCourseKey))
        {
            if (courses.ContainsKey(group.Key)) continue;
            var task = group.First(); var name = task.CourseName.Trim();
            courses.Add(group.Key, new(group.Key, name.Length > 0 ? name : "未填写课程",
                task.Source == TaskSource.Manual ? "手动录入" : "历史记录 · 待同步确认", false));
        }
        return courses.Values.OrderByDescending(course => course.Available).ThenBy(course => course.Name, StringComparer.CurrentCulture)
            .ThenBy(course => course.Detail, StringComparer.CurrentCulture).ThenBy(course => course.Key, StringComparer.Ordinal).ToArray();
    }

    public CourseRecordingSettings LoadCourseRecordingOverview(string platformId)
    {
        CheckRecordingPlatform(platformId);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var settings = ReadCourseRecording(connection, transaction, platformId);
            return settings with { Courses = RecordingCourses(settings, ReadRecordingTasks(connection, transaction, platformId)) };
        }
    }

    public CourseRecordingSettings ObserveRecordingCourses(string platformId, IReadOnlyList<DiscoveredRecordingCourse> observed,
        bool complete, DateTimeOffset now, CancellationToken cancellation = default)
    {
        CheckRecordingPlatform(platformId);
        if (observed.Any(item => !CourseRecordingRules.IsKey(platformId, item.Course.Key)
            || string.IsNullOrWhiteSpace(item.Course.Name) || item.Course.Name.Length > 200 || item.Course.Detail.Length > 200))
            throw new TaskValidationException("COURSE001", "课程信息无法确认，未更新记录课程设置。");
        CourseRecordingSettings result;
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var current = ReadCourseRecording(connection, transaction, platformId);
            var selectedKeys = current.SelectedCourseKeys.ToHashSet(StringComparer.Ordinal);
            var courses = current.Courses.ToDictionary(course => course.Key,
                course => complete ? course with { Available = false } : course, StringComparer.Ordinal);
            foreach (var item in observed) courses[item.Course.Key] = item.Course with { Available = true };
            // Upgrade old tasks by their verified task identity, never by their editable course name.
            foreach (var task in ReadRecordingTasks(connection, transaction, platformId).Where(task => task.Source != TaskSource.Manual && task.RecordingCourseKey.Length == 0))
            {
                cancellation.ThrowIfCancellationRequested();
                var matches = observed.Where(item => item.MatchesLegacyTask(task)).Select(item => item.Course.Key).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                if (matches.Length != 1) continue;
                if (selectedKeys.Contains(CourseRecordingRules.TaskCourseKey(task))) selectedKeys.Add(matches[0]);
                var bound = task with { RecordingCourseKey = matches[0], Revision = task.Revision + 1 };
                Write(connection, transaction, bound); Reconcile(connection, transaction, bound, now);
            }
            result = current with { Courses = courses.Values.ToArray(), SelectedCourseKeys = selectedKeys.ToArray(), CatalogCheckedAtUtc = now.ToUniversalTime(), CatalogComplete = complete };
            WriteCourseRecording(connection, transaction, platformId, result);
            foreach (var task in ReadRecordingTasks(connection, transaction, platformId)) Reconcile(connection, transaction, task, now);
            cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        Changed?.Invoke(); return result;
    }

    public CourseRecordingSettings SaveCourseRecording(string platformId, bool recordAll, IReadOnlyList<string> selectedKeys,
        bool hideUnselectedTasks, long expectedRevision, DateTimeOffset now)
    {
        CheckRecordingPlatform(platformId); CourseRecordingSettings result;
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var current = ReadCourseRecording(connection, transaction, platformId);
            if (current.Revision != expectedRevision) throw new TaskValidationException("COURSE002", "课程选择已在其他窗口更新，请刷新后再保存。");
            var tasks = ReadRecordingTasks(connection, transaction, platformId);
            var courses = RecordingCourses(current, tasks); var keys = selectedKeys.Distinct(StringComparer.Ordinal).ToArray();
            if (keys.Any(key => !courses.Any(course => course.Key == key)))
                throw new TaskValidationException("COURSE001", "选中的课程不属于当前平台，请刷新课程列表。");
            result = current with
            {
                Revision = checked(current.Revision + 1), RecordAllCourses = recordAll,
                SelectedCourseKeys = keys, HideUnselectedTasks = hideUnselectedTasks, Courses = courses
            };
            WriteCourseRecording(connection, transaction, platformId, result);
            foreach (var task in tasks) Reconcile(connection, transaction, task, now);
            transaction.Commit();
        }
        Changed?.Invoke(); return result;
    }

    public bool IsCourseRecorded(string platformId, string courseKey) => CourseRecordingRules.IsSelected(LoadCourseRecording(platformId), courseKey);

    public void CheckCourseRecordingRevision(string platformId, long expectedRevision, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (LoadCourseRecording(platformId).Revision != expectedRevision) throw new CourseRecordingChangedException();
    }

    public IReadOnlyList<TaskItem> LoadRecordedTasks()
    {
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var settings = PlatformCatalog.Sites.ToDictionary(site => site.Id, site => ReadCourseRecording(connection, transaction, site.Id), StringComparer.Ordinal);
            using var command = Command(connection, transaction, "SELECT id,revision,payload,protected_link FROM tasks ORDER BY id;");
            using var reader = command.ExecuteReader(); var tasks = new List<TaskItem>();
            while (reader.Read())
            {
                var task = ReadItem(reader);
                if (!settings.TryGetValue(task.PlatformId, out var recording) || CourseRecordingRules.IsVisible(recording, task)) tasks.Add(task);
            }
            return tasks;
        }
    }
}
