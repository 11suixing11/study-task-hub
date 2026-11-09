using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public sealed record RecordingCourse(string Key, string Name, string Detail, bool Available = true);

// Matchers exist only during discovery. Account and remote identifiers are never persisted in the catalog.
public sealed record DiscoveredRecordingCourse(RecordingCourse Course, Func<TaskItem, bool> MatchesLegacyTask);

public sealed record CourseCatalogRefresh(DateTimeOffset CheckedAtUtc, string Outcome, IReadOnlyList<string> ProblemCodes);

public sealed record CourseRecordingSettings
{
    public long Revision { get; init; }
    public bool SelectionConfirmed { get; init; }
    public bool RecordAllCourses { get; init; }
    public bool HideUnselectedTasks { get; init; } = true;
    public IReadOnlyList<string> SelectedCourseKeys { get; init; } = [];
    public IReadOnlyList<RecordingCourse> Courses { get; init; } = [];
    public DateTimeOffset? CatalogCheckedAtUtc { get; init; }
    public bool CatalogComplete { get; init; }
    public CourseCatalogRefresh? LastCatalogRefresh { get; init; }
}

public sealed class CourseRecordingChangedException() : OperationCanceledException("Course recording selection changed.");

public static class CourseRecordingRules
{
    public static bool SupportsAutomatic(string platformId) => platformId is "chaoxing" or "yuketang" or "changjiang" or "mooc" or "xuetang" or "ucampus" or "zhihuishu" or "pta";

    public static bool IsCatalogProblemCode(string? code) => code is not null
        && new[] { "SYNC", "RAIN", "MOOC", "XT", "UC", "FIF", "ZHS", "PTA", "SESSION", "DB", "COURSE" }.Any(prefix =>
            code.StartsWith(prefix, StringComparison.Ordinal) && code.Length == prefix.Length + 3
            && code[prefix.Length..].All(char.IsAsciiDigit));

    public static bool IsCatalogRefresh(CourseCatalogRefresh refresh) => refresh.CheckedAtUtc != default
        && refresh.Outcome is "completed" or "partial" or "login_required" or "error"
        && refresh.ProblemCodes is not null && refresh.ProblemCodes.Count <= 16
        && refresh.ProblemCodes.All(IsCatalogProblemCode);

    public static string CatalogRefreshOutcome(bool covered, bool loginRequired, IReadOnlyList<string> codes)
        => loginRequired ? "login_required" : covered && codes.Count == 0 ? "completed" : "partial";

    public static string CatalogRefreshText(CourseRecordingSettings settings)
    {
        if (settings.LastCatalogRefresh is not { } refresh) return "课程列表尚无刷新结果。";
        var result = refresh.Outcome switch
        {
            "completed" => "课程列表已完整刷新",
            "login_required" => "课程刷新需要官方登录，已有课程与选择保留",
            "partial" => "课程刷新未完整完成，已有课程与选择保留",
            _ => "课程刷新失败，已有课程与选择保留"
        };
        return result + " · " + refresh.CheckedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            + (refresh.ProblemCodes.Count > 0 ? " · " + string.Join("、", refresh.ProblemCodes) : "");
    }

    private static string Key(string platformId, params string[] identity) => platformId + ":"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))));

    public static bool IsKey(string platformId, string? key) => key is not null && key.StartsWith(platformId + ":", StringComparison.Ordinal)
        && key.Length == platformId.Length + 65 && key[(platformId.Length + 1)..].All(char.IsAsciiHexDigit);

    public static string ChaoxingKey(string courseId, string classId, string cpi) => Key("chaoxing", "official", courseId, classId, cpi);
    public static string RainKey(string platformId, string host, string accountId, string classroomId)
        => Key(platformId, "official", host.ToLowerInvariant(), accountId, classroomId);
    public static string MoocKey(string accountId, string courseId, string termId) => Key("mooc", "official", accountId, courseId, termId);
    public static string XuetangKey(string accountId, string classroomId, string productSign, string courseSign)
        => Key("xuetang", "official", accountId, classroomId, productSign, courseSign);
    public static string MetadataKey(string platformId, string accountId, string courseId, string kind)
        => Key(platformId, "official", accountId, kind, courseId);
    public static DiscoveredRecordingCourse Metadata(string platformId, string accountId, MetadataCourse course)
    {
        var key = MetadataKey(platformId, accountId, course.Id, course.Kind);
        return new(new(key, course.Name, course.Detail), task => false);
    }

    public static string TaskCourseKey(TaskItem task) => IsKey(task.PlatformId, task.RecordingCourseKey) ? task.RecordingCourseKey
        : Key(task.PlatformId, task.Source == TaskSource.Manual ? "manual" : "legacy", task.CourseName.Trim());

    public static bool IsSelected(CourseRecordingSettings settings, string courseKey) => settings.SelectionConfirmed
        && (settings.RecordAllCourses || settings.SelectedCourseKeys.Contains(courseKey, StringComparer.Ordinal));

    public static bool IsVisible(CourseRecordingSettings settings, TaskItem task) => !settings.SelectionConfirmed || !settings.HideUnselectedTasks
        || IsSelected(settings, TaskCourseKey(task));

    public static DiscoveredRecordingCourse Chaoxing(ChaoxingCourse course) => new(
        new(ChaoxingKey(course.CourseId, course.ClassId, course.Cpi), course.Name, "班级 " + course.ClassId),
        task => task.Source == TaskSource.Chaoxing && WorkListParser.ParseTaskLocation(task.Link) is { } location
            && TaskStore.ObservedWorkKey(location) == task.SourceKey && ChaoxingSyncParser.SameCourse(location, course));

    public static DiscoveredRecordingCourse Rain(RainPlatform platform, string host, string accountId, RainClassroom course)
    {
        var key = RainKey(platform.Id, host, accountId, course.Id);
        return new(new(key, course.CourseName, course.Name.Length > 0 ? course.Name : "班级 " + course.Id),
            task => RainTaskNavigation.RecordingCourseKey(task, accountId, course.Id) == key);
    }

    public static DiscoveredRecordingCourse Mooc(string accountId, MoocCourse course)
    {
        var key = MoocKey(accountId, course.Id, course.TermId);
        return new(new(key, course.Name, "开课期 " + course.TermId + (course.IsSpoc ? " · SPOC" : "")),
            task => MoocTaskNavigation.RecordingCourseKey(task, accountId) == key);
    }

    public static DiscoveredRecordingCourse Xuetang(string accountId, XuetangCourse course)
    {
        var key = XuetangKey(accountId, course.ClassroomId, course.ProductSign, course.CourseSign);
        var status = course.Status switch { 1 => "正在上课", 2 => "即将开课", 3 => "已结课", _ => "状态待核对" };
        return new(new(key, course.Name, status + (course.Stowed ? " · 官网已收起" : "")),
            task => XuetangTaskNavigation.RecordingCourseKey(task, accountId) == key);
    }
}
