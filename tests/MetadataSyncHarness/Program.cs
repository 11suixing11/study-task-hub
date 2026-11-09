using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Data = PlatformMetadataTestData.Fixtures;
using U = PlatformMetadataTestData.UcampusFixtures;

namespace MetadataSyncHarness;

internal static partial class Program
{
    private static Application app = null!;
    private static TaskDashboardWindow dashboard = null!;
    private static TaskStore store = null!;
    private static string runRoot = "";
    private static int checks;
    private static string lastCheck = "startup";
    private static readonly Dictionary<string, MetadataSyncService> services = [];
    private static readonly Dictionary<string, Fixtures> fixtures = [];
    private static readonly Dictionary<string, WebView2> browsers = [];
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--session-child") return SessionRestartChecks.RunChild(args[1], args[2], args[3]);
        if (args.Length != 3 || args[0] != "--project-root" || args[2] != "--verify") return 2;
        var projectRoot = Path.GetFullPath(args[1]); if (!File.Exists(Path.Combine(projectRoot, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var harnessRoot = Path.Combine(projectRoot, ".local", "metadata-sync-harness");
        runRoot = Path.Combine(harnessRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot); File.WriteAllText(Path.Combine(harnessRoot, "latest-run.txt"), runRoot);
        Environment.SetEnvironmentVariable("TEMP", runRoot); Environment.SetEnvironmentVariable("TMP", runRoot);
        // Match the other desktop harnesses so offscreen glyphs render consistently.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        store = FixtureTaskStore.Create(Path.Combine(runRoot, "tasks.db"), pendingPlatforms: ["ucampus", "zhihuishu"]); store.SaveSettings(new(false, true));
        store.Create(new("合成手动任务", "fif", "演示课程", DeadlineKind.Known, null, DateTimeOffset.UtcNow.AddDays(1), "https://example.invalid/synthetic-task", null, "", "合成验收数据", null), DateTimeOffset.UtcNow);
        dashboard = new TaskDashboardWindow(store, runRoot, () => { }, () => { }, _ => checks++) { Title = "平台接入 · 本地合成验收" };
        foreach (var platform in new[] { MetadataPlatform.Ucampus, MetadataPlatform.Zhihuishu })
        {
            var prefix = platform.Id == "ucampus" ? "Ucampus" : platform.Id == "fif" ? "Fif" : "Zhihuishu";
            var browser = (WebView2)dashboard.FindName(prefix + "SessionBrowser"); var fixture = new Fixtures(platform); fixture.BlockBrowser(browser);
            browsers[platform.Id] = browser; fixtures[platform.Id] = fixture;
            var service = new MetadataSyncService(platform, store, runRoot, browser, fixture.CreateHandler, TimeSpan.FromMilliseconds(50),
                platform.Id == "zhihuishu" ? (ReadOnlyMemory<byte>?)Data.Key : null);
            services[platform.Id] = service; dashboard.AttachMetadataSync(service); store.SaveMetadataSettings(platform.Id, new() { Connected = true });
        }
        app.MainWindow = dashboard; dashboard.Loaded += async (_, _) => await VerifyAsync(); app.Run(dashboard); return failure is null ? 0 : 1;
    }
    private static string? failure;
    private static void Check(bool condition, string label) { lastCheck = label; if (!condition) throw new InvalidOperationException(label); checks++; }
    private static DataGrid Table => (DataGrid)dashboard.FindName("TaskTable");
    private static string Text(string name) => ((TextBlock)dashboard.FindName(name)).Text;
    private static void Click(string name) => ((Button)dashboard.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitAsync(Func<bool> ready, int seconds = 20)
    { var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds); while (!ready()) { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(lastCheck); await Task.Delay(25); } }
    private static async Task VerifyAsync()
    {
        try
        {
            foreach (var platform in new[] { MetadataPlatform.Ucampus, MetadataPlatform.Zhihuishu })
            {
                var service = services[platform.Id]; var fixture = fixtures[platform.Id]; var browser = browsers[platform.Id];
                await service.EnsureSessionAsync(); fixture.SetCookies(browser); await Task.Delay(60);
                Check(browser.CoreWebView2.Source == "about:blank" && browser.Visibility == Visibility.Hidden, "hidden metadata browser stays blank");
                Check(services.Values.Select(value => MetadataSyncService.ProfilePath(runRoot, value.Platform.Id)).Distinct().Count() == 2, "dedicated profiles for two enabled platforms");
                fixture.RenewNext = true; await service.StartAsync(true);
                var recording = store.LoadCourseRecording(platform.Id);
                Check(!recording.SelectionConfirmed && recording.Courses.Count > 0 && store.LoadMetadataSettings(platform.Id).LastSummary is null,
                    "platform returns full course catalog and waits for selection");
                Check(store.LoadPlatformSessionInfo(platform.Id).State == "ready", "cookie saved before course selection");
                store.SaveCourseRecording(platform.Id, false, recording.Courses.Where(course => course.Available).Select(course => course.Key).ToArray(), true, recording.Revision, DateTimeOffset.UtcNow);
                await service.RecordingCoursesChangedAsync();
                var summary = store.LoadMetadataSettings(platform.Id).LastSummary!;
                Check(summary is not null && !summary.LoginRequired && summary.PlatformId == platform.Id, "metadata result belongs to enabled platform");
                Check(summary!.Tasks == (platform.Id == "zhihuishu" ? Data.SchoolExamTaskCount : U.TaskCount), "both metadata platforms import confirmed unified tasks");
                Check(platform.Id == "zhihuishu" ? summary.CourseListCovered && summary.ReminderListCovered && summary.FullTaskListsCovered && summary.SharedCourses == 2
                    : summary is { Courses: 1, CoursesRead: 1, FullTaskListsCovered: true, TasksWithLink: U.TaskCount }, "confirmed platform list coverage reported");
                if (platform.Id == "zhihuishu")
                {
                    Check(summary is { RoomExamTasks: 2, OfflineExamTasks: 1, MakeupExamTasks: 2, UnidentifiedExamRows: 0, StatusNeedsReview: 1 }, "school exam counters and offline review state visible in desktop service");
                    Check(fixture.Paths.Count(path => path.EndsWith("getStudentRoomExam", StringComparison.Ordinal)) == 2
                        && fixture.Paths.Count(path => path.EndsWith("getStudentOfflineExam", StringComparison.Ordinal)) == 2
                        && fixture.Paths.Count(path => path.EndsWith("hasMakeupExam", StringComparison.Ordinal)) == 1, "desktop sync follows room offline and makeup branches");
                }
                var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync("");
                Check(cookies.Single(cookie => cookie.Name == fixture.AuthName).Value == "synthetic-session-renewed", "HTTP renewal written to live profile");
                var archive = new MetadataSessionStore(platform, Path.Combine(runRoot, ".local", "sessions", platform.Id + ".bin"), store);
                Check(archive.Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Value == "synthetic-session-renewed"), "renewed session saved with DPAPI");
            }
            foreach (var manager in app.Windows.OfType<CourseSelectionWindow>().ToArray()) manager.Close();
            await WaitAsync(() => Table.Items.Count == Data.SchoolExamTaskCount + U.TaskCount + 1);
            Check(Text("AggregationSummaryText").Contains("智慧树 14 条", StringComparison.Ordinal) && Text("AggregationSummaryText").Contains("U 校园 " + U.TaskCount + " 条", StringComparison.Ordinal)
                && Text("AggregationSummaryText").Contains("Fi 口语 1 条", StringComparison.Ordinal), "enabled sources and retained FiF manual task included in unified counts");
            Check(store.LoadTasks().Count(value => value.State == TaskState.Completed) == 8, "submitted shared flip and legacy homework and exam tasks visible in unified data");
            Check(store.LoadTasks().Single(value => value.PlatformTitle == "合成线下考试") is { State: TaskState.NeedsReview, DeadlineKind: DeadlineKind.Unknown, Link: null }, "desktop offline task preserves school-arrangement uncertainty");
            await VerifyDisabledFifAsync();
            var task = store.LoadTasks().Single(value => value.PlatformTitle == "合成作业");
            var navigation = new TaskNavigationService(store, null, _ => checks++); await navigation.OpenAsync(task.Id, CancellationToken.None);
            Check(fixtures["zhihuishu"].Paths.All(path => !path.Contains("/atHomeworkExam/", StringComparison.Ordinal)), "external specific task opens without answer reads");
            await VerifyUcampusAsync();
            await VerifySelectionAsync(); await VerifySettingsAsync(); await VerifyFailuresAsync(); await VerifyLoginFailurePreservationAsync();
            await VerifyUcampusSsoExpiryAsync(); await VerifyCourseSessionPreservationAsync(); await VerifyOfficialSessionRenewalAsync(); await VerifyRacesAsync(); await VerifyConnectionAsync(); await VerifySmartDesktopAsync();
            await VerifyLearningUnitsDesktopAsync();
            await WaitAsync(() => services.Values.All(service => !service.IsRunning));
            foreach (var service in services.Values) Check(await service.SaveForExitAsync(), "exit saves latest platform session");
            await VerifyLayoutAsync();
            var restarts = await SessionRestartChecks.VerifyAsync(runRoot); Check(restarts > 0, "fresh-process session recovery verified");
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message + " (after " + lastCheck + ")"; Capture("metadata-failure.png"); }
        finally
        {
            foreach (var fixture in fixtures.Values) fixture.Release?.TrySetResult(true);
            File.WriteAllText(Path.Combine(runRoot, "metadata-desktop-checks.json"), JsonSerializer.Serialize(new
            { outcome = failure is null ? "passed" : "failed", checks, platforms = services.Count, requests = fixtures.Values.Sum(value => value.Requests),
                browserRequests = fixtures.Values.Sum(value => value.BrowserRequests), data = "synthetic_only", network = "local_handler_and_webview_fixture_only",
                loginDocumentRoutes = fixtures["zhihuishu"].BrowserDocumentRoutes,
                externalBrowser = "captured_without_launch", layout = new[] { "normal", "minimum", "expanded-minimum" }, failure }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var service in services.Values) service.Dispose(); dashboard.CloseForExit(); app.Shutdown(failure is null ? 0 : 1);
        }
    }
    private static async Task VerifySelectionAsync()
    {
        var service = services["zhihuishu"]; var fixture = fixtures["zhihuishu"]; var recording = store.LoadCourseRecording("zhihuishu");
        Check(recording.Courses.Count == 3 && recording.CatalogComplete, "flip and both shared terms available for course selection");
        var count = store.LoadTasks().Count; store.SaveCourseRecording("zhihuishu", false, [], true, recording.Revision, Data.Now);
        await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table.Items.Count == U.TaskCount + 1);
        Check(store.LoadTasks().Count == count && store.LoadMetadataSettings("zhihuishu").LastSummary!.Tasks == 0, "deselected tasks retained and not imported");
        recording = store.LoadCourseRecording("zhihuishu"); store.SaveCourseRecording("zhihuishu", true, [], true, recording.Revision, Data.Now);
        await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table.Items.Count == Data.SchoolExamTaskCount + U.TaskCount + 1);
        recording = store.LoadCourseRecording("zhihuishu");
        var selectedKey = CourseRecordingRules.MetadataKey("zhihuishu", Data.Account, Data.SharedCourseModel().Id, "shared");
        store.SaveCourseRecording("zhihuishu", false, [selectedKey], true, recording.Revision, Data.Now);
        fixture.Paths.Clear(); await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table.Items.Count == U.TaskCount + 7);
        Check(store.LoadMetadataSettings("zhihuishu").LastSummary is { SelectedCourses: 1, Tasks: 6, RoomExamTasks: 2, MakeupExamTasks: 2, OfflineExamTasks: 0 }
            && fixture.Paths.All(path => path is not ("/zhsathome/homeworkUser/List" or "/zhsathome/examStu/List")), "shared term selection excludes flip list requests");
        recording = store.LoadCourseRecording("zhihuishu"); store.SaveCourseRecording("zhihuishu", true, [], true, recording.Revision, Data.Now);
        await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table.Items.Count == Data.SchoolExamTaskCount + U.TaskCount + 1);
        var summary = JsonSerializer.Serialize(store.LoadMetadataSettings("zhihuishu").LastSummary); fixture.Paths.Clear(); await service.RefreshCourseCatalogAsync();
        Check(fixture.Paths.All(path => !path.EndsWith("findImportantReminderList", StringComparison.Ordinal))
            && summary == JsonSerializer.Serialize(store.LoadMetadataSettings("zhihuishu").LastSummary), "catalog-only refresh preserves task summary");
        fixture.UnknownCatalog = true; await service.RefreshCourseCatalogAsync(); fixture.UnknownCatalog = false;
        Check(store.LoadCourseRecording("zhihuishu").Courses.Count == 3 && store.LoadCourseRecording("zhihuishu").LastCatalogRefresh!.Outcome == "partial", "unknown catalog keeps existing courses");
        Check(store.LoadPlatformSessionInfo("zhihuishu").State == "ready" && service.LoginDiagnostics.Snapshot().AccountConfirmed
            && Text("ZhihuishuPlatformSummary").Contains("已登录", StringComparison.Ordinal), "catalog parse failure preserves verified login and visible card status");
        ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = "智慧树"; Click("ChooseRecordingCoursesButton"); await Task.Delay(60);
        var window = app.Windows.OfType<CourseSelectionWindow>().Single(value =>
            (value.FindName("RecordingPlatformSelector") as ComboBox)?.SelectedItem is PlatformSite { Id: "zhihuishu" });
        Check((window.FindName("RecordingPlatformSelector") as ComboBox)?.SelectedItem is PlatformSite { Id: "zhihuishu" }, "unified course picker selects new platform");
        window.Close(); Click("ShowAllTasksButton");
    }
    private static async Task VerifySettingsAsync()
    {
        Click("OpenSettingsButton"); await Task.Delay(60); var window = app.Windows.OfType<Window>().Single(value => value.Title == "设置与平台状态");
        Check(!Visuals<CheckBox>(window).Any(box => (string?)box.Content == "自动检查Fi 口语"), "FiF settings contain no automatic sync toggle");
        Check(!Visuals<CheckBox>(window).Any(box => (string?)box.Content == "自动检查智慧树"), "platform controls are consolidated in platform managers");
        window.Close();
        foreach (var platform in new[] { MetadataPlatform.Ucampus, MetadataPlatform.Zhihuishu })
        {
            Click(platform.Id == "ucampus" ? "UcampusConnectionButton" : "ZhihuishuConnectionButton"); await Task.Delay(100);
            window = app.Windows.OfType<CourseSelectionWindow>().Single(value =>
                (value.FindName("RecordingPlatformSelector") as ComboBox)?.SelectedItem is PlatformSite selected && selected.Id == platform.Id);
            var checkbox = (CheckBox)window.FindName("PlatformAutomaticSync");
            checkbox.IsChecked = false; checkbox.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!store.LoadMetadataSettings(platform.Id).AutomaticSyncEnabled, "per-platform switch persists");
            var requests = fixtures[platform.Id].Requests; services[platform.Id].LoginNavigationCompleted(platform.Home.AbsoluteUri); await Task.Delay(120);
            Check(fixtures[platform.Id].Requests == requests, "disabled automatic check does not run after login navigation");
            await services[platform.Id].StartAsync(true); Check(fixtures[platform.Id].Requests > requests, "manual sync works with auto disabled");
            window.Close();
        }
        Click("SyncDetailsButton"); await Task.Delay(60); window = app.Windows.OfType<Window>().Single(value => value.Title == "各平台同步详情");
        Check(MetadataPlatform.Enabled.All(platform => Visuals<TextBlock>(window).Any(text => text.Text == platform.Name + "同步详情")), "details covers enabled added platforms");
        Check(Visuals<TextBlock>(window).Any(text => text.Text.Contains("单元学习", StringComparison.Ordinal)
            && text.Text.Contains("必学", StringComparison.Ordinal)), "learning-unit coverage and required-progress totals visible"); window.Close();
        var service = services["zhihuishu"]; var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(MetadataSyncSummary _) => completion.TrySetResult(true);
        service.Completed += Done; Click("SyncButton"); await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); await WaitAsync(() => !service.IsRunning); service.Completed -= Done;
        Check(store.LoadTasks().Count == Data.SchoolExamTaskCount + U.TaskCount + 1, "sync-all dispatches enabled platform services without duplicates");
    }
    private static async Task VerifyFailuresAsync()
    {
        var service = services["zhihuishu"]; var fixture = fixtures["zhihuishu"]; var count = store.LoadTasks().Count;
        fixture.UnknownReminders = true; await service.StartAsync(true); fixture.UnknownReminders = false;
        Check(!store.LoadMetadataSettings("zhihuishu").LastSummary!.ReminderListCovered && store.LoadTasks().Count == count, "unrecognized reminders preserve tasks and partial status");
        fixture.RejectLogin = true; await service.StartAsync(true); fixture.RejectLogin = false;
        Check(store.LoadMetadataSettings("zhihuishu").LastSyncOutcome == "login_required" && store.LoadTasks().Count == count, "official rejection shows login required");
        Check(!File.Exists(Path.Combine(runRoot, ".local", "sessions", "zhihuishu.bin")), "rejected archive removed");
        fixture.SetCookies(browsers["zhihuishu"]); await Task.Delay(60); await service.StartAsync(true);
        Check(!store.LoadMetadataSettings("zhihuishu").LastSummary!.LoginRequired, "fresh official login can recover");
    }
    private static async Task VerifyRacesAsync()
    {
        var service = services["zhihuishu"]; var fixture = fixtures["zhihuishu"]; var browser = browsers["zhihuishu"]; var count = store.LoadTasks().Count;
        fixture.PauseReminders(); var pending = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var changed = browser.CoreWebView2.CookieManager.CreateCookie("CASLOGC", Data.Cookie("synthetic-new-login").Value, ".zhihuishu.com", "/");
        changed.IsSecure = true; browser.CoreWebView2.CookieManager.AddOrUpdateCookie(changed); await Task.Delay(60); fixture.Release!.TrySetResult(true); await pending;
        Check(store.LoadTasks().Count == count && Text("ZhihuishuSyncStatusText").Contains("登录会话", StringComparison.Ordinal), "login switch cancels old task import");
        fixture.ResetPause(); fixture.SetCookies(browser); await Task.Delay(60); fixture.PauseReminders(); fixture.LateRenewal = true;
        pending = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var clear = service.ClearSessionAsync(); fixture.Release!.TrySetResult(true); await pending; await clear; fixture.ResetPause();
        Check((await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Count == 0 && !File.Exists(Path.Combine(runRoot, ".local", "sessions", "zhihuishu.bin")), "late response cannot resurrect cleared login");
        Check(store.LoadTasks().Count == count, "reconnect preserves unified task data"); fixture.SetCookies(browser); await Task.Delay(60);
    }
    private static async Task VerifyConnectionAsync()
    {
        lastCheck = "current official login return and student iframe";
        var service = services["zhihuishu"]; var connection = new MetadataConnectionWindow(service, () => { });
        var fixture = fixtures["zhihuishu"]; fixture.DelayStudentToken = fixture.IncludeUnrelatedTokenFrame = true;
        fixture.InjectLoginScript = fixture.FailLoginScript = true;
        var field = typeof(MetadataConnectionWindow).GetField("browser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var browser = (WebView2)field.GetValue(connection)!; fixtures["zhihuishu"].BlockBrowser(browser);
        connection.Show(); await WaitAsync(() => browser.CoreWebView2 is not null && fixtures["zhihuishu"].BrowserRequests > 0);
        await VerifyZhihuishuCacheRetryAsync(connection, browser, service, fixture);
        fixture.SimulateZhihuishuLoginReturn = true;
        var routesBeforeLogin = fixture.BrowserDocumentRoutes.Count;
        Visuals<Button>(connection).Single(button => (string?)button.Content == "打开官方登录").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var archive = new MetadataSessionStore(MetadataPlatform.Zhihuishu, Path.Combine(runRoot, ".local", "sessions", "zhihuishu.bin"), store);
        await WaitAsync(() => fixture.StudentDocumentLoadedWithoutToken);
        Check(fixture.BrowserDocumentRoutes.Skip(routesBeforeLogin).Take(4).SequenceEqual(new[]
        {
            "login.zhihuishu.com/", "passport.zhihuishu.com/login",
            "cloudapi.polymas.com/oauth/login/ssoLogin", "onlineweb.zhihuishu.com/onlinestuh5"
        }), "official login follows the current login center, passport and SSO before loading student data");
        Check(browser.CoreWebView2.Source == Fixtures.ZhihuishuStudentHome
            && browser.CoreWebView2.Environment.UserDataFolder == browsers["zhihuishu"].CoreWebView2.Environment.UserDataFolder,
            "SSO return reaches student home in the platform's shared login environment");
        Check(fixture.StudentDocumentLoadedWithoutToken, "student iframe finishes DOM before login token exists");
        await WaitAsync(() => { archive.Load(DateTimeOffset.UtcNow); return archive.Token == "synthetic-localstorage-token"; });
        Check(archive.Token == "synthetic-localstorage-token", "official student frame token observed and encrypted");
        var binding = archive.TokenBinding;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await service.ObserveMetadataTokenAsync("https://onlinestuh5.zhihuishu.com/", () => delayed.Task, timeout.Token);
                throw new InvalidOperationException("Stalled token read did not cancel.");
            }
            catch (OperationCanceledException) { Check(true, "stalled token script releases cookie gate when cancelled"); }
            delayed.SetResult("{\"eligible\":true,\"token\":\"synthetic-obsolete-token\"}");
            await Task.Delay(60); archive.Load(DateTimeOffset.UtcNow);
            Check(archive.Token == "synthetic-localstorage-token", "late cancelled token result cannot replace current login");
        }
        await fixture.StudentFrame!.ExecuteScriptAsync("localStorage.setItem('token','synthetic-refreshed-token');");
        await WaitAsync(() => { archive.Load(DateTimeOffset.UtcNow); return archive.Token == "synthetic-refreshed-token"; });
        Check(archive.Token == "synthetic-refreshed-token" && archive.TokenBinding == binding, "SPA token renewal captured without navigation and unrelated frame ignored");
        await service.RecordingCoursesChangedAsync(); Check(store.LoadMetadataSettings("zhihuishu").LastSummary!.Tasks == Data.SchoolExamTaskCount, "frame token and cookies support selected shared and flip metadata sync");
        var count = store.LoadTasks().Count;
        await fixture.StudentFrame.ExecuteScriptAsync("localStorage.removeItem('token');");
        await WaitAsync(() => { archive.Load(DateTimeOffset.UtcNow); return archive.Token is null; });
        Check(archive.Token is null && store.LoadTasks().Count == count, "removed student token clears encrypted token and preserves tasks");
        await browser.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('token','synthetic-parent-token');");
        await WaitAsync(() => { archive.Load(DateTimeOffset.UtcNow); return archive.Token == "synthetic-parent-token"; });
        Check(archive.Token == "synthetic-parent-token", "current qiankun parent origin token observed from exact student home");
        await service.ObserveMetadataTokenAsync("https://onlinestuh5.zhihuishu.com/", () => Task.FromResult("{\"eligible\":true,\"source\":\"https://onlinestuh5.zhihuishu.com\",\"token\":null}"));
        await service.ObserveMetadataTokenAsync("https://onlinestuh5.zhihuishu.com/", () => Task.FromResult("{\"eligible\":true,\"source\":\"https://onlinestuh5.zhihuishu.com\",\"token\":\"synthetic-legacy-token\"}"));
        archive.Load(DateTimeOffset.UtcNow);
        Check(archive.Token == "synthetic-parent-token", "empty or obsolete iframe credential cannot replace current parent token");
        var unrelatedReads = 0;
        await service.ObserveMetadataTokenAsync("https://onlineweb.zhihuishu.com/synthetic-unrelated", () => { unrelatedReads++; return Task.FromResult("{\"eligible\":true,\"token\":\"synthetic-unrelated-token\"}"); });
        Check(unrelatedReads == 0, "same-origin unrelated document path is rejected before reading its token script");
        await browser.CoreWebView2.ExecuteScriptAsync("localStorage.removeItem('token');");
        await WaitAsync(() => { archive.Load(DateTimeOffset.UtcNow); return archive.Token is null; });
        Check(archive.Token is null && store.LoadTasks().Count == count, "current parent token removal clears archive without changing tasks");
        fixture.DelayStudentToken = false;
        await VerifyZhihuishuReturnNavigationAsync(browser, fixture);
        connection.Close();
        foreach (var platform in new[] { MetadataPlatform.Ucampus })
        {
            connection = new MetadataConnectionWindow(services[platform.Id], () => { }); browser = (WebView2)field.GetValue(connection)!; fixtures[platform.Id].BlockBrowser(browser);
            connection.Show(); await WaitAsync(() => browser.CoreWebView2 is not null && fixtures[platform.Id].BrowserRequests > 0);
            Check(connection.Title.Contains(platform.Name, StringComparison.Ordinal), "dedicated official connection window");
            await PlatformConnectionTestData.ConnectionPopupChecks.VerifyAsync(browser.CoreWebView2, platform.Host, Check);
            await VerifyUcampusStudentPopupAsync(connection, browser, services[platform.Id], fixtures[platform.Id]);
            connection.Close();
            await VerifyUcampusClosedWindowPopupAsync(services[platform.Id], fixtures[platform.Id]);
        }
    }
    private static async Task VerifyLayoutAsync()
    {
        dashboard.Width = 1360; dashboard.Height = 930; ((Expander)dashboard.FindName("PlatformSyncExpander")).IsExpanded = false;
        await Task.Delay(100); CheckLayout("normal"); Capture("metadata-dashboard-normal.png");
        dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight; await Task.Delay(100); CheckLayout("minimum"); Capture("metadata-dashboard-minimum.png");
        ((Expander)dashboard.FindName("PlatformSyncExpander")).IsExpanded = true; await Task.Delay(100); CheckLayout("expanded minimum"); Capture("metadata-dashboard-expanded-minimum.png");
        ((Expander)dashboard.FindName("SelectedDetailsExpander")).IsExpanded = true; await Task.Delay(100); dashboard.UpdateLayout();
        Check(Table.ActualHeight >= 100, $"both detail panels leave task rows at minimum size ({Table.ActualHeight:F2})");
        Capture("metadata-dashboard-both-expanded-minimum.png");
    }
    private static void CheckLayout(string size)
    {
        dashboard.UpdateLayout(); var panel = (Panel)dashboard.FindName("PlatformActionPanel");
        var bounds = new List<Rect>();
        foreach (var button in panel.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible))
        {
            var rect = button.TransformToAncestor(dashboard).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight));
            Check(button.IsVisible && rect.Width > 30 && rect.Top >= 0 && rect.Right <= dashboard.ActualWidth && rect.Bottom <= dashboard.ActualHeight,
                size + " platform button visible inside sidebar");
            Check(bounds.All(previous => !previous.IntersectsWith(rect)), size + " platform controls do not overlap"); bounds.Add(rect);
        }
        Check(Table.ActualHeight >= 150, size + $" task list usable height ({Table.ActualHeight:F2})");
        Check(((TextBlock)dashboard.FindName("AggregationSummaryText")).ActualHeight > 0, size + " platform counts visible");
    }
    private static async Task VerifyDisabledFifAsync()
    {
        var button = (Button)dashboard.FindName("FifConnectionButton"); var banner = (Border)dashboard.FindName("FifSyncBanner");
        Check(button.Visibility == Visibility.Collapsed && !button.IsEnabled && banner.Visibility == Visibility.Collapsed, "FiF connection and sync banner hidden");
        var browser = (WebView2)dashboard.FindName("FifSessionBrowser"); var fixture = new Fixtures(MetadataPlatform.Fif);
        using var service = new MetadataSyncService(MetadataPlatform.Fif, store, runRoot, browser, fixture.CreateHandler, TimeSpan.FromMilliseconds(50));
        store.SaveMetadataSettings("fif", new() { Connected = true, AutomaticSyncEnabled = true });
        service.StartAutomatic(); service.ConnectionOpened(); service.LoginNavigationCompleted(MetadataPlatform.Fif.Home.AbsoluteUri);
        await service.StartAsync(true); await Task.Delay(120);
        Check(fixture.Requests == 0 && browser.CoreWebView2 is null && !service.IsRunning, "FiF saved settings cannot initialize browser or send sync requests");
        Check(PlatformCatalog.Sites.Single(site => site.Id == "fif").Status.Contains("暂不适配", StringComparison.Ordinal), "FiF manual source retained without adaptation claim");
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(string file)
    {
        dashboard.UpdateLayout();
        var content = (FrameworkElement)dashboard.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(content.RenderSize);
            context.DrawRectangle(dashboard.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(content), null, bounds);
        }
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(runRoot, file)); png.Save(stream);
    }
}
