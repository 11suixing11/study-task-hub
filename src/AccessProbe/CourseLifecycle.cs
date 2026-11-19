namespace AccessProbe;

public enum CourseLifecycleState { Unknown, Ongoing, Upcoming, Ended }
public enum CourseLifecycleEvidence { None, OfficialStatus, OfficialEndTime }
public enum CourseListFilter { HideEnded, All, EndedOnly }

public sealed record CourseLifecycle
{
    public CourseLifecycleState State { get; init; }
    public CourseLifecycleEvidence StateEvidence { get; init; }
    public DateTimeOffset? EndUtc { get; init; }
    public CourseLifecycleEvidence EndEvidence { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }
}

public static class CourseLifecycleRules
{
    public static bool IsValid(CourseLifecycle? value) => value is not null
        && Enum.IsDefined(value.State) && Enum.IsDefined(value.StateEvidence) && Enum.IsDefined(value.EndEvidence)
        && (value.State == CourseLifecycleState.Unknown ? value.StateEvidence == CourseLifecycleEvidence.None
            : value.StateEvidence == CourseLifecycleEvidence.OfficialStatus)
        && (value.EndUtc is null ? value.EndEvidence == CourseLifecycleEvidence.None
            : value.EndEvidence == CourseLifecycleEvidence.OfficialEndTime && value.EndUtc.Value.UtcDateTime.Year is >= 2000 and <= 2100)
        && (value.ObservedAtUtc is null || value.ObservedAtUtc != default(DateTimeOffset));

    public static CourseLifecycleState State(RecordingCourse course, DateTimeOffset now)
    {
        var value = course.Lifecycle;
        if (!IsValid(value)) return CourseLifecycleState.Unknown;
        // A fresh official state can reopen or extend a course; a past date must not overrule it.
        if (value.StateEvidence == CourseLifecycleEvidence.OfficialStatus) return value.State;
        return value.EndEvidence == CourseLifecycleEvidence.OfficialEndTime && value.EndUtc is { } end && end <= now
            ? CourseLifecycleState.Ended : CourseLifecycleState.Unknown;
    }

    public static bool IsEnded(RecordingCourse course, DateTimeOffset now) => State(course, now) == CourseLifecycleState.Ended;

    public static bool MatchesFilter(RecordingCourse course, CourseListFilter filter, DateTimeOffset now) => filter switch
    {
        CourseListFilter.HideEnded => !IsEnded(course, now),
        CourseListFilter.EndedOnly => IsEnded(course, now),
        _ => true
    };

    public static string Explanation(RecordingCourse course, DateTimeOffset now)
    {
        var value = course.Lifecycle;
        if (!IsValid(value)) return "结束状态待核对";
        if (value.StateEvidence == CourseLifecycleEvidence.OfficialStatus)
            return value.State switch
            {
                CourseLifecycleState.Ended => "官网已确认结课",
                CourseLifecycleState.Ongoing => "官网显示正在上课",
                CourseLifecycleState.Upcoming => "官网显示即将开课",
                _ => "结束状态待核对"
            };
        if (value.EndEvidence == CourseLifecycleEvidence.OfficialEndTime && value.EndUtc is { } end)
            return (end <= now ? "课程已于 " : "课程结束于 ") + end.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return "结束状态待核对";
    }

    public static CourseLifecycle FromOfficialState(CourseLifecycleState state) => state == CourseLifecycleState.Unknown ? new()
        : new() { State = state, StateEvidence = CourseLifecycleEvidence.OfficialStatus };

    public static CourseLifecycle FromOfficialEnd(DateTimeOffset? end) => end is null ? new()
        : new() { EndUtc = end.Value.ToUniversalTime(), EndEvidence = CourseLifecycleEvidence.OfficialEndTime };
}
