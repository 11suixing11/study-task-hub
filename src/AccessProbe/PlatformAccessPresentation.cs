namespace AccessProbe;

internal enum PlatformAccessState { NotConnected, PendingRefresh, CoursesAvailable, Syncing, Synced, NeedsRetry }

internal sealed record PlatformAccessPresentation(string PlatformName, PlatformAccessState State,
    string StatusTitle, string Description, string PrimaryAction, DateTimeOffset? LastSyncedAtUtc = null)
{
    public bool NeedsAttention => State is PlatformAccessState.NeedsRetry;

    public static PlatformAccessPresentation For(string id, PlatformSessionInfo session, CourseRecordingSettings recording,
        ConnectionReport? report, bool running, bool warning)
    {
        var name = PlatformCatalog.Name(id);
        // A check performed before the current login was saved cannot describe that login.
        if (report is not null && session.SavedAtUtc > report.CheckedAtUtc) report = null;

        var completed = report is { AccountVerified: true, SessionSaved: true, SessionAvailable: true, LoginRequired: false,
            CatalogCovered: true, Courses: > 0, CatalogOnly: false, SelectionConfirmed: true, SelectedCourses: > 0,
            TaskListsCovered: true, ProblemCodes.Count: 0, ProblemOutcomes.Count: 0 };
        var lastSynced = completed ? report?.CheckedAtUtc : null;

        if (running)
            return new(name, PlatformAccessState.Syncing, "正在同步", "正在读取课程和任务，已有任务仍可查看。", "查看进度", lastSynced);
        var usableReport = report is { AccountVerified: true, SessionSaved: true, SessionAvailable: true, LoginRequired: false };
        var usableFeedback = usableReport ? ConnectionFeedback.From(report!) : null;
        // PTA answer review and Wisdom Tree scope notes are neutral results; only real read failures retry.
        var expectedCoverageWarning = usableFeedback is { Warning: false }
            && report!.SupportedTaskListsCovered && !report.TaskListsCovered;
        if (session.State == "expired" || report is { LoginRequired: true })
            return new(name, PlatformAccessState.NeedsRetry, "需要重新登录", "在官网确认登录后可继续同步，已有任务保留。", "重新连接", lastSynced);
        if (!expectedCoverageWarning && (warning || usableReport &&
            (usableFeedback!.Warning || report!.ProblemCodes.Count > 0 || report.ProblemOutcomes.Count > 0)
            || session.CanReuse && recording.LastCatalogRefresh is { Outcome: "partial" or "error" }))
            return new(name, PlatformAccessState.NeedsRetry, "需要重试", "上次读取未完整完成，已有任务仍可查看。",
                warning ? "重新连接" : "查看同步详情", lastSynced);
        if (usableReport && expectedCoverageWarning)
            return new(name, PlatformAccessState.PendingRefresh, usableFeedback!.Title,
                usableFeedback.Explanation, "查看同步详情", report!.CheckedAtUtc);
        if (!session.CanReuse)
            return new(name, PlatformAccessState.NotConnected, "未接入", "连接后可在首页查看该平台支持的课程任务。", "连接平台");
        if (report is { AccountVerified: true, CatalogCovered: true, Courses: 0 })
            return new(name, PlatformAccessState.PendingRefresh, "暂未读到课程", "已确认登录，官网当前没有返回课程。", "查看平台");
        if (session.State == "ready" && recording.CatalogCheckedAtUtc.HasValue && recording.Courses.Count > 0
            && !recording.Courses.Any(course => CourseRecordingRules.IsSelected(recording, course.Key)))
            return new(name, PlatformAccessState.CoursesAvailable, "已接入，未选课程",
                "选择要记录的课程后，任务才会汇入首页；不选择不会提醒。", "选择课程");
        if (session.State == "ready" && completed && report is not null)
            return new(name, PlatformAccessState.Synced, $"已同步 {report.Tasks} 条任务", "同步结果已汇入首页。", "查看平台", lastSynced);
        return new(name, PlatformAccessState.PendingRefresh,
            session.State == "ready" ? "已登录，待同步" : "登录待确认", "可以查看或刷新此平台的接入进度。", "查看平台");
    }
}
