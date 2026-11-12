namespace AccessProbe.Linux;

public sealed record TaskInput(string Title, string PlatformId, string CourseName,
    string DeadlineKind, string? BeginIso, string? DeadlineIso, string? Link,
    decimal? ProgressPercent, string ScoreText, string Notes, int? PriorityOverride,
    bool ConfirmDeadline = false);
public sealed record TaskQuery(string? Platform = null, string State = "all", string Search = "", string Sort = "priority",
    int Offset = 0, int Limit = 100, bool IncludeUnselected = false);
public sealed record LinuxTaskView(TaskItem Task, string StateLabel, string DeadlineLabel, bool CanComplete, int Priority, string? LinkIssue);
public sealed record DashboardCounts(int Pending, int Completed, int Ignored, int Returned, int NeedsReview, int DueSoon, int Overdue);
public sealed record TaskPage(long SnapshotVersion, int Offset, int Total, int FilteredTotal, int HiddenTotal, DashboardCounts Summary,
    IReadOnlyList<LinuxTaskView> Items, IReadOnlyDictionary<string,int> PlatformCounts);
public sealed record TaskDetail(LinuxTaskView View, string? Link);
