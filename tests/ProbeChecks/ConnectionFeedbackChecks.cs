using AccessProbe;
using Microsoft.Data.Sqlite;

internal static class ConnectionFeedbackChecks
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidDataException("Connection feedback: " + label); checks++; }
        var ready = new ConnectionReport("chaoxing") { AccountVerified = true, CatalogCovered = true, Courses = 2, CatalogOnly = true };
        var waiting = ConnectionFeedback.From(ready with { AccountVerified = false });
        Check(waiting.Warning && !waiting.CanChooseCourses && waiting.Title.Contains("尚未确认"), "a saved browser login does not prove account verification");
        Check(ConnectionFeedback.Waiting().Explanation.Contains("确认授权") && ConnectionFeedback.Waiting().NextStep.Contains(ConnectionFeedback.CheckLabel),
            "scan guidance explains official authorization and gives an explicit check action");
        var rejected = ConnectionFeedback.From(ready with { LoginRequired = true });
        Check(rejected.Warning && !rejected.CanChooseCourses && rejected.Title.Contains("官网仍要求登录"), "official rejection overrides prior verification");
        Check(rejected.Explanation.Contains("无法判断") && !rejected.Title.Contains("过期"), "a login response cannot diagnose whether a scan failed or expired");
        Check(ConnectionFeedback.From(ready with { SessionAvailable = false }).Title == "尚未接入", "missing session has its own result");
        var cases = new (string Outcome, string Explanation)[]
        {
            ("network_error", "连接官网失败"), ("timeout", "超时"), ("forbidden", "访问权限"), ("rate_limited", "请求频率"),
            ("verification_required", "额外验证"), ("unsupported_redirect", "跳转"), ("schema_unrecognized", "尚未识别")
        };
        foreach (var item in cases)
        {
            var feedback = ConnectionFeedback.From(ready with { CatalogCovered = false, Courses = 0,
                ProblemCodes = ["SYNC003"], ProblemOutcomes = [item.Outcome] });
            Check(feedback.Title == "登录已确认，课程读取未完成" && feedback.Explanation.Contains(item.Explanation), "verified account and failed catalog: " + item.Outcome);
            Check(feedback.Warning && !feedback.CanChooseCourses && feedback.Explanation.Contains("不能视为没有课程"), "incomplete catalog is not an empty catalog: " + item.Outcome);
        }
        var partial = ConnectionFeedback.From(ready with { CatalogCovered = false, ProblemOutcomes = ["network_error"] });
        Check(partial.CanChooseCourses && partial.Explanation.Contains("可能不完整"), "usable partial courses remain selectable with a coverage explanation");
        var empty = ConnectionFeedback.From(ready with { Courses = 0 });
        Check(empty.Title.Contains("官网当前没有课程") && !empty.Warning && !empty.CanChooseCourses, "a verified empty catalog is explained without a useless selection action");
        var choose = ConnectionFeedback.From(ready);
        Check(choose.Title == "已接入，待选择课程" && choose.CanChooseCourses && choose.Explanation.Contains("任务才会进入首页"), "login alone explains why the dashboard has no tasks");
        Check(ConnectionFeedback.From(ready with { SelectionConfirmed = true }).Title.Contains("尚未选择同步课程"), "explicitly choosing no courses is distinct from missing login");
        Check(ConnectionFeedback.From(ready with { SelectionConfirmed = true, SelectedCourses = 1 }).Title == "已接入，课程已就绪", "catalog checking does not claim tasks were synchronized");
        var full = ready with { CatalogOnly = false, SelectionConfirmed = true, SelectedCourses = 1, TaskListsCovered = true, Tasks = 0 };
        Check(ConnectionFeedback.From(full).Explanation.Contains("官网没有返回任务") && !ConnectionFeedback.From(full).Warning, "zero tasks is valid only with confirmed task coverage");
        var incomplete = ConnectionFeedback.From(full with { TaskListsCovered = false });
        Check(incomplete.Title.Contains("任务同步未完成") && incomplete.Warning && incomplete.Explanation.Contains("已有记录保留"), "zero from an incomplete task read is not declared empty");
        var unsaved = ConnectionFeedback.From(ready with { SessionSaved = false });
        Check(unsaved.Title.Contains("保存未完成") && unsaved.Warning && !unsaved.CanChooseCourses, "failed persistence explains re-login and withholds the next action");
        Check(ConnectionFeedback.PageFailed("certificate_failed").Explanation.Contains("证书") && ConnectionFeedback.PageFailed("blank_page").Explanation.Contains("网页组件"), "browser failures retain their real categories");
        Check(ConnectionFeedback.Interrupted(true).Title.Contains("已停止") && ConnectionFeedback.Interrupted(false, true).Explanation.Contains("先前"), "cancel and recheck failure preserve phase meaning");
        var uai = ConnectionFeedback.From(new("uai") { AccountVerified = true });
        Check(uai.Title == "官网账号已确认" && uai.Explanation.Contains("尚未接入") && !uai.CanChooseCourses, "UAI preview never promises automatic task collection");
        Check(ConnectionFeedback.ForPlatformManagement(ready).NextStep.Contains("保存选择并采集")
            && !ConnectionFeedback.ForPlatformManagement(ready).NextStep.Contains(ConnectionFeedback.ChooseLabel),
            "course manager directs selection to its actual save action");
        Check(ConnectionFeedback.ForPlatformManagement(ready with { SelectionConfirmed = true, SelectedCourses = 1 }).NextStep.Contains("同步已选课程"),
            "saved course selection exposes the available synchronization action");
        var mobileRetry = ConnectionFeedback.ForPlatformManagement(ready with { AccountVerified = false, ProblemOutcomes = ["captcha_required"] }, "刷新课程");
        Check(mobileRetry.NextStep.Contains("刷新课程") && !mobileRetry.Text.Contains("下方") && !mobileRetry.Text.Contains(ConnectionFeedback.CheckLabel),
            "mobile platform manager does not refer to login-page controls or an absent browser");
        Check(ConnectionFeedback.ForPlatformManagement(ready with { Courses = 0 }, "刷新课程").NextStep.Contains("刷新课程"),
            "verified empty catalog offers the existing refresh action");

        var recording = new CourseRecordingSettings { Courses = [new("first", "课程一", ""), new("second", "课程二", "")] };
        var chaoxing = new ChaoxingSyncSummary(2, 0, 0, 0, 0, 0, 0, false, true, []) { AccountVerified = true, SelectedCourses = 0 };
        var rain = new RainSyncSummary("yuketang", 2, 0, 0, 0, 0, false, true, []) { AccountVerified = true, SelectedCourses = 0 };
        var mooc = new MoocSyncSummary(2, 0, 0, 0, 0, false, true, []) { AccountVerified = true, SelectedCourses = 0 };
        var xuetang = new XuetangSyncSummary(2, 0, 0, 0, 0, false, true, []) { AccountVerified = true, SelectedCourses = 0 };
        var metadata = new MetadataSyncSummary("pta", 2, 0, 0, 0, 0, 0, false, true, []) { AccountVerified = true };
        var reports = new[] { ConnectionReports.From(chaoxing, recording, true), ConnectionReports.From(rain, recording, true),
            ConnectionReports.From(mooc, recording, true), ConnectionReports.From(xuetang, recording, true), ConnectionReports.From(metadata, recording, true) };
        Check(reports.All(report => report.SelectedCourses == 0 && !report.TaskListsCovered), "all adapters distinguish discovered courses from selected courses and tasks");
        Check(reports.Select(ConnectionFeedback.From).All(feedback => feedback.Title == choose.Title && feedback.Explanation == choose.Explanation), "Windows and Android adapters share the same stage wording");
        var selected = recording with { SelectionConfirmed = true, SelectedCourseKeys = ["first"] };
        Check(ConnectionReports.From(xuetang, selected, true).SelectedCourses == 1 && ConnectionReports.From(metadata, selected, true).SelectedCourses == 1,
            "catalog-only results use the saved selection rather than the number of task reads");
        var blocked = ConnectionReports.From(rain with { CourseListCovered = false,
            Problems = [new("RAIN003", "synthetic-account", "synthetic-token") { Outcome = "forbidden" }] }, selected, true);
        Check(blocked.ProblemOutcomes.SequenceEqual(["forbidden"]) && ConnectionFeedback.From(blocked).Explanation.Contains("访问权限"), "structured failure category survives service-to-feedback mapping");

        var root = Path.Combine(Path.GetTempPath(), "connection-feedback-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var store = new TaskStore(Path.Combine(root, "tasks.db"));
        Check(store.LoadConnectionReport("chaoxing") is null, "fresh platform has no invented prior success");
        var saved = store.SaveConnectionReport(ready with { Courses = -1, Tasks = int.MaxValue,
            ProblemCodes = ["SYNC003", "token=synthetic-private", "SYNC003"],
            ProblemOutcomes = ["network_error", "synthetic_private_account", "https://private.invalid/?token=synthetic", "network_error"] });
        Check(saved.Courses == 0 && saved.Tasks == 1000000 && saved.ProblemCodes.SequenceEqual(["SYNC003"])
            && saved.ProblemOutcomes.SequenceEqual(["network_error"]), "persistence keeps bounded facts and recognized diagnostic categories only");
        using (var db = new SqliteConnection("Data Source=" + store.DatabasePath))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key='connection-report:chaoxing'";
            var text = (string)command.ExecuteScalar()!;
            Check(!text.Contains("synthetic_private_account") && !text.Contains("token=") && !text.Contains("https:"), "report storage contains no free-form account or credential text");
        }
        var reopened = new TaskStore(store.DatabasePath);
        Check(reopened.LoadConnectionReport("chaoxing") is { AccountVerified: true, CatalogCovered: true }, "last check survives reopening the app");
        store.SaveConnectionReport(new("pta") { AccountVerified = true });
        var manual = store.Create(new("保留的手动任务", "chaoxing", "", DeadlineKind.Unknown, null, null, null, null, "", "", null), DateTimeOffset.UtcNow);
        store.ClearPlatformSession("chaoxing");
        Check(reopened.LoadConnectionReport("chaoxing") is null && reopened.LoadConnectionReport("pta") is not null && reopened.Get(manual.Id) is not null,
            "clearing login removes stale success while preserving other platform feedback and tasks");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { store.SaveConnectionReport(ready, cancellation.Token); throw new InvalidDataException("Cancelled report was written."); }
        catch (OperationCanceledException) { Check(store.LoadConnectionReport("chaoxing") is null, "cancelled verification cannot overwrite the latest result"); }
        CheckWindowsActions(ready, Check);
        CheckMetadataCoverage(recording, Check);
        Console.WriteLine($"PASS: {checks} connection stage, actionable failure, cross-client wording and secret-free report persistence checks (synthetic only).");
    }

    private static void CheckMetadataCoverage(CourseRecordingSettings recording, Action<bool, string> check)
    {
        var selected = recording with { SelectionConfirmed = true, RecordAllCourses = true };
        var pta = new MetadataSyncSummary("pta", 6, 0, 2, 14, 0, 14, false, true,
            [new("PTA003", "", "合成答卷待核对") { Outcome = "partial_tasks" }])
        { AccountVerified = true, TaskListCoursesRead = 2, StatusNeedsReview = 14 };
        var smart = new MetadataSyncSummary("zhihuishu", 3, 1, 1, 5, 0, 5, false, true,
            [new("ZHS006", "", "合成范围说明") { Outcome = "smart_other_tasks_unverified" }])
        { AccountVerified = true, FullTaskListsCovered = true, SmartOtherTasks = 6 };
        var root = Path.Combine(Path.GetTempPath(), "metadata-coverage-" + Guid.NewGuid().ToString("N"));
        var store = new TaskStore(Path.Combine(root, "tasks.db"));
        ConnectionReport RoundTrip(MetadataSyncSummary summary)
        {
            store.SaveConnectionReport(ConnectionReports.From(summary, selected, false));
            return store.LoadConnectionReport(summary.PlatformId)!;
        }
        var ptaReport = RoundTrip(pta);
        var ptaFeedback = WindowsConnectionFeedback.For(ptaReport);
        check(!ptaReport.TaskListsCovered && ptaReport.SupportedTaskListsCovered && !ptaFeedback.Feedback.Warning
            && ptaFeedback.Primary == WindowsConnectionAction.RetryTasks
            && ptaFeedback.Feedback.Title == "已接入，题目集已读取，答卷待核对"
            && ptaFeedback.Feedback.Explanation.Contains("14 条") && ptaFeedback.Feedback.Explanation.Contains("答卷")
            && !ptaFeedback.Feedback.Explanation.Contains("重新登录", StringComparison.Ordinal)
            && ptaFeedback.Feedback.NextStep.Contains("重试同步", StringComparison.Ordinal),
            "completed PTA lists report imported tasks and unverified answers, not a login or list failure");
        var smartReport = RoundTrip(smart);
        var smartFeedback = WindowsConnectionFeedback.For(smartReport);
        check(!smartReport.TaskListsCovered && smartReport.SupportedTaskListsCovered && !smartFeedback.Feedback.Warning
            && smartFeedback.Primary == WindowsConnectionAction.ReturnHome
            && smartFeedback.Feedback.Title == "已接入，支持范围任务已同步"
            && smartFeedback.Feedback.Explanation.Contains("5 条") && smartFeedback.Feedback.Explanation.Contains("另有 6 条")
            && !smartFeedback.Feedback.Explanation.Contains("登录", StringComparison.Ordinal),
            "complete supported Wisdom Tree lists return home while reporting the separate coverage limit");
        check(WindowsConnectionFeedback.For(RoundTrip(pta with { TaskListCoursesRead = 1 })).Feedback.Warning,
            "incomplete PTA lists cannot be described as answer review only");
        var mixedSmart = smart with { Problems = [.. smart.Problems, new("ZHS002", "", "合成网络错误") { Outcome = "network_error" }] };
        var mixedFeedback = WindowsConnectionFeedback.For(RoundTrip(mixedSmart));
        check(mixedFeedback.Feedback.Warning && mixedFeedback.Primary == WindowsConnectionAction.RetryTasks
            && mixedFeedback.Feedback.Title == "已接入，任务同步未完成",
            "a real network error takes precedence over Wisdom Tree's out-of-scope coverage note");
        var ptaRowCheck = new MetadataSyncSummary("pta", 6, 2, 2, 14, 0, 14, false, true, [])
        { AccountVerified = true, FullTaskListsCovered = true, TaskListCoursesRead = 2, StatusNeedsReview = 14 };
        var ptaRowReport = ConnectionReports.From(ptaRowCheck, selected, false);
        var failCheck = new Action<bool, string>((condition, label) => check(condition, label));
        failCheck(!ptaRowReport.TaskListsCovered && ptaRowReport.SupportedTaskListsCovered && ptaRowReport.StatusNeedsReview == 14,
            "PTA complete list requests keep imported task evidence separate from answer status review");
        var smartRowCheck = new MetadataSyncSummary("zhihuishu", 3, 1, 1, 5, 0, 5, false, true, [])
        { AccountVerified = true, FullTaskListsCovered = true, SmartOtherTasks = 6 };
        var smartRowReport = ConnectionReports.From(smartRowCheck, selected, false);
        failCheck(smartRowReport.TaskListsCovered && smartRowReport.SupportedTaskListsCovered && smartRowReport.SmartOtherTasks == 6,
            "complete smart homework and exam lists are independent of unsupported extra-task warnings");
        var uncoveredSmart = ConnectionReports.From(smartRowCheck with { FullTaskListsCovered = false }, selected, false);
        failCheck(!uncoveredSmart.SupportedTaskListsCovered,
            "smart scope note must not mask an actual supported task-list failure");
        foreach (var summary in new[] { pta, smart })
        {
            var expired = WindowsConnectionFeedback.For(RoundTrip(summary with { LoginRequired = true }));
            check(expired.Feedback.Warning && expired.Primary == WindowsConnectionAction.RefreshLogin,
                summary.PlatformId + " confirmed rejection takes precedence over coverage notes");
        }
    }

    private static void CheckWindowsActions(ConnectionReport ready, Action<bool, string> check)
    {
        var login = WindowsConnectionFeedback.For(ready with { LoginRequired = true });
        check(login.Primary == WindowsConnectionAction.RefreshLogin && login.Feedback.NextStep.Contains("刷新登录页"), "rejected login has a direct browser recovery action");
        var network = WindowsConnectionFeedback.For(ready with { AccountVerified = false, ProblemOutcomes = ["network_error"] });
        check(network.Primary == WindowsConnectionAction.Check && network.Feedback.Explanation.Contains("网络"), "unconfirmed account with network failure offers to check again");
        var empty = WindowsConnectionFeedback.For(ready with { Courses = 0 });
        check(empty.Primary == WindowsConnectionAction.RetryCatalog && !empty.OfferCourseChoice && empty.Feedback.Title.Contains("没有课程"), "empty official catalog can be reread without asking for a course selection");
        var partial = WindowsConnectionFeedback.For(ready with { CatalogCovered = false, ProblemOutcomes = ["network_error"] });
        check(partial.Primary == WindowsConnectionAction.RetryCatalog && partial.OfferCourseChoice && partial.Feedback.Explanation.Contains("可能不完整"), "partial catalog has one recovery action and keeps usable selection available");
        check(WindowsConnectionFeedback.For(ready).Primary == WindowsConnectionAction.ChooseCourses, "verified catalog prioritizes course confirmation");
        var incomplete = ready with { CatalogOnly = false, SelectionConfirmed = true, SelectedCourses = 1 };
        check(WindowsConnectionFeedback.For(incomplete).Primary == WindowsConnectionAction.RetryTasks, "incomplete task sync retries the task read");
        check(WindowsConnectionFeedback.PageFailed("certificate_failed").Primary == WindowsConnectionAction.ReopenLogin, "browser failure reopens the page instead of checking a failed load");
    }
}
