using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public sealed record RecordingCourse(string Key, string Name, string Detail, bool Available = true);

// Matchers exist only during discovery. Account and remote identifiers are never persisted in the catalog.
public sealed record DiscoveredRecordingCourse(RecordingCourse Course, Func<TaskItem, bool> MatchesLegacyTask);

public sealed record CourseRecordingSettings
{
    public long Revision { get; init; }
    public bool RecordAllCourses { get; init; } = true;
    public bool HideUnselectedTasks { get; init; } = true;
    public IReadOnlyList<string> SelectedCourseKeys { get; init; } = [];
    public IReadOnlyList<RecordingCourse> Courses { get; init; } = [];
    public DateTimeOffset? CatalogCheckedAtUtc { get; init; }
    public bool CatalogComplete { get; init; }
}

public sealed class CourseRecordingChangedException() : OperationCanceledException("Course recording selection changed.");

public static class CourseRecordingRules
{
    public static bool SupportsAutomatic(string platformId) => platformId is "chaoxing" or "yuketang" or "changjiang" or "mooc";

    private static string Key(string platformId, params string[] identity) => platformId + ":"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))));

    public static bool IsKey(string platformId, string? key) => key is not null && key.StartsWith(platformId + ":", StringComparison.Ordinal)
        && key.Length == platformId.Length + 65 && key[(platformId.Length + 1)..].All(char.IsAsciiHexDigit);

    public static string ChaoxingKey(string courseId, string classId, string cpi) => Key("chaoxing", "official", courseId, classId, cpi);
    public static string RainKey(string platformId, string host, string accountId, string classroomId)
        => Key(platformId, "official", host.ToLowerInvariant(), accountId, classroomId);
    public static string MoocKey(string accountId, string courseId, string termId) => Key("mooc", "official", accountId, courseId, termId);

    public static string TaskCourseKey(TaskItem task) => IsKey(task.PlatformId, task.RecordingCourseKey) ? task.RecordingCourseKey
        : Key(task.PlatformId, task.Source == TaskSource.Manual ? "manual" : "legacy", task.CourseName.Trim());

    public static bool IsSelected(CourseRecordingSettings settings, string courseKey) => settings.RecordAllCourses
        || settings.SelectedCourseKeys.Contains(courseKey, StringComparer.Ordinal);

    public static bool IsVisible(CourseRecordingSettings settings, TaskItem task) => !settings.HideUnselectedTasks
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
}
