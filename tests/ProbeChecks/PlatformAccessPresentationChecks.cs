using AccessProbe;

internal static class PlatformAccessPresentationChecks
{
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var missing = new PlatformSessionInfo("pta", "not_saved", null, null);
        var ready = new PlatformSessionInfo("pta", "ready", now, now);
        var discovered = new CourseRecordingSettings
        {
            CatalogCheckedAtUtc = now,
            CatalogComplete = true,
            Courses = [new("synthetic-course", "合成课程", "")]
        };
        var catalog = new ConnectionReport("pta")
        {
            CheckedAtUtc = now,
            AccountVerified = true,
            CatalogCovered = true,
            CatalogOnly = true,
            Courses = 1
        };
        var selected = discovered with { SelectionConfirmed = true, RecordAllCourses = true };
        var synced = catalog with { CatalogOnly = false, SelectionConfirmed = true, SelectedCourses = 1, TaskListsCovered = true, Tasks = 3 };

        Check(PlatformAccessPresentation.For("pta", missing, new(), null, false, false)
            is { State: PlatformAccessState.NotConnected, PrimaryAction: "连接平台" }, "a fresh platform offers connection");
        var available = PlatformAccessPresentation.For("pta", ready, discovered, catalog, false, false);
        Check(available is { State: PlatformAccessState.CoursesAvailable, PrimaryAction: "选择课程" } && !available.NeedsAttention,
            "a verified catalog without selection stays a neutral user choice, never an attention item");
        Check(PlatformAccessPresentation.For("pta", ready, discovered, catalog, true, true)
            is { State: PlatformAccessState.Syncing, PrimaryAction: "查看进度" }, "active sync takes precedence over old warnings and course prompts");
        Check(PlatformAccessPresentation.For("pta", ready, discovered, catalog with { ProblemOutcomes = ["network_error"] }, false, true)
            is { State: PlatformAccessState.NeedsRetry, PrimaryAction: "重新连接" }, "failed check gives a recovery action");
        Check(PlatformAccessPresentation.For("pta", ready with { State = "expired" }, selected, synced, false, false)
            is { State: PlatformAccessState.NeedsRetry }, "an expired session takes precedence over old success");
        Check(PlatformAccessPresentation.For("pta", ready, selected, synced, false, false)
            is { State: PlatformAccessState.Synced, StatusTitle: "已同步 3 条任务", LastSyncedAtUtc: not null }, "complete sync presents actual task count and time");
        Check(PlatformAccessPresentation.For("pta", ready, selected, synced with { TaskListsCovered = false }, false, false)
            is { State: PlatformAccessState.NeedsRetry, PrimaryAction: "查看同步详情" }, "a verified account with incomplete task coverage is not routed to re-login");
        Check(PlatformAccessPresentation.For("pta", ready, discovered with { Courses = [] }, catalog with { Courses = 0 }, false, false)
            is { State: PlatformAccessState.PendingRefresh, StatusTitle: "暂未读到课程" }, "confirmed empty catalog is distinct from a failed or missing login");

        Console.WriteLine("PASS: 8 Windows platform access states and actions (synthetic only).");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Platform access: " + label);
    }
}
