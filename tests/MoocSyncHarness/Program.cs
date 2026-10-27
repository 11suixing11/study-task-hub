using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AccessProbe;
using Microsoft.Web.WebView2.Wpf;

namespace MoocSyncHarness;

internal static partial class Program
{
    private static Application app = null!;
    private static TaskDashboardWindow dashboard = null!;
    private static TaskStore store = null!;
    private static MoocSyncService service = null!;
    private static WebView2 browser = null!;
    private static readonly Fixtures fixture = new();
    private static readonly List<string> opened = [];
    private static readonly List<string> connections = [];
    private static readonly Dictionary<string, double> tableHeights = [];
    private static string runRoot = "", lastCheck = "";
    private static int checks, restartChecks;
    private static bool passed;
    private static string? failure;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--session-child") return SessionRestartChecks.RunChild(args[1], args[2], args[3]);
        if (args.Length != 3 || args[0] != "--project-root" || args[2] != "--verify") return 2;
        var root = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var harnessRoot = Path.Combine(root, ".local", "mooc-sync-harness"); Directory.CreateDirectory(harnessRoot);
        runRoot = Path.Combine(harnessRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot); File.WriteAllText(Path.Combine(harnessRoot, "latest-run.txt"), runRoot);
        Environment.SetEnvironmentVariable("TEMP", runRoot); Environment.SetEnvironmentVariable("TMP", runRoot);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        store = new TaskStore(Path.Combine(runRoot, "tasks.db")); store.SaveSettings(new(false, true) { LastSyncOutcome = "chaoxing-preserved" });
        SeedOtherPlatforms();
        dashboard = new(store, runRoot, () => connections.Add("chaoxing"), () => app.Shutdown(), opened.Add)
        { Title = "学习任务聚合 · MOOC 合成桌面验收" };
        dashboard.PlatformConnectionRequested += connections.Add;
        browser = (WebView2)dashboard.FindName("MoocSessionBrowser"); fixture.BlockBrowser(browser);
        service = new(store, runRoot, browser, fixture.CreateHandler, TimeSpan.FromMilliseconds(250));
        dashboard.AttachMoocSync(service); dashboard.AutomaticSyncSettingChanged += service.SettingsChanged;
        dashboard.Loaded += (_, _) => dashboard.Dispatcher.BeginInvoke(new Action(async () => await VerifyAsync()));
        dashboard.Closed += (_, _) => { service.Dispose(); app.Shutdown(); };
        app.MainWindow = dashboard; app.Run(dashboard); service.Dispose(); return passed ? 0 : 1;
    }

    private static async Task VerifyAsync()
    {
        try
        {
            service.StartAutomatic(); await Task.Delay(100);
            Check(fixture.Requests == 0 && fixture.HomeRequests == 0 && !service.IsRunning, "unconnected MOOC makes no requests");
            Click("MoocConnectionButton");
            Check(connections.SequenceEqual(new[] { "mooc" }), "MOOC connection button selects its platform");
            var noLogin = await RunAsync(service.ConnectionOpened);
            Check(noLogin.LoginRequired && fixture.Requests == 0 && store.LoadTasks().Count == 3, "logged-out official home never imports another platform");
            Check(store.LoadMoocSettings().LastSyncOutcome == "login_required" && Text("MoocSyncStatusText").Contains("MOOC001", StringComparison.Ordinal), "login-required outcome and banner are visible");
            Check(((Expander)dashboard.FindName("PlatformSyncExpander")).IsExpanded && Text("SyncOverviewText").Contains("MOOC", StringComparison.Ordinal), "MOOC warning enters common platform status");
            fixture.RenewNext = true; Fixtures.SetCookie(browser, fixture.ExpectedToken);
            var imported = await RunAsync(() => service.LoginNavigationCompleted(MoocPlatform.Home.AbsoluteUri), summary => !summary.LoginRequired);
            Check(imported.Courses == 2 && imported.CoursesRead == 2 && imported.Tasks == 8 && imported.Added == 8 && imported.Problems.Count == 0, "official cookie login automatically reads every course term and new exam");
            Check(imported.SpocCourses == 1 && imported.CatalogPages == 2 && imported.NewExamTasks == 2 && imported.StatusNeedsReview == 2, "MOOC SPOC pagination and review counters reflect metadata");
            Check(imported.TasksWithDeadline == 8 && imported.TasksWithLink == 8 && imported.Requests == 6, "all MOOC metadata includes deadlines and exact links");
            Check(fixture.RedirectedHomeRequests > 0 && browser.CoreWebView2.Source.Contains("userId=", StringComparison.Ordinal),
                "official script redirect completes despite aborted initial navigation");
            await WaitAsync(() => Table.Items.Count == 11);
            Check(store.LoadMoocSettings().LastSyncOutcome == "completed" && service.SessionPersistenceOutcome == "saved", "successful automatic run stores protected session");
            Check(browser.Visibility == Visibility.Hidden && browser.CoreWebView2.Environment.UserDataFolder == MoocSyncService.ProfilePath(runRoot), "MOOC uses its own hidden profile");
            var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync("");
            Check(cookies.Single(cookie => cookie.Name == Fixtures.AuthName).Value == "synthetic-renewed", "HTTP renewal reaches WebView profile"); cookies.Clear();
            service.ConnectionClosed();
            await VerifyAggregationAsync();
            await VerifyCourseRecordingAsync();
            await VerifySwitchesAsync();
            await VerifyRacesAsync();
            await VerifyAccountNavigationAsync();
            await VerifyPartialAndClearAsync();
            await VerifyConnectionWindowAsync();
            await VerifyLayoutAsync();
            restartChecks = await SessionRestartChecks.VerifyAsync(runRoot);
            Check(await service.SaveForExitAsync(), "desktop service saves session at graceful exit");
            passed = true;
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        finally
        {
            File.WriteAllText(Path.Combine(runRoot, "mooc-desktop-checks.json"), JsonSerializer.Serialize(new
            {
                outcome = passed ? "passed" : "failed", checks, restartChecks, lastCheck, failure, tableHeights,
                requests = fixture.Requests, homeRequests = fixture.HomeRequests, requestPaths = fixture.RequestPaths.Distinct().ToArray(),
                moocOutcome = store.LoadMoocSettings().LastSyncOutcome, moocStatus = Text("MoocSyncStatusText"), persistence = service.SessionPersistenceOutcome,
                data = "synthetic_only", network = "local_handler_and_webview_fixture_only", externalBrowser = "captured_without_launch"
            }, new JsonSerializerOptions { WriteIndented = true }));
            service.Dispose(); dashboard.CloseForExit(); app.Shutdown(passed ? 0 : 1);
        }
    }

    private static async Task VerifyAccountNavigationAsync()
    {
        var before = store.LoadTasks(); var requests = fixture.Requests;
        fixture.SlowHomeResource = true;
        fixture.ResourceEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ResourceRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await service.StartAsync(true).WaitAsync(TimeSpan.FromSeconds(10));
            Check(store.LoadMoocSettings().LastSyncOutcome == "completed" && fixture.Requests == requests + 6,
                "account DOM readiness allows sync while optional image remains pending");
        }
        finally { fixture.SlowHomeResource = false; fixture.ResourceRelease.TrySetResult(true); }
        fixture.SlowHomeScript = true; requests = fixture.Requests;
        fixture.ResourceEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ResourceRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await service.StartAsync(true).WaitAsync(TimeSpan.FromSeconds(10));
            Check(fixture.ResourceEntered.Task.IsCompleted && !fixture.ResourceRelease.Task.IsCompleted
                && store.LoadMoocSettings().LastSyncOutcome == "completed" && fixture.Requests == requests + 6,
                "confirmed account sync proceeds while a trailing script blocks DOMContentLoaded");
        }
        finally { fixture.SlowHomeScript = false; fixture.ResourceRelease.TrySetResult(true); }
        fixture.DelayedHomeIdentity = true; requests = fixture.Requests;
        fixture.ResourceEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ResourceRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var pendingIdentity = service.StartAsync(true);
            await fixture.ResourceEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Task.Delay(400);
            Check(service.IsRunning && fixture.Requests == requests && store.LoadMoocSettings().LastSyncOutcome == "completed",
                "unfinished document without account identity cannot be declared logged out");
            fixture.ResourceRelease.TrySetResult(true); await pendingIdentity.WaitAsync(TimeSpan.FromSeconds(10));
            Check(store.LoadMoocSettings().LastSyncOutcome == "completed" && fixture.Requests == requests + 6,
                "delayed official account script and its redirect resume metadata without duplicate tasks");
        }
        finally { fixture.DelayedHomeIdentity = false; fixture.ResourceRelease.TrySetResult(true); }
        fixture.RedirectHomeAway = true; requests = fixture.Requests;
        var completed = new List<MoocSyncSummary>(); void Complete(MoocSyncSummary summary) => completed.Add(summary);
        service.Completed += Complete;
        try { await service.StartAsync(true); }
        finally { fixture.RedirectHomeAway = false; service.Completed -= Complete; }
        Check(fixture.Requests == requests && store.LoadTasks().Count == before.Count && store.LoadMoocSettings().LastSyncOutcome == "error",
            "unsupported account redirect retains tasks and persists failure");
        Check(completed.Count == 1 && completed[0].Problems.Single().Code == "MOOC002"
            && completed[0].Problems[0].Message.Contains("unsupported_redirect", StringComparison.Ordinal)
            && Text("MoocSyncStatusText").Contains("个人中心", StringComparison.Ordinal),
            "account failure shows its exact stage and diagnostic category");
        await service.StartAsync(true);
        Check(store.LoadMoocSettings().LastSyncOutcome == "completed" && store.LoadTasks().Count == before.Count,
            "manual retry recovers after account-page failure without duplicate tasks");
        fixture.RedirectHomeToLogin = true; requests = fixture.Requests;
        try { await service.StartAsync(true); }
        finally { fixture.RedirectHomeToLogin = false; }
        Check(fixture.Requests == requests && store.LoadMoocSettings().LastSyncOutcome == "login_required"
            && store.LoadTasks().Count == before.Count && !File.Exists(Path.Combine(runRoot, ".local", "sessions", "mooc.bin")),
            "official homepage authentication redirect is login-required and preserves tasks");
        await service.StartAsync(true);
    }

    private static void SeedOtherPlatforms()
    {
        var now = DateTimeOffset.UtcNow;
        const string link = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=801&classId=802&cpi=803&workId=804&enc=SYNTHETIC-ONLY";
        store.ImportWorks([new("同名合成作业", "未交", "剩余1小时", WorkListParser.ParseTaskLocation(link))], "合成聚合课程", now);
        foreach (var platform in RainPlatform.All)
        {
            var json = "{\"success\":true,\"data\":{\"course_chapter\":[{\"id\":601,\"section_leaf_list\":[{\"id\":701,\"name\":\"同名合成作业\",\"leaf_type\":6,\"score_deadline\":"
                + now.AddDays(2).ToUnixTimeMilliseconds() + "}]}]}}";
            var schedule = RainClassroomParser.Schedule("{\"success\":true,\"data\":{\"leaf_schedules\":{\"701\":{\"done\":1,\"total\":1}}}}");
            var tasks = RainClassroomParser.Chapter(json, platform, platform.DefaultHost, "9001", new("合成班级", "合成聚合课程", "1001", "1002", "", "", "", true), schedule).Tasks;
            if (tasks.Count != 1) throw new InvalidOperationException("Other-platform fixture contract failed.");
            store.ImportCollectedTasks(tasks, now);
        }
    }

    private static async Task VerifyAggregationAsync()
    {
        Check(Rows().Select(row => row.Task.PlatformId).ToHashSet().SetEquals(new[] { "chaoxing", "yuketang", "changjiang", "mooc" }), "one default table combines all four platforms");
        Check(new[] { "学习通 1 条", "雨课堂 1 条", "长江雨课堂 1 条", "中国大学 MOOC 8 条" }.All(label => Text("AggregationSummaryText").Contains(label, StringComparison.Ordinal)), "source counts agree with merged list");
        Check(Text("ActiveCountText") == "7" && Text("CompleteCountText") == "4", "global task status counters include MOOC and review tasks");
        Check(Rows().Count(row => row.Title == "同名合成作业") == 5 && store.LoadTasks().Select(task => task.SourceKey).Distinct().Count() == 11, "same task names and course IDs stay separate by source and term");
        Check(Rows().Where(row => row.Task.Source == TaskSource.Mooc && row.Task.State == TaskState.Pending)
            .Any(row => row.Priority.StartsWith("P1", StringComparison.Ordinal)) && Rows().Where(row => row.Task.Source == TaskSource.Mooc)
            .All(row => row.Deadline != "未知"), "real MOOC deadlines drive shared priority sorting");
        var before = store.LoadTasks(); var requests = fixture.Requests;
        foreach (var platform in new[] { "chaoxing", "yuketang", "changjiang", "mooc" })
        {
            var row = Rows().First(row => row.Task.PlatformId == platform);
            Table.SelectedItem = row; Table.ScrollIntoView(row); Table.UpdateLayout();
            var container = (DataGridRow)Table.ItemContainerGenerator.ContainerFromItem(row);
            var button = FindVisual<Button>(container)!; Check(button.IsEnabled, "task title is clickable for " + platform);
            var count = opened.Count; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            await WaitAsync(() => opened.Count > count);
            Check(opened.Last() == row.Task.Link, "mixed-list click opens exact official task for " + platform);
        }
        var exam = Rows().First(row => row.Task.Source == TaskSource.Mooc && row.Title == "MOOC 新版考试");
        Table.SelectedItem = exam; await WaitAsync(() => ((Button)dashboard.FindName("GoButton")).IsEnabled);
        var openedBefore = opened.Count; Click("GoButton"); await WaitAsync(() => opened.Count > openedBefore);
        Check(opened.Last().EndsWith("#/learn/newExamAgreement?eid=7001", StringComparison.Ordinal), "new exam toolbar opens its exact official agreement");
        Check(fixture.Requests == requests && store.LoadTasks().SequenceEqual(before), "navigation does not read answers or alter task metadata");
        ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = MoocPlatform.Name;
        Check(Table.Items.Count == 8 && Rows().All(row => row.Task.PlatformId == "mooc"), "optional MOOC filter leaves other sources stored");
        ((ComboBox)dashboard.FindName("StateFilter")).SelectedIndex = 8;
        Check(Table.Items.Count == 2 && Rows().All(row => row.State == "待核对"), "unknown status can be inspected separately");
        Click("ActiveTasksButton");
        Check(Table.Items.Count == 7 && ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedIndex == 0, "global active card includes review tasks across sources");
        ((TextBox)dashboard.FindName("SearchBox")).Text = "NO-MATCH-SYNTHETIC"; Click("ShowAllTasksButton");
        Check(Table.Items.Count == 11 && ((TextBox)dashboard.FindName("SearchBox")).Text.Length == 0, "show all restores full merged table");
        Click("SyncButton"); await WaitAsync(() => !service.IsRunning && fixture.Requests > requests); await WaitAsync(() => Table.Items.Count == 11);
        Check(store.LoadMoocSettings().LastSummary?.Added == 0 && store.LoadTasks().Count == 11 && store.LoadSettings().LastSyncOutcome == "chaoxing-preserved", "sync-all refreshes MOOC without duplicate or changing learning status");
        Check(store.LoadTasks().Where(task => task.Source != TaskSource.Mooc).SequenceEqual(before.Where(task => task.Source != TaskSource.Mooc)), "MOOC refresh preserves other platform metadata");
    }

    private static async Task VerifySwitchesAsync()
    {
        store.SaveMoocSettings(store.LoadMoocSettings() with { AutomaticSyncEnabled = false }); service.SettingsChanged(false);
        var before = fixture.Requests; service.ConnectionOpened(); service.StartAutomatic(); await Task.Delay(350);
        Check(fixture.Requests == before && !service.IsRunning, "per-platform disable stops login timer and automatic requests");
        await service.StartAsync(true); Check(fixture.Requests > before, "manual MOOC sync works with automatic switch off");
        store.SaveMoocSettings(store.LoadMoocSettings() with { AutomaticSyncEnabled = true });
        await RunAsync(() => service.SettingsChanged(true)); service.ConnectionClosed();
        store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = false }); service.SettingsChanged(false);
        before = fixture.Requests; service.StartAutomatic(); service.ConnectionOpened(); await Task.Delay(350);
        Check(fixture.Requests == before, "global automatic switch stops MOOC requests");
        store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = true }); await RunAsync(() => service.SettingsChanged(true)); service.ConnectionClosed();
    }

    private static async Task VerifyRacesAsync()
    {
        fixture.PauseTerm(); fixture.LateRenewal = true;
        var previous = store.LoadMoocSettings(); var taskCount = store.LoadTasks().Count;
        var old = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Fixtures.SetCookie(browser, "synthetic-account-two"); fixture.ExpectedToken = "synthetic-account-two"; fixture.Account = "9002";
        fixture.Release!.TrySetResult(true); await old;
        var current = await browser.CoreWebView2.CookieManager.GetCookiesAsync("");
        Check(current.Single(cookie => cookie.Name == Fixtures.AuthName).Value == "synthetic-account-two", "late HTTP renewal cannot overwrite new login cookies"); current.Clear();
        Check(store.LoadTasks().Count == taskCount && SameSettings(previous), "changed login cannot import old account or replace sync result");
        fixture.ResetPause(); fixture.Account = "9001"; fixture.ExpectedToken = "synthetic-renewed"; Fixtures.SetCookie(browser, fixture.ExpectedToken);
        await service.StartAsync(true);
        fixture.PauseTerm(); old = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var recoveredTask = RunAsync(() => { service.ConnectionOpened(); service.LoginNavigationCompleted(MoocPlatform.Home.AbsoluteUri); fixture.Release!.TrySetResult(true); });
        await old; var recovered = await recoveredTask;
        Check(recovered.Updated == 8 && recovered.Added == 0, "login queued during old run completes automatically after cancellation");
        fixture.ResetPause(); service.ConnectionClosed();
        browser.CoreWebView2.CookieManager.DeleteAllCookies(); await service.StartAsync(true);
        var cookieOnly = await RunAsync(() => { service.ConnectionOpened(); Fixtures.SetCookie(browser, fixture.ExpectedToken); }, result => !result.LoginRequired);
        Check(cookieOnly.Updated == 8, "cookie-only official login works without top-level navigation"); service.ConnectionClosed();
        fixture.PauseTerm(); var before = fixture.Requests; previous = store.LoadMoocSettings();
        old = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(((Button)dashboard.FindName("SyncButton")).IsEnabled == false && ((Button)dashboard.FindName("StopSyncButton")).Visibility == Visibility.Visible, "common sync controls show MOOC busy state");
        await service.StartAsync(true); Check(fixture.Requests == before + 3, "busy manual sync does not add duplicate requests");
        Click("StopSyncButton"); fixture.Release!.TrySetResult(true); await old;
        Check(!service.IsRunning && SameSettings(previous) && store.LoadTasks().Count == taskCount, "global stop cancels MOOC before import"); fixture.ResetPause();
    }

    private static async Task VerifyPartialAndClearAsync()
    {
        fixture.UnknownIdentity = true; var count = fixture.Requests; await service.StartAsync(true);
        Check(fixture.Requests == count && store.LoadMoocSettings().LastSyncOutcome == "partial" && store.LoadTasks().Count == 11, "unknown account identity is visible and cannot import"); fixture.UnknownIdentity = false;
        fixture.UnknownNewExams = true; await service.StartAsync(true);
        Check(store.LoadMoocSettings().LastSyncOutcome == "partial" && store.LoadMoocSettings().LastSummary?.CoursesRead == 0 && store.LoadTasks().Count == 11, "unknown new exams keep known tasks with incomplete coverage"); fixture.UnknownNewExams = false;
        fixture.EmptyCourses = true; await service.StartAsync(true);
        Check(store.LoadMoocSettings().LastSyncOutcome == "completed" && store.LoadMoocSettings().LastSummary?.Courses == 0 && store.LoadTasks().Count == 11, "confirmed empty course list preserves existing local tasks"); fixture.EmptyCourses = false;
        await service.StartAsync(true);
        fixture.RejectRpc = true; await service.StartAsync(true);
        Check(store.LoadMoocSettings().LastSyncOutcome == "login_required" && !File.Exists(Path.Combine(runRoot, ".local", "sessions", "mooc.bin")) && store.LoadTasks().Count == 11,
            "official RPC expiry removes archive and keeps all platform tasks"); fixture.RejectRpc = false;
        Fixtures.SetCookie(browser, fixture.ExpectedToken); await service.StartAsync(true);
        fixture.PauseTerm(); var old = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var clear = service.ClearSessionAsync(); await Task.Delay(50); fixture.Release!.TrySetResult(true); await old; await clear;
        Check((await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Count == 0 && service.SessionPersistenceOutcome == "cleared", "reconnect awaits old run and clears only MOOC profile");
        Check(store.LoadTasks().Count == 11 && !File.Exists(Path.Combine(runRoot, ".local", "sessions", "mooc.bin")), "reconnect preserves combined tasks and removes session archive"); fixture.ResetPause();
        Fixtures.SetCookie(browser, fixture.ExpectedToken); await service.StartAsync(true);
    }

    private static async Task VerifyConnectionWindowAsync()
    {
        var window = new MoocConnectionWindow(service, dashboard.ShowDashboard);
        var loginBrowser = ((DockPanel)window.Content).Children.OfType<WebView2>().Single(); fixture.BlockBrowser(loginBrowser); window.Show();
        await WaitAsync(() => loginBrowser.CoreWebView2?.Source == MoocPlatform.Login.AbsoluteUri);
        Check(loginBrowser.CoreWebView2.Environment.UserDataFolder == browser.CoreWebView2.Environment.UserDataFolder, "official login window shares only MOOC profile");
        Check(new Uri(loginBrowser.CoreWebView2.Source).AbsolutePath == "/member/login.htm"
            && System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(RainAddressPolicy.Query(new Uri(loginBrowser.CoreWebView2.Source))!["returnUrl"])) == MoocPlatform.Home.AbsoluteUri,
            "connection opens official sign-in form directly and returns to personal home");
        Check(loginBrowser.CoreWebView2.Settings.IsPasswordAutosaveEnabled == false && loginBrowser.CoreWebView2.Settings.IsGeneralAutofillEnabled == false, "login credentials are not retained in browser autofill");
        window.Close(); service.Cancel(); await WaitAsync(() => !service.IsRunning);
        Check(MoocPlatform.IsLoginNavigation("https://open.weixin.qq.com/connect/qrconnect") && MoocPlatform.IsLoginNavigation("https://webzj.reg.163.com/"), "official WeChat and URS login jumps supported");
    }

    private static async Task VerifyLayoutAsync()
    {
        Click("ShowAllTasksButton"); var expander = (Expander)dashboard.FindName("PlatformSyncExpander");
        expander.IsExpanded = false; await Task.Delay(100); Capture("mooc-unified-dashboard.png");
        dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight; await Task.Delay(100); dashboard.UpdateLayout();
        foreach (var name in new[] { "SyncButton", "RainConnectionButton", "ChangjiangConnectionButton", "MoocConnectionButton", "ChooseRecordingCoursesButton", "AggregationTitleText", "TaskTable" })
        {
            var element = (FrameworkElement)dashboard.FindName(name); var content = (FrameworkElement)dashboard.Content;
            var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1, "minimum layout keeps " + name + " visible");
        }
        tableHeights["collapsed"] = Table.ActualHeight; Capture("mooc-unified-dashboard-minimum.png");
        Check(Table.ActualHeight >= 230, "minimum collapsed layout shows several unified task rows");
        expander.IsExpanded = true; ((Expander)dashboard.FindName("SelectedDetailsExpander")).IsExpanded = true;
        await Task.Delay(100); dashboard.UpdateLayout(); tableHeights["bothExpanded"] = Table.ActualHeight; Capture("mooc-unified-dashboard-expanded-minimum.png");
        Check(Table.ActualHeight >= 100, "minimum layout retains task rows with both detail panels expanded");
    }

    private static void Capture(string filename)
    {
        CaptureWindow(dashboard, filename);
    }
    private static void CaptureWindow(Window window, string filename)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen())
        { var bounds = new Rect(content.RenderSize); context.DrawRectangle(window.Background, null, bounds); context.DrawRectangle(new VisualBrush(content), null, bounds); }
        bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(runRoot, filename)); encoder.Save(output);
    }
    private static DataGrid Table => (DataGrid)dashboard.FindName("TaskTable");
    private static TaskDashboardWindow.TaskDisplayRow[] Rows() => Table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().ToArray();
    private static string Text(string name) => ((TextBlock)dashboard.FindName(name)).Text;
    private static bool SameSettings(MoocSyncSettings previous) => JsonSerializer.Serialize(store.LoadMoocSettings()) == JsonSerializer.Serialize(previous);
    private static void Click(string name) => ((Button)dashboard.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task<MoocSyncSummary> RunAsync(Action start, Func<MoocSyncSummary, bool>? matches = null)
    {
        var result = new TaskCompletionSource<MoocSyncSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(MoocSyncSummary summary) { if (matches is null || matches(summary)) result.TrySetResult(summary); }
        service.Completed += Completed;
        try { start(); var summary = await result.Task.WaitAsync(TimeSpan.FromSeconds(30)); await WaitAsync(() => !service.IsRunning); await Task.Delay(75); return summary; }
        finally { service.Completed -= Completed; }
    }
    private static async Task WaitAsync(Func<bool> ready)
    { var expires = DateTimeOffset.UtcNow.AddSeconds(10); while (!ready()) { if (DateTimeOffset.UtcNow > expires) throw new TimeoutException("After " + lastCheck); await Task.Delay(25); } }
    private static void Check(bool condition, string label)
    { lastCheck = label; if (!condition) throw new InvalidOperationException(label); checks++; }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { var child = VisualTreeHelper.GetChild(parent, index); if (child is T match) return match; if (FindVisual<T>(child) is { } nested) return nested; }
        return null;
    }
}
