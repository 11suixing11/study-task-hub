namespace AccessProbe;

public enum TaskFilterState { All, Active, Pending, Completed, Ignored, Returned, NeedsReview }
public enum TaskFilterTime { All, Today, Next7Days, Next30Days, Overdue, Next24Hours, NoDeadline, Unknown, Custom }
public enum TaskFilterSort { Priority, Deadline, Updated }

public sealed record TaskFilterOptions
{
    public TaskFilterState State { get; init; }
    public TaskFilterTime Time { get; init; }
    public string? PlatformId { get; init; }
    public string? CourseKey { get; init; }
    public string Search { get; init; } = "";
    public TaskFilterSort Sort { get; init; }
    public DateOnly? FromDate { get; init; }
    public DateOnly? ToDate { get; init; }
}

public sealed record TaskCourseFilterOption(string Key, string Label, string PlatformId);

public static class TaskFiltering
{
    public static readonly IReadOnlyList<string> TimeLabels =
        ["全部时间", "今天截止", "未来 7 天", "未来 30 天", "已逾期", "24 小时内", "无截止时间", "时间未知", "自定义日期"];
    public static readonly IReadOnlyList<string> StateLabels =
        ["全部状态", "待办（含待核对）", "未完成", "已完成", "已忽略", "被打回", "待核对"];
    public static readonly IReadOnlyList<string> SortLabels = ["优先级排序", "截止时间排序", "最近更新"];

    public static string? ValidateRange(DateOnly? from, DateOnly? to) => from is null && to is null
        ? "请选择开始或结束日期，可只填写一端。"
        : from is { } start && to is { } end && start > end ? "开始日期不能晚于结束日期。" : null;

    public static string? Validate(TaskFilterOptions options) => options.Time == TaskFilterTime.Custom
        ? ValidateRange(options.FromDate, options.ToDate) : null;

    public static bool Matches(TaskItem task, TaskFilterOptions options, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (Validate(options) is not null || !MatchesState(task, options.State)
            || options.PlatformId is { Length: > 0 } platform && task.PlatformId != platform
            || options.CourseKey is { Length: > 0 } key && CourseRecordingRules.TaskCourseKey(task) != key)
            return false;
        var search = options.Search.Trim();
        if (search.Length > 0 && !new[] { task.Title, task.CourseName, PlatformCatalog.Name(task.PlatformId), task.Notes }
            .Any(text => text.Contains(search, StringComparison.OrdinalIgnoreCase))) return false;
        return MatchesTime(task, options, now, zone ?? TimeZoneInfo.Local);
    }

    public static bool MatchesState(TaskItem task, TaskFilterState state) => state switch
    {
        TaskFilterState.Active => TaskRules.IsActive(task),
        TaskFilterState.Pending => task.State == TaskState.Pending,
        TaskFilterState.Completed => task.State == TaskState.Completed,
        TaskFilterState.Ignored => task.State == TaskState.Ignored,
        TaskFilterState.Returned => task.State == TaskState.Returned,
        TaskFilterState.NeedsReview => task.State == TaskState.NeedsReview,
        _ => true
    };

    private static bool MatchesTime(TaskItem task, TaskFilterOptions options, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (options.Time == TaskFilterTime.All) return true;
        if (options.Time == TaskFilterTime.NoDeadline) return task.DeadlineKind == DeadlineKind.None;
        if (options.Time == TaskFilterTime.Unknown) return task.DeadlineKind != DeadlineKind.None && !TaskRules.HasUsableDeadline(task);
        if (!TaskRules.HasUsableDeadline(task) || task.DeadlineUtc is not { } deadline) return false;
        if (options.Time == TaskFilterTime.Overdue) return deadline < now;
        if (options.Time == TaskFilterTime.Next24Hours) return deadline >= now && deadline - now <= TimeSpan.FromHours(24);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(deadline, zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return options.Time switch
        {
            TaskFilterTime.Today => date == today,
            // Include the current day's remaining hours and the next six / twenty-nine local dates.
            TaskFilterTime.Next7Days => deadline >= now && date.DayNumber - today.DayNumber is >= 0 and < 7,
            TaskFilterTime.Next30Days => deadline >= now && date.DayNumber - today.DayNumber is >= 0 and < 30,
            TaskFilterTime.Custom => (!options.FromDate.HasValue || date >= options.FromDate.Value)
                && (!options.ToDate.HasValue || date <= options.ToDate.Value),
            _ => true
        };
    }

    public static IReadOnlyList<TaskItem> Apply(IEnumerable<TaskItem> tasks, TaskFilterOptions options, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var filtered = tasks.Where(task => Matches(task, options, now, zone));
        return (options.Sort switch
        {
            TaskFilterSort.Deadline => filtered.OrderBy(SortDeadline)
                .ThenBy(task => task.Title, StringComparer.CurrentCulture).ThenBy(task => task.Id, StringComparer.Ordinal),
            TaskFilterSort.Updated => filtered.OrderByDescending(task => task.UpdatedAtUtc).ThenBy(task => task.Id, StringComparer.Ordinal),
            _ => filtered.OrderBy(task => TaskRules.IsActive(task) ? 0 : 1).ThenBy(task => TaskRules.Priority(task, now))
                .ThenBy(SortDeadline)
                .ThenBy(task => task.Title, StringComparer.CurrentCulture).ThenBy(task => task.Id, StringComparer.Ordinal)
        }).ToArray();
    }

    private static DateTimeOffset SortDeadline(TaskItem task) => TaskRules.HasUsableDeadline(task)
        ? task.DeadlineUtc!.Value : DateTimeOffset.MaxValue;

    public static IReadOnlyList<TaskCourseFilterOption> Courses(IEnumerable<TaskItem> tasks, string? platformId = null)
    {
        var groups = tasks.Where(task => string.IsNullOrEmpty(platformId) || task.PlatformId == platformId)
            .GroupBy(CourseRecordingRules.TaskCourseKey).Select(group => new
            {
                Key = group.Key, Platform = group.First().PlatformId,
                Name = group.First().CourseName.Trim() is { Length: > 0 } name ? name : "未填写课程",
                Manual = group.All(task => task.Source == TaskSource.Manual)
            }).ToArray();
        var sameNameCounts = groups.GroupBy(course => (course.Platform, course.Name))
            .ToDictionary(group => group.Key, group => group.Count());
        return new[] { new TaskCourseFilterOption("", "全部课程", "") }.Concat(groups
            .OrderBy(course => PlatformCatalog.Name(course.Platform), StringComparer.CurrentCulture)
            .ThenBy(course => course.Name, StringComparer.CurrentCulture).ThenBy(course => course.Key, StringComparer.Ordinal)
            .Select(course => new TaskCourseFilterOption(course.Key,
                course.Name + " · " + PlatformCatalog.Name(course.Platform)
                + (sameNameCounts[(course.Platform, course.Name)] > 1
                    ? course.Manual ? "（手动记录）" : "（官网课程）" : ""), course.Platform))).ToArray();
    }
}
