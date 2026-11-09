using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AccessProbe;
using Microsoft.Web.WebView2.Wpf;
using XuetangTestData;

namespace XuetangSyncHarness;

internal static partial class Program
{
    private static Application app = null!;
    private static TaskDashboardWindow dashboard = null!;
    private static TaskStore store = null!;
    private static WebView2 browser = null!;
    private static XuetangSyncService service = null!;
    private static readonly Fixtures fixture = new();
    private static readonly List<string> opened = [], connections = [];
    private static string runRoot = "", lastCheck = "";
    private static int checks;
    private static bool passed;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--session-child") return SessionRestartChecks.RunChild(args[1], args[2], args[3]);
        if (args.Length != 3 || args[0] != "--project-root" || args[2] != "--verify") return 2;
        var root = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var harnessRoot = Path.Combine(root, ".local", "xuetang-sync-harness"); Directory.CreateDirectory(harnessRoot);
        runRoot = Path.Combine(harnessRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(runRoot);
        File.WriteAllText(Path.Combine(harnessRoot, "latest-run.txt"), runRoot);
        Environment.SetEnvironmentVariable("TEMP", runRoot); Environment.SetEnvironmentVariable("TMP", runRoot);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        store = FixtureTaskStore.Create(Path.Combine(runRoot, "tasks.db"), pendingPlatforms: ["xuetang"]); store.SaveSettings(new(false, true) { LastSyncOutcome = "chaoxing-preserved" });
        store.Create(new("已有学习通任务", "chaoxing", "已有课程", DeadlineKind.Unknown, null, null, null, null, "", "", null), Metadata.Now);
        dashboard = new(store, runRoot, () => connections.Add("chaoxing"), () => app.Shutdown(), opened.Add)
        { Title = "学习任务聚合 · 学堂在线合成桌面验收" };
        dashboard.PlatformConnectionRequested += connections.Add;
        browser = (WebView2)dashboard.FindName("XuetangSessionBrowser"); fixture.BlockBrowser(browser);
        service = new(store, runRoot, browser, fixture.CreateHandler, TimeSpan.FromMilliseconds(250));
        dashboard.AttachXuetangSync(service); dashboard.AutomaticSyncSettingChanged += service.SettingsChanged;
        dashboard.Loaded += (_, _) => dashboard.Dispatcher.BeginInvoke(new Action(async () => await VerifyAsync()));
        app.MainWindow = dashboard; app.Run(dashboard); service.Dispose(); return passed ? 0 : 1;
    }

    private static async Task VerifyAsync()
    {
        string? failure = null;
        try
        {
            service.StartAutomatic(); await Task.Delay(75);
            Check(fixture.Requests == 0 && !service.IsRunning, "unconnected platform sends no requests");
            Click("XuetangConnectionButton");
            await WaitAsync(() => app.Windows.OfType<CourseSelectionWindow>().Any());
            var manager = app.Windows.OfType<CourseSelectionWindow>().Single();
            await WaitAsync(() => manager.FindName("RecordingCourseList") is ListBox { ItemsSource: not null });
            ((Button)manager.FindName("RecordingConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(connections.SequenceEqual(new[] { "xuetang" }), "platform manager connects only Xuetang");
            var loggedOut = await RunAsync(service.ConnectionOpened);
            Check(loggedOut.LoginRequired && fixture.Requests == 1 && store.LoadTasks().Count == 1, "anonymous official login response preserves existing tasks");
            Check(store.LoadCourseRecording("xuetang").LastCatalogRefresh?.Outcome == "login_required" && Text("XuetangSyncStatusText").Contains("XT001", StringComparison.Ordinal), "login failure stored and visible");
            Check(((Expander)dashboard.FindName("PlatformSyncExpander")).IsExpanded && Text("SyncOverviewText").Contains("学堂在线", StringComparison.Ordinal), "new-platform warning enters shared overview");
            Fixtures.SetCookie(browser, fixture.ExpectedToken); fixture.RenewNext = true;
            var catalog = await RunAsync(() => service.LoginNavigationCompleted(XuetangPlatform.Home.AbsoluteUri), value => !value.LoginRequired);
            Check(catalog.Courses == 1 && catalog.Tasks == 0 && catalog.CoursesRead == 0 && store.LoadTasks().Count == 1, "login returns all courses without task requests");
            var selection = store.LoadCourseRecording("xuetang");
            store.SaveCourseRecording("xuetang", false, selection.Courses.Select(course => course.Key).ToArray(), true, selection.Revision, DateTimeOffset.UtcNow);
            var imported = await RunAsync(() => _ = service.RecordingCoursesChangedAsync());
            service.ConnectionClosed();
            manager.Close();
            Check(imported.Courses == 1 && imported.CoursesRead == 1 && imported.Tasks == 5 && imported.Added == 5 && imported.Problems.Count == 0, "official login triggers scoped automatic sync");
            Check(imported.TasksWithDeadline == 5 && imported.StatusNeedsReview == 1 && store.LoadTasks().Count == 6, "task evidence counters and combined history");
            Check(store.LoadSettings().LastSyncOutcome == "chaoxing-preserved" && store.LoadMoocSettings().LastSummary is null, "platform sync settings do not affect other platforms");
            var archivePath = Path.Combine(runRoot, ".local", "sessions", "xuetang.bin");
            var archive = new XuetangSessionStore(archivePath, store);
            Check(archive.Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Name == "sessionid" && cookie.Value == "synthetic-renewed" && cookie.ExpiresUtc is null), "renewed temporary session encrypted");
            Check((await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Single(cookie => cookie.Name == "sessionid").Value == "synthetic-renewed", "renewed auth written back to browser");
            await WaitAsync(() => Table().Items.Count == 6);
            Check(Text("AggregationSummaryText").Contains("学堂在线 5 条", StringComparison.Ordinal), "unified source summary includes Xuetang");
            Check(Text("CompleteCountText") == "1" && Text("ActiveCountText") == "5", "unified completion and review counts");
            var exam = store.LoadTasks().Single(task => task.PlatformTitle == "合成任务 202"); Select(exam.Id); Click("GoButton");
            await WaitAsync(() => opened.Count == 1);
            Check(opened[0] == exam.Link && opened[0].EndsWith("/quiz/202", StringComparison.Ordinal), "task button captures correct official exam route without launching browser");
            var beforeRequests = fixture.Requests; await service.StartAsync(true); await Task.Delay(75);
            Check(store.LoadTasks().Count == 6 && store.LoadXuetangSettings().LastSummary!.Added == 0 && fixture.Requests > beforeRequests, "manual resync no duplicates");
            var sourceTask = store.LoadTasks().Single(task => task.PlatformTitle == "合成任务 201");
            var edited = store.Edit(sourceTask.Id, sourceTask.Revision, new("用户标题", "xuetang", "用户课程", DeadlineKind.Known, null,
                Metadata.Now.AddDays(1), sourceTask.Link, 100, "人工成绩", "人工备注", 2), Metadata.Now);
            store.SetState(edited.Id, edited.Revision, TaskState.Completed, Metadata.Now);
            await service.StartAsync(true); var latest = store.Get(sourceTask.Id)!;
            Check(latest.Title == "用户标题" && latest.CourseName == "用户课程" && latest.State == TaskState.Completed && latest.ProgressPercent == 100
                && latest.ScoreText == "人工成绩" && latest.DeadlineUtc == Metadata.Now.AddDays(1), "desktop sync retains manual edits");
            Check(latest.PlatformStatusText == "未开始" && latest.PlatformStateEvidence == "leaf_schedules=0", "platform evidence visible separately");

            await VerifySelectionAsync();
            await VerifySettingsAsync();
            await VerifyFailuresAsync();
            await VerifyRacesAsync();
            await VerifyOfficialConnectionAsync();
            Check(await service.SaveForExitAsync(), "graceful shutdown writes current encrypted session");
            var reopened = FixtureTaskStore.Create(store.DatabasePath);
            Check(reopened.LoadTasks().Count == 6 && reopened.LoadXuetangSettings().LastSummary is not null, "tasks and platform summary restored from unified database");
            Capture("xuetang-dashboard.png");
            var restartChecks = await SessionRestartChecks.VerifyAsync(runRoot);
            Check(restartChecks > 0, "fresh-process encrypted recovery verification completed");
            passed = true;
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message + " (after " + lastCheck + ")"; Capture("xuetang-failure.png"); }
        finally
        {
            fixture.Release?.TrySetResult(true);
            File.WriteAllText(Path.Combine(runRoot, "xuetang-desktop-checks.json"), JsonSerializer.Serialize(new
            { outcome = passed ? "passed" : "failed", checks, requests = fixture.Requests, browserRequests = fixture.BrowserRequests,
                data = "synthetic_only", network = "local_handler_and_webview_fixture_only", externalBrowser = "captured_without_launch",
                coverageScope = XuetangPlatform.Scope, failure }, new JsonSerializerOptions { WriteIndented = true }));
            service.Dispose(); dashboard.CloseForExit(); app.Shutdown(passed ? 0 : 1);
        }
    }

    private static async Task VerifySelectionAsync()
    {
        var recording = store.LoadCourseRecording("xuetang");
        Check(recording.Courses.Count == 1 && recording.CatalogComplete, "task-free discovery catalog persisted");
        store.SaveCourseRecording("xuetang", false, [], true, recording.Revision, Metadata.Now);
        fixture.Paths.Clear(); await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table().Items.Count == 1);
        Check(store.LoadTasks().Count == 6 && store.LoadRecordedTasks().Count == 1, "deselection hides and retains Xuetang history");
        Check(fixture.Paths.All(path => path is not (XuetangPlatform.Chapter or XuetangPlatform.Schedule or XuetangPlatform.ResetDeadlines)), "deselected course not requested");
        Check(Text("AggregationSummaryText").Contains("学堂在线 0 条", StringComparison.Ordinal), "zero selected tasks shown by platform");
        var current = store.LoadCourseRecording("xuetang"); store.SaveCourseRecording("xuetang", true, [], true, current.Revision, Metadata.Now);
        await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table().Items.Count == 6);
        Check(store.LoadRecordedTasks().Count == 6, "reselection restores combined view");
        var syncResult = JsonSerializer.Serialize(store.LoadXuetangSettings().LastSummary);
        fixture.Paths.Clear(); await service.RefreshCourseCatalogAsync();
        Check(fixture.Paths.All(path => path is not (XuetangPlatform.Chapter or XuetangPlatform.Schedule or XuetangPlatform.ResetDeadlines))
            && JsonSerializer.Serialize(store.LoadXuetangSettings().LastSummary) == syncResult, "course refresh keeps previous task summary");
        Check(store.LoadCourseRecording("xuetang").LastCatalogRefresh!.Outcome == "completed", "independent catalog success persisted");
        fixture.UnknownCatalog = true; await service.RefreshCourseCatalogAsync(); fixture.UnknownCatalog = false;
        Check(store.LoadCourseRecording("xuetang").Courses.Count == 1 && store.LoadCourseRecording("xuetang").LastCatalogRefresh!.Outcome == "partial"
            && JsonSerializer.Serialize(store.LoadXuetangSettings().LastSummary) == syncResult, "catalog failure keeps course list selection and historical sync result");
        ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = XuetangPlatform.Name;
        Click("ChooseRecordingCoursesButton"); await Task.Delay(75);
        var window = app.Windows.OfType<CourseSelectionWindow>().Single();
        var selector = (ComboBox)window.FindName("RecordingPlatformSelector");
        Check(window.IsVisible && selector.Items.OfType<PlatformSite>().Any(site => site.Id == "xuetang")
            && (selector.SelectedItem as PlatformSite)?.Id == "xuetang", "all-platform course selection includes Xuetang");
        window.Close();
        Click("ShowAllTasksButton");
    }

    private static async Task VerifySettingsAsync()
    {
        Click("XuetangConnectionButton"); await Task.Delay(100);
        var window = app.Windows.OfType<CourseSelectionWindow>().Single();
        var automatic = (CheckBox)window.FindName("PlatformAutomaticSync");
        automatic.IsChecked = false; automatic.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var count = fixture.Requests; service.StartAutomatic(); await Task.Delay(75);
        Check(!store.LoadXuetangSettings().AutomaticSyncEnabled && fixture.Requests == count, "per-platform toggle stops automatic reads");
        Check(store.LoadMoocSettings().AutomaticSyncEnabled && store.LoadSettings().AutomaticSyncEnabled, "platform toggle independent of global and other platform");
        await service.StartAsync(true); Check(fixture.Requests > count, "manual sync remains available with automatic disabled");
        automatic.IsChecked = true; automatic.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); await WaitAsync(() => !service.IsRunning);
        Check(store.LoadXuetangSettings().AutomaticSyncEnabled, "platform automatic setting reenabled");
        window.Close();
        store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = false }); service.SettingsChanged(false); count = fixture.Requests;
        service.StartAutomatic(); await Task.Delay(50); Check(fixture.Requests == count, "global disable prevents automatic platform read");
        store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = true }); service.SettingsChanged(true); await WaitAsync(() => !service.IsRunning);
    }

    private static async Task VerifyFailuresAsync()
    {
        var before = store.LoadTasks().Count;
        fixture.UnknownChapter = true; await service.StartAsync(true); fixture.UnknownChapter = false;
        Check(store.LoadXuetangSettings().LastSyncOutcome == "partial" && store.LoadTasks().Count == before, "unknown chapter retains saved tasks and reports partial");
        fixture.ResetUnavailable = true; await service.StartAsync(true); fixture.ResetUnavailable = false;
        var result = store.LoadXuetangSettings().LastSummary!;
        Check(result.Tasks == 5 && result.TasksWithDeadline == 0 && result.CoursesRead == 0 && result.Problems.Any(problem => problem.Code == "XT006"), "extension failure shows deadline verification gap");
        fixture.UnknownProfile = true; await service.StartAsync(true); fixture.UnknownProfile = false;
        Check(store.LoadXuetangSettings().LastSyncOutcome == "partial" && store.LoadTasks().Count == before, "unrecognized identity imports nothing");
        await service.StartAsync(true);
        Click("SyncDetailsButton"); await Task.Delay(50);
        var details = app.Windows.OfType<Window>().Single(value => value != dashboard && value.Title.Contains("同步详情", StringComparison.Ordinal));
        Check(FindText(details).Any(text => text.Text == "学堂在线同步详情")
            && FindText(details).Any(text => text.Text.Contains("旧版课程、多课项目", StringComparison.Ordinal)), "details show scoped coverage and unsupported platforms");
        details.Close();
    }

    private static async Task VerifyRacesAsync()
    {
        var before = store.LoadTasks().Select(task => (task.Id, task.Revision)).ToArray();
        fixture.PauseChapter(); var run = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(!((Button)dashboard.FindName("SyncButton")).IsEnabled && ((Button)dashboard.FindName("StopSyncButton")).Visibility == Visibility.Visible,
            "running Xuetang sync disables all-platform duplicate trigger");
        Click("StopSyncButton"); fixture.Release!.TrySetResult(true); await run; fixture.ResetPause();
        Check(store.LoadTasks().Select(task => (task.Id, task.Revision)).SequenceEqual(before), "stop cancels late task writes");
        fixture.PauseChapter(); fixture.LateRenewal = true; run = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Fixtures.SetCookie(browser, "synthetic-new-login");
        var changedDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!(await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Any(cookie => cookie.Name == "sessionid" && cookie.Value == "synthetic-new-login"))
        { if (DateTimeOffset.UtcNow > changedDeadline) throw new TimeoutException("synthetic cookie change"); await Task.Delay(25); }
        fixture.Release!.TrySetResult(true); await run; fixture.ResetPause();
        Check(store.LoadTasks().Select(task => (task.Id, task.Revision)).SequenceEqual(before), "account cookie change cancels old task response");
        Check((await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Single(cookie => cookie.Name == "sessionid").Value == "synthetic-new-login", "old renewal cannot overwrite new login");
        fixture.ExpectedToken = "synthetic-new-login"; fixture.Account = "91001";
        fixture.PauseChapter(); run = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var clear = service.ClearSessionAsync(); fixture.Release!.TrySetResult(true); await Task.WhenAll(run, clear); fixture.ResetPause();
        Check(store.LoadTasks().Select(task => (task.Id, task.Revision)).SequenceEqual(before), "clear waits and retains unified history");
        Check((await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Count == 0
            && !File.Exists(Path.Combine(runRoot, ".local", "sessions", "xuetang.bin")), "clear removes only dedicated login and archive");
        Fixtures.SetCookie(browser, fixture.ExpectedToken); await service.StartAsync(true);
        Check(store.LoadTasks().Count == before.Length, "relogin resync keeps stable identities");

    }

    private static async Task VerifyOfficialConnectionAsync()
    {
        var window = new XuetangConnectionWindow(service, dashboard.ShowDashboard);
        var loginBrowser = ((DockPanel)window.Content).Children.OfType<WebView2>().Single(); fixture.BlockBrowser(loginBrowser);
        window.Show(); await WaitAsync(() => loginBrowser.CoreWebView2?.Source == XuetangPlatform.Login.AbsoluteUri);
        Check(loginBrowser.CoreWebView2.Environment.UserDataFolder == browser.CoreWebView2.Environment.UserDataFolder, "dedicated official login window shares Xuetang-only environment");
        Check(FindText(window).Any(text => text.Text.Contains("旧版课程、多课项目", StringComparison.Ordinal)), "login screen discloses collection scope");
        await VerifyLoginPageAsync(window, loginBrowser);
        loginBrowser.CoreWebView2.Navigate("https://evil.example/login"); await Task.Delay(100);
        Check(loginBrowser.CoreWebView2.Source == XuetangPlatform.Login.AbsoluteUri, "unsupported login redirect stays blocked");
        await PlatformConnectionTestData.ConnectionPopupChecks.VerifyAsync(loginBrowser.CoreWebView2, XuetangPlatform.Host, Check);
        window.Close(); await WaitAsync(() => !service.IsRunning);
        Check(fixture.BrowserRequests > 0, "official login tested against local document response");
    }

    private static DataGrid Table() => (DataGrid)dashboard.FindName("TaskTable");
    private static string Text(string name) => ((TextBlock)dashboard.FindName(name)).Text;
    private static void Click(string name) => ((Button)dashboard.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Select(string id) => Table().SelectedItem = Table().Items.OfType<TaskDashboardWindow.TaskDisplayRow>().Single(row => row.Task.Id == id);
    private static void Check(bool condition, string label) { lastCheck = label; if (!condition) throw new InvalidOperationException(label); checks++; }
    private static async Task WaitAsync(Func<bool> ready)
    { var deadline = DateTimeOffset.UtcNow.AddSeconds(15); while (!ready()) { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(lastCheck); await Task.Delay(25); } }
    private static async Task<XuetangSyncSummary> RunAsync(Action start, Func<XuetangSyncSummary, bool>? filter = null)
    {
        var result = new TaskCompletionSource<XuetangSyncSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(XuetangSyncSummary summary) { if (filter is null || filter(summary)) result.TrySetResult(summary); }
        service.Completed += Completed;
        void Catalog(XuetangSyncSummary summary)
        {
            if (summary.LoginRequired || !store.LoadCourseRecording("xuetang").SelectionConfirmed) { Completed(summary); return; }
            dashboard.Dispatcher.BeginInvoke(new Action(async () => await service.RecordingCoursesChangedAsync()));
        }
        service.CatalogCompleted += Catalog;
        try { start(); var summary = await result.Task.WaitAsync(TimeSpan.FromSeconds(30)); await WaitAsync(() => !service.IsRunning); await Task.Delay(75); return summary; }
        finally { service.Completed -= Completed; service.CatalogCompleted -= Catalog; }
    }
    private static IEnumerable<TextBlock> FindText(DependencyObject node) => FindVisuals<TextBlock>(node);
    private static IEnumerable<T> FindVisuals<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var value in FindVisuals<T>(VisualTreeHelper.GetChild(node, index))) yield return value;
    }
    private static void Capture(string name)
    {
        dashboard.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)dashboard.ActualWidth, (int)dashboard.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dashboard); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(runRoot, name)); encoder.Save(stream);
    }
}
