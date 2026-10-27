using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RainSyncHarness;

internal static class Program
{
    private static Application app = null!;
    private static TaskDashboardWindow dashboard = null!;
    private static TaskStore store = null!;
    private static string runRoot = "";
    private static int checks;
    private static bool passed;
    private static string? failure;
    private static string lastCheck = "";
    private static readonly List<string> opened = [];
    private static readonly List<string> connectionClicks = [];
    private static readonly Dictionary<string, RainSyncService> services = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, FixtureSession> sessions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, double> tableHeights = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, double> layoutHeights = new(StringComparer.Ordinal);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--project-root" || args[2] != "--verify") return 2;
        var root = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var harnessRoot = Path.Combine(root, ".local", "rain-sync-harness");
        Directory.CreateDirectory(harnessRoot);
        runRoot = Path.Combine(harnessRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(Path.Combine(harnessRoot, "latest-run.txt"), runRoot);
        Environment.SetEnvironmentVariable("TEMP", runRoot); Environment.SetEnvironmentVariable("TMP", runRoot);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        store = new TaskStore(Path.Combine(runRoot, "tasks.db"));
        store.SaveSettings(new(false, true) { LastSyncOutcome = "chaoxing-untouched" });
        dashboard = new(store, runRoot, () => connectionClicks.Add("chaoxing"), () => app.Shutdown(), opened.Add)
        { Title = "学习任务聚合 · 雨课堂离线桌面验收" };
        dashboard.PlatformConnectionRequested += connectionClicks.Add;
        foreach (var platform in RainPlatform.All)
        {
            var browser = Browser(platform.Id);
            BlockNetwork(browser);
            var fixture = new FixtureSession(platform); sessions.Add(platform.Id, fixture);
            var service = new RainSyncService(store, runRoot, platform, browser, () => new FixtureHandler(fixture), TimeSpan.FromMilliseconds(500));
            services.Add(platform.Id, service); dashboard.AttachRainSync(service);
            dashboard.AutomaticSyncSettingChanged += service.SettingsChanged;
        }
        dashboard.Loaded += (_, _) => dashboard.Dispatcher.BeginInvoke(new Action(async () => await VerifyAsync()));
        dashboard.Closed += (_, _) => { foreach (var service in services.Values) service.Dispose(); app.Shutdown(); };
        app.MainWindow = dashboard; app.Run(dashboard);
        foreach (var service in services.Values) service.Dispose();
        return passed ? 0 : 1;
    }

    private static async Task VerifyAsync()
    {
        try
        {
            foreach (var service in services.Values) service.StartAutomatic();
            await Task.Delay(100);
            Check(sessions.Values.All(session => session.Requests == 0), "unconnected platforms send no requests");
            Check(services.Values.All(service => !service.IsRunning), "unconnected services remain idle");
            ((Button)dashboard.FindName("RainConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)dashboard.FindName("ChangjiangConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(connectionClicks.SequenceEqual(new[] { "yuketang", "changjiang" }), "platform connection buttons target correct service");

            foreach (var platform in RainPlatform.All)
            {
                var service = services[platform.Id]; var fixture = sessions[platform.Id];
                var loginRequired = await RunAsync(service, service.ConnectionOpened);
                Check(loginRequired.LoginRequired && store.LoadTasks().Count == (platform.Id == "yuketang" ? 0 : 2), "expired session keeps other platform tasks");
                Check(store.LoadPlatformSettings(platform.Id).LastSyncOutcome == "login_required", "independent login-required outcome is persisted");
                Check(((TextBlock)dashboard.FindName(platform.Id == "yuketang" ? "RainSyncStatusText" : "ChangjiangSyncStatusText")).Text.Contains("RAIN001", StringComparison.Ordinal), "login-required banner is visible");
                Check(((Expander)dashboard.FindName("PlatformSyncExpander")).IsExpanded
                    && ((TextBlock)dashboard.FindName("SyncOverviewText")).Text.Contains(platform.Name, StringComparison.Ordinal),
                    "a login problem expands the selected platform warning without hiding task rows");
                fixture.LoggedIn = true;
                var loggedIn = await RunAsync(service, () => service.LoginNavigationCompleted(platform.IndexUri(platform.DefaultHost).AbsoluteUri));
                Check(loggedIn.CoursesRead == 1 && loggedIn.Tasks == 2 && loggedIn.Added == 2 && loggedIn.Problems.Count == 0, "official login automatically imports both task types");
                await WaitAsync(() => ((DataGrid)dashboard.FindName("TaskTable")).Items.Count == store.LoadTasks().Count);
                Check(((DataGrid)dashboard.FindName("TaskTable")).Items.Cast<TaskDashboardWindow.TaskDisplayRow>()
                    .Any(row => row.Task.PlatformId == platform.Id && row.Task.State == TaskState.Completed),
                    "a completed task imported after login is immediately visible in the default list");
                Check(store.LoadPlatformSettings(platform.Id).LastSyncOutcome == "completed", "successful platform outcome is persisted");
                Check(Browser(platform.Id).CoreWebView2 is not null && Browser(platform.Id).Visibility == Visibility.Hidden, "hidden isolated session host is ready");
                service.ConnectionClosed();
            }
            Check(store.LoadTasks().Count == 4 && store.LoadTasks().Select(task => task.SourceKey).Distinct().Count() == 4, "same task IDs remain isolated by platform");
            Check(store.LoadSettings().LastSyncOutcome == "chaoxing-untouched", "rain does not overwrite chaoxing sync result");
            var rainBrowser = Browser("yuketang"); var cjBrowser = Browser("changjiang");
            rainBrowser.CoreWebView2.CookieManager.AddOrUpdateCookie(rainBrowser.CoreWebView2.CookieManager.CreateCookie("synthetic-account", "RAIN", ".yuketang.cn", "/"));
            cjBrowser.CoreWebView2.CookieManager.AddOrUpdateCookie(cjBrowser.CoreWebView2.CookieManager.CreateCookie("synthetic-account", "CJ", ".yuketang.cn", "/"));
            var rainCookies = await rainBrowser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.yuketang.cn/");
            var cjCookies = await cjBrowser.CoreWebView2.CookieManager.GetCookiesAsync("https://changjiang.yuketang.cn/");
            Check(rainCookies.Single(cookie => cookie.Name == "synthetic-account").Value == "RAIN" && cjCookies.Single(cookie => cookie.Name == "synthetic-account").Value == "CJ", "separate profiles cannot mix account cookies");
            Check(rainBrowser.CoreWebView2.Environment.UserDataFolder != cjBrowser.CoreWebView2.Environment.UserDataFolder, "two profile folders are distinct");
            await VerifyClicksAsync();
            await VerifyFiltersAsync();
            var rainService = services["yuketang"]; var rainSession = sessions["yuketang"];
            var countBefore = store.LoadTasks().Count;
            await rainService.StartAsync(true);
            Check(store.LoadTasks().Count == countBefore && store.LoadPlatformSettings("yuketang").LastSummary?.Added == 0, "repeat sync does not duplicate tasks");
            var rainItem = store.LoadTasks().First(task => task.PlatformId == "yuketang" && TaskRules.IsActive(task));
            rainItem = store.SetState(rainItem.Id, rainItem.Revision, TaskState.Ignored, DateTimeOffset.UtcNow);
            await rainService.StartAsync(true);
            Check(store.Get(rainItem.Id)!.State == TaskState.Ignored && store.Get(rainItem.Id)!.Overrides.HasFlag(TaskOverrides.State), "manual ignore survives automatic platform refresh");
            var beforeRequests = rainSession.Requests;
            var settings = store.LoadPlatformSettings("yuketang") with { AutomaticSyncEnabled = false };
            store.SavePlatformSettings("yuketang", settings); rainService.SettingsChanged(false);
            rainService.ConnectionOpened(); rainService.StartAutomatic();
            await Task.Delay(200);
            Check(rainSession.Requests == beforeRequests, "per-platform automatic switch stops login polling");
            Check(((TextBlock)dashboard.FindName("RainSyncStatusText")).Text.Contains("关闭", StringComparison.Ordinal), "per-platform disabled warning stays visible");
            await rainService.StartAsync(true);
            Check(rainSession.Requests > beforeRequests && store.LoadPlatformSettings("yuketang").LastSummary?.Updated == 2, "manual sync still works with automatic switch off");
            store.SavePlatformSettings("yuketang", store.LoadPlatformSettings("yuketang") with { AutomaticSyncEnabled = true });
            await RunAsync(rainService, () => rainService.SettingsChanged(true));
            store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = false });
            foreach (var service in services.Values) service.SettingsChanged(false);
            var allBefore = sessions.Values.Sum(session => session.Requests);
            foreach (var service in services.Values) { service.StartAutomatic(); service.ConnectionOpened(); }
            await Task.Delay(200);
            Check(sessions.Values.Sum(session => session.Requests) == allBefore, "global automatic switch stops all platform requests");
            store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = true });
            await RunAsync(rainService, () => rainService.SettingsChanged(true));
            // Login completes while an earlier check was still reading the expired account response.
            rainSession.LoggedIn = false;
            rainSession.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            rainSession.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var stale = rainService.StartAsync(true);
            await rainSession.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            rainSession.LoggedIn = true;
            var recovered = await RunAsync(rainService, () =>
            {
                rainService.ConnectionOpened(); rainService.LoginNavigationCompleted("https://www.yuketang.cn/v2/web/index");
                rainSession.Release.TrySetResult(true);
            }, result => !result.LoginRequired);
            await stale;
            Check(!recovered.LoginRequired && recovered.Updated == 2, "login queued during active sync is not lost");
            rainSession.Entered = rainSession.Release = null;
            // Cookie-only login completion must work without top-level navigation.
            rainSession.LoggedIn = false; await rainService.StartAsync(true);
            var cookieOnly = await RunAsync(rainService, () => { rainService.ConnectionOpened(); rainSession.LoggedIn = true; });
            Check(!cookieOnly.LoginRequired && cookieOnly.Updated == 2, "cookie-only login is detected by timer");
            // Broken paging or metadata remains visible as partial rather than an empty successful sync.
            rainSession.Partial = true;
            await rainService.StartAsync(true);
            Check(store.LoadPlatformSettings("yuketang").LastSyncOutcome == "partial" && store.LoadTasks().Count == countBefore, "partial metadata preserves existing tasks");
            Check(((TextBlock)dashboard.FindName("RainSyncStatusText")).Text.Contains("未完成", StringComparison.Ordinal), "partial coverage warning is visible");
            rainSession.Partial = false;
            await rainService.ClearSessionAsync();
            rainCookies = await rainBrowser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.yuketang.cn/");
            cjCookies = await cjBrowser.CoreWebView2.CookieManager.GetCookiesAsync("https://changjiang.yuketang.cn/");
            Check(rainCookies.Count == 0 && cjCookies.Any(cookie => cookie.Name == "synthetic-account"), "reconnect clears only selected profile");
            Check(store.LoadTasks().Count == countBefore, "reconnect preserves local task database");
            var clearRecovery = await RunAsync(rainService, rainService.ConnectionOpened);
            Check(!clearRecovery.LoginRequired && clearRecovery.Updated == 2, "synchronization resumes after reconnect");
            await VerifyConnectionCancellationAsync(rainService, rainSession, countBefore);
            // Aggregate the current site before the connection-window check intentionally selects another site.
            await VerifyAggregationAsync();
            await VerifyConnectionWindowAsync(rainService);
            passed = true; WriteReport(); app.Shutdown();
        }
        catch (Exception error)
        {
            failure = error.GetType().Name; WriteReport(); app.Shutdown(1);
        }
    }

    private static async Task VerifyConnectionCancellationAsync(RainSyncService service, FixtureSession fixture, int expectedTasks)
    {
        service.ConnectionClosed(); service.Cancel();
        fixture.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = service.StartAsync(true);
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.SelectHostAsync("PRO.YUKETANG.CN");
        await pending;
        Check(!service.IsRunning && service.Host == "pro.yuketang.cn", "host switch cancels and awaits prior run before normalizing host");
        Check(store.LoadPlatformSettings("yuketang") is { LastSummary: null, LastSyncOutcome: "not_started" }, "old host completion cannot overwrite newly selected host outcome");
        Check(store.LoadTasks().Count == expectedTasks && store.LoadPlatformSettings("changjiang").Host == "changjiang.yuketang.cn", "host switch preserves tasks and other platform settings");
        fixture.Entered = fixture.Release = null;
        await service.SelectHostAsync("www.yuketang.cn");
        await service.StartAsync(true);
        var before = store.LoadPlatformSettings("yuketang");
        var core = Browser("yuketang").CoreWebView2;
        core.CookieManager.AddOrUpdateCookie(core.CookieManager.CreateCookie("synthetic-clear-race", "RAIN", ".yuketang.cn", "/"));
        fixture.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = service.StartAsync(true);
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.ClearSessionAsync(); await pending;
        Check(!service.IsRunning && store.LoadPlatformSettings("yuketang").LastSyncAtUtc == before.LastSyncAtUtc, "clearing during request stops run without recording stale completion");
        var ownCookies = await core.CookieManager.GetCookiesAsync("https://www.yuketang.cn/");
        var otherCookies = await Browser("changjiang").CoreWebView2.CookieManager.GetCookiesAsync("https://changjiang.yuketang.cn/");
        Check(ownCookies.Count == 0 && otherCookies.Any(cookie => cookie.Name == "synthetic-account"), "clearing active run affects only selected login profile");
        Check(store.LoadTasks().Count == expectedTasks, "clearing active run preserves existing task database");
        fixture.Entered = fixture.Release = null;
        await service.StartAsync(true);
    }

    private static async Task VerifyConnectionWindowAsync(RainSyncService service)
    {
        var window = new RainConnectionWindow(service, dashboard.ShowDashboard);
        var browser = (WebView2)((DockPanel)window.Content).Children.OfType<WebView2>().Single();
        BlockNetwork(browser); window.Show();
        await WaitAsync(() => browser.CoreWebView2 is not null);
        await WaitAsync(() => browser.CoreWebView2.Source == "https://www.yuketang.cn/web");
        Check(browser.CoreWebView2.Environment.UserDataFolder == Browser("yuketang").CoreWebView2.Environment.UserDataFolder, "official login window shares selected profile");
        Check(browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled == false && browser.CoreWebView2.Settings.IsGeneralAutofillEnabled == false, "login fields are user-owned and not retained by autofill");
        var combo = FindLogical<ComboBox>((DependencyObject)window.Content)!;
        combo.SelectedItem = "pro.yuketang.cn";
        await WaitAsync(() => service.Host == "pro.yuketang.cn" && browser.CoreWebView2.Source == "https://pro.yuketang.cn/web");
        Check(service.Host == "pro.yuketang.cn" && store.LoadPlatformSettings("changjiang").Host == "changjiang.yuketang.cn", "standard and pro host selector does not affect changjiang");
        window.Close();
        await Task.Delay(100);
    }

    private static async Task VerifyClicksAsync()
    {
        var table = (DataGrid)dashboard.FindName("TaskTable");
        ((ComboBox)dashboard.FindName("StateFilter")).SelectedIndex = 1;
        await WaitAsync(() => table.Items.Count == 4);
        CaptureDashboard("rain-synthetic-dashboard.png");
        var fullWidth = dashboard.Width; var fullHeight = dashboard.Height;
        dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight;
        await Task.Delay(100); dashboard.UpdateLayout();
        foreach (var name in new[] { "RainConnectionButton", "ChangjiangConnectionButton", "AggregationTitleText", "ShowAllTasksButton", "TaskTable" })
        {
            var element = (FrameworkElement)dashboard.FindName(name);
            var bounds = element.TransformToAncestor((Visual)dashboard.Content).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Left >= -1 && bounds.Top >= -1
                && bounds.Right <= ((FrameworkElement)dashboard.Content).ActualWidth + 1
                && bounds.Bottom <= ((FrameworkElement)dashboard.Content).ActualHeight + 1 && element.ActualHeight > 0,
                "minimum dashboard size keeps " + name + " within visible layout");
        }
        tableHeights["minimumExpandedInitial"] = table.ActualHeight;
        var dock = (DockPanel)dashboard.Content;
        foreach (var (element, index) in dock.Children.Cast<FrameworkElement>().Select((element, index) => (element, index)))
        {
            layoutHeights[$"dock{index}:{element.GetType().Name}"] = element.ActualHeight;
            if (element is StackPanel stack)
                foreach (var (child, childIndex) in stack.Children.Cast<FrameworkElement>().Select((child, childIndex) => (child, childIndex)))
                    layoutHeights[$"dock{index}/child{childIndex}:{child.GetType().Name}"] = child.ActualHeight;
        }
        CaptureDashboard("rain-synthetic-dashboard-minimum.png");
        Check(table.ActualHeight >= 100, "minimum dashboard size leaves room for task rows");
        dashboard.Width = fullWidth; dashboard.Height = fullHeight;
        await Task.Delay(100); dashboard.UpdateLayout();
        var beforeRequests = sessions.Values.Sum(session => session.Requests);
        var beforeTasks = store.LoadTasks();
        foreach (var platform in RainPlatform.All)
        {
            var row = table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().First(item => item.Task.PlatformId == platform.Id);
            table.SelectedItem = row; table.ScrollIntoView(row); table.UpdateLayout();
            var container = (DataGridRow)table.ItemContainerGenerator.ContainerFromItem(row);
            var title = FindVisual<Button>(container)!;
            Check(title is not null && title.IsEnabled, "task title renders clickable official link");
            var before = opened.Count; title!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, title));
            await WaitAsync(() => opened.Count > before);
            Check(opened.Last() == row.Task.Link && new Uri(opened.Last()).Host == platform.DefaultHost, "title click opens exact task on correct platform");
            var toolbar = (Button)dashboard.FindName("GoButton");
            await WaitAsync(() => toolbar.IsEnabled);
            before = opened.Count; toolbar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toolbar));
            await WaitAsync(() => opened.Count > before);
            Check(opened.Last() == row.Task.Link, "toolbar opens same selected official task");
        }
        await Task.Delay(100);
        Check(sessions.Values.Sum(session => session.Requests) == beforeRequests, "task navigation does not fetch answer pages");
        Check(store.LoadTasks().SequenceEqual(beforeTasks), "task clicks do not mutate task status or content");
        Check(((Button)dashboard.FindName("SyncButton")).IsEnabled, "sync button resets after automatic runs");
        var summaryButton = FindLogical<Button>((DependencyObject)dashboard.Content, button => button.Content?.ToString() == "同步详情")!;
        summaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, summaryButton));
        await Task.Delay(100);
        var detailsWindow = app.Windows.Cast<Window>().Single(window => window != dashboard);
        var detailText = CollectText((DependencyObject)detailsWindow.Content);
        Check(detailText.Contains("雨课堂同步详情", StringComparison.Ordinal) && detailText.Contains("长江雨课堂同步详情", StringComparison.Ordinal), "sync details display both independent platforms");
        detailsWindow.Close();
    }

    private static async Task VerifyFiltersAsync()
    {
        var table = (DataGrid)dashboard.FindName("TaskTable");
        var state = (ComboBox)dashboard.FindName("StateFilter");
        var platform = (ComboBox)dashboard.FindName("PlatformFilter");
        var search = (TextBox)dashboard.FindName("SearchBox");
        var selected = store.LoadTasks().First(task => task.PlatformId == "changjiang" && task.State == TaskState.Completed);
        var unchanged = store.LoadTasks();
        search.Text = "NO-MATCH-SYNTHETIC"; state.SelectedIndex = 0; platform.SelectedItem = "长江雨课堂";
        ((Button)dashboard.FindName("ShowAllTasksButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => table.Items.Count == 4);
        Check(table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Any(row => row.Task.Id == selected.Id)
            && table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Select(row => row.Task.PlatformId).Distinct().Count() == 2,
            "unified view reveals completed tasks from both platforms despite prior platform search and pending filters");
        Check(search.Text.Length == 0 && state.SelectedIndex == 1 && platform.SelectedIndex == 0,
            "unified view action clears search and shows every platform and task state");
        state.SelectedIndex = 0;
        Check(!table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Any(row => row.Task.Id == selected.Id)
            && ((TextBlock)dashboard.FindName("FilterCountText")).Text.Contains("已完成任务被当前筛选隐藏", StringComparison.Ordinal),
            "pending filter explains why a successfully imported completed task is hidden");
        search.Text = "NO-MATCH-SYNTHETIC";
        ((Button)dashboard.FindName("ShowAllTasksButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => table.Items.Count == 4);
        Check(platform.SelectedIndex == 0 && state.SelectedIndex == 1 && search.Text.Length == 0,
            "show all tasks restores both platforms and clears incompatible filters");
        platform.SelectedItem = "雨课堂";
        await WaitAsync(() => table.Items.Count == 2);
        Check(table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().All(row => row.Task.PlatformId == "yuketang"),
            "optional platform filter selects only rain tasks");
        Check(((TextBlock)dashboard.FindName("FilterCountText")).Text.Contains("2/4", StringComparison.Ordinal)
            && ((TextBlock)dashboard.FindName("FilterCountText")).Text.Contains("2 条其他平台任务", StringComparison.Ordinal),
            "optional platform filter preserves the global total and explains hidden tasks from other platforms");
        Check(store.LoadTasks().SequenceEqual(unchanged), "filter recovery never changes stored completion status or deadlines");
        ((Button)dashboard.FindName("ShowAllTasksButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => table.Items.Count == 4);
    }

    private static async Task VerifyAggregationAsync()
    {
        await WaitAsync(() => services.Values.All(service => !service.IsRunning));
        var table = (DataGrid)dashboard.FindName("TaskTable");
        var state = (ComboBox)dashboard.FindName("StateFilter");
        var platform = (ComboBox)dashboard.FindName("PlatformFilter");
        var search = (TextBox)dashboard.FindName("SearchBox");
        var now = DateTimeOffset.UtcNow;
        var ignoredRain = store.LoadTasks().First(task => task.PlatformId == "yuketang" && task.State == TaskState.Ignored);
        store.SetState(ignoredRain.Id, ignoredRain.Revision, TaskState.Pending, now);
        const string pendingLink = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=801&classId=802&cpi=803&workId=804&answerId=805&enc=SYNTHETIC-ONLY";
        const string completedLink = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=801&classId=802&cpi=803&workId=806&answerId=807&enc=SYNTHETIC-ONLY";
        store.ImportWorks([
            new("同名合成作业", "未交", "剩余1小时", WorkListParser.ParseTaskLocation(pendingLink)),
            new("合成学习通已提交作业", "待批阅", "剩余48小时", WorkListParser.ParseTaskLocation(completedLink))
        ], "合成聚合课程", now);
        var manual = store.Create(new("同名合成作业", "mooc", "合成聚合课程", DeadlineKind.Known,
            null, now.AddHours(7), "https://example.invalid/synthetic-aggregate", null, "", "合成手动任务", null), now);
        ((Button)dashboard.FindName("ShowAllTasksButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => table.Items.Count == 7);
        TaskDashboardWindow.TaskDisplayRow[] Rows() => table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().ToArray();
        Check(Rows().Count(row => row.Task.PlatformId == "chaoxing") == 2
            && Rows().Count(row => row.Task.PlatformId == "yuketang") == 2
            && Rows().Count(row => row.Task.PlatformId == "changjiang") == 2
            && Rows().Count(row => row.Task.PlatformId == "mooc") == 1,
            "one default table aggregates learning rain changjiang and manually maintained tasks");
        Check(((TextBlock)dashboard.FindName("AggregationTitleText")).Text.Contains("共 7 条", StringComparison.Ordinal)
            && new[] { "学习通 2 条", "雨课堂 2 条", "长江雨课堂 2 条", "中国大学 MOOC 1 条" }
                .All(text => ((TextBlock)dashboard.FindName("AggregationSummaryText")).Text.Contains(text, StringComparison.Ordinal)),
            "unified task total and source counts account for every platform");
        Check(Rows().Count(row => row.Title == "同名合成作业" && row.Course == "合成聚合课程") == 2,
            "tasks with the same title and course retain separate platform identities");
        Check(Rows()[0].Task.PlatformId == "chaoxing" && Rows()[1].Task.Id == manual.Id
            && Rows()[0].Priority.StartsWith("P1", StringComparison.Ordinal)
            && Rows()[1].Priority.StartsWith("P2", StringComparison.Ordinal)
            && Rows().Take(4).All(row => TaskRules.IsActive(row.Task))
            && Rows().Skip(4).All(row => row.Task.State == TaskState.Completed),
            "unified priority sorting compares due tasks across platforms before completed tasks");

        var before = store.LoadTasks();
        var requestCounts = sessions.ToDictionary(pair => pair.Key, pair => pair.Value.Requests);
        platform.SelectedItem = "长江雨课堂"; state.SelectedIndex = 0; search.Text = "NO-MATCH-SYNTHETIC";
        ((Button)dashboard.FindName("SyncButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => services.Values.All(service => !service.IsRunning)
            && sessions.All(pair => pair.Value.Requests > requestCounts[pair.Key]));
        await WaitAsync(() => table.Items.Count == 7);
        Check(platform.SelectedIndex == 0 && state.SelectedIndex == 1 && search.Text.Length == 0
            && Rows().Select(row => row.Task.Id).ToHashSet().SetEquals(before.Select(task => task.Id)),
            "sync all refreshes both connected platforms and restores the merged list without duplicates");
        Check(store.LoadTasks().Where(task => task.Source != TaskSource.RainClassroom)
            .SequenceEqual(before.Where(task => task.Source != TaskSource.RainClassroom)),
            "refreshing connected rain platforms preserves learning and manual task metadata");

        foreach (var (buttonName, stateIndex, matches) in new (string, int, Func<TaskItem, bool>)[]
        {
            ("ActiveTasksButton", 0, TaskRules.IsActive),
            ("OverdueTasksButton", 6, task => TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc < DateTimeOffset.UtcNow),
            ("SoonTasksButton", 7, task => TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task)
                && task.DeadlineUtc >= DateTimeOffset.UtcNow && task.DeadlineUtc <= DateTimeOffset.UtcNow.AddHours(24)),
            ("CompletedTasksButton", 3, task => task.State == TaskState.Completed)
        })
        {
            platform.SelectedItem = "长江雨课堂"; search.Text = "NO-MATCH-SYNTHETIC";
            ((Button)dashboard.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(platform.SelectedIndex == 0 && state.SelectedIndex == stateIndex && search.Text.Length == 0
                && Rows().Select(row => row.Task.Id).ToHashSet().SetEquals(store.LoadTasks().Where(matches).Select(task => task.Id)),
                "global summary action returns matching tasks from every platform: " + buttonName);
        }
        platform.SelectedItem = "雨课堂"; search.Text = "NO-MATCH-SYNTHETIC";
        dashboard.ShowDashboard();
        await WaitAsync(() => table.Items.Count == 7);
        Check(platform.SelectedIndex == 0 && state.SelectedIndex == 1 && search.Text.Length == 0,
            "returning to the home screen restores the unified task list");

        var target = store.LoadTasks().First(task => task.PlatformId == "changjiang" && TaskRules.IsActive(task));
        store.SetState(target.Id, target.Revision, TaskState.Ignored, DateTimeOffset.UtcNow);
        await WaitAsync(() => Rows().Any(row => row.Task.Id == target.Id && row.Task.State == TaskState.Ignored));
        Check(Rows().Length == 7 && Rows().Any(row => row.Task.PlatformId == "chaoxing")
            && ((TextBlock)dashboard.FindName("ActiveCountText")).Text == "3",
            "one platform state update refreshes the shared list and global counters without losing other tasks");
        var unchanged = store.LoadTasks();
        var beforeRequests = sessions.Values.Sum(session => session.Requests);
        foreach (var platformId in new[] { "chaoxing", "yuketang", "changjiang", "mooc" })
        {
            var row = Rows().First(item => item.Task.PlatformId == platformId);
            table.SelectedItem = row; table.ScrollIntoView(row); table.UpdateLayout();
            var container = (DataGridRow)table.ItemContainerGenerator.ContainerFromItem(row);
            var title = FindVisual<Button>(container)!;
            var openedBefore = opened.Count;
            title.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, title));
            await WaitAsync(() => opened.Count > openedBefore);
            Check(opened.Last() == row.Task.Link && row.Platform == PlatformCatalog.Name(platformId),
                "mixed-list task click preserves its own source and exact task link: " + platformId);
        }
        Check(store.LoadTasks().SequenceEqual(unchanged) && sessions.Values.Sum(session => session.Requests) == beforeRequests,
            "unified-list navigation leaves stored metadata untouched and does not request answers");

        var expander = (Expander)dashboard.FindName("PlatformSyncExpander");
        expander.IsExpanded = false;
        await Task.Delay(100); dashboard.UpdateLayout();
        CaptureDashboard("unified-synthetic-dashboard.png");
        var width = dashboard.Width; var height = dashboard.Height;
        dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight;
        await Task.Delay(100); dashboard.UpdateLayout();
        tableHeights["minimumCollapsedUnified"] = table.ActualHeight;
        CaptureDashboard("unified-synthetic-dashboard-minimum.png");
        Check(table.ActualHeight >= 230, "collapsed platform details leave at least four unified task rows at minimum size");
        expander.IsExpanded = true;
        await Task.Delay(100); dashboard.UpdateLayout();
        tableHeights["minimumExpandedUnified"] = table.ActualHeight;
        Check(table.ActualHeight >= 100, "expanded platform details keep unified task rows visible at minimum size");
        var taskDetails = (Expander)dashboard.FindName("SelectedDetailsExpander");
        taskDetails.IsExpanded = true;
        await Task.Delay(100); dashboard.UpdateLayout();
        tableHeights["minimumBothExpandedUnified"] = table.ActualHeight;
        CaptureDashboard("unified-synthetic-dashboard-expanded-minimum.png");
        Check(table.ActualHeight >= 100, "opening task and platform details together keeps a full task row visible at minimum size");
        taskDetails.IsExpanded = false;
        expander.IsExpanded = false;
        dashboard.Width = width; dashboard.Height = height;
    }

    private static void CaptureDashboard(string name)
    {
        dashboard.UpdateLayout();
        var content = (FrameworkElement)dashboard.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle(dashboard.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(content), null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(runRoot, name)); encoder.Save(output);
    }

    private static WebView2 Browser(string platformId) => (WebView2)dashboard.FindName(platformId == "yuketang" ? "RainSessionBrowser" : "ChangjiangSessionBrowser");

    private static void BlockNetwork(WebView2 browser)
    {
        browser.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) return;
            var core = browser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) => request.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html><title>Offline official login fixture</title>")),
                200, "Offline Fixture", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
        };
    }

    private static async Task<RainSyncSummary> RunAsync(RainSyncService service, Action start, Func<RainSyncSummary, bool>? expected = null)
    {
        var completion = new TaskCompletionSource<RainSyncSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(RainSyncSummary result) { if (expected is null || expected(result)) completion.TrySetResult(result); }
        service.Completed += Finished;
        try { start(); var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(35)); await Task.Delay(100); return result; }
        finally { service.Completed -= Finished; }
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!ready()) { if (DateTimeOffset.UtcNow > expires) throw new TimeoutException(); await Task.Delay(25); }
    }
    private static void Check(bool condition, string label)
    {
        lastCheck = label; if (!condition) throw new InvalidOperationException(label); checks++;
    }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private static T? FindLogical<T>(DependencyObject parent, Func<T, bool>? matches = null) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T found && (matches is null || matches(found))) return found;
            if (FindLogical(child, matches) is { } nested) return nested;
        }
        return null;
    }
    private static string CollectText(DependencyObject parent) => (parent is TextBlock text ? text.Text : "")
        + string.Join("\n", LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>().Select(CollectText));
    private static void WriteReport() => File.WriteAllText(Path.Combine(runRoot, "rain-desktop-checks.json"),
        JsonSerializer.Serialize(new { outcome = passed ? "passed" : "failed", checks, lastCheck, failure,
            requests = sessions.ToDictionary(pair => pair.Key, pair => pair.Value.Requests), tableHeights, layoutHeights, data = "synthetic_only",
            network = "local_handler_and_webview_fixture_only", externalBrowser = "captured_without_launch" }, new JsonSerializerOptions { WriteIndented = true }));

    private sealed class FixtureSession(RainPlatform platform)
    {
        public RainPlatform Platform { get; } = platform;
        public bool LoggedIn { get; set; }
        public bool Partial { get; set; }
        public int Requests { get; set; }
        public TaskCompletionSource<bool>? Entered { get; set; }
        public TaskCompletionSource<bool>? Release { get; set; }
    }
    private sealed class FixtureHandler(FixtureSession session) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); session.Requests++;
            var uri = request.RequestUri!;
            if (request.Method != HttpMethod.Get || !RainAddressPolicy.IsReadAddress(session.Platform, uri.Host, uri))
                throw new InvalidOperationException("Fixture rejected unexpected request.");
            var expired = !session.LoggedIn;
            if (uri.AbsolutePath == "/v2/api/web/userinfo" && session.Release is { Task.IsCompleted: false } pending)
            { session.Entered!.TrySetResult(true); await pending.Task.WaitAsync(token); }
            if (expired) return new(HttpStatusCode.Unauthorized);
            var deadline = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds();
            var json = uri.AbsolutePath switch
            {
                "/v2/api/web/userinfo" => "{\"errcode\":0,\"data\":[{\"user_id\":77}]}",
                "/v2/api/web/courses/list" => "{\"errcode\":0,\"data\":{\"list\":[{\"id\":101,\"name\":\"合成班级\",\"course_name\":\"合成课程\"}]}}",
                "/v2/api/web/classrooms/101" => "{\"errcode\":0,\"data\":{\"id\":101,\"name\":\"合成班级\",\"course_name\":\"合成课程\",\"course_sign\":\"FIXTURE-SIGN\"}}",
                "/mooc-api/v1/lms/learn/course/schedule" => "{\"success\":true,\"data\":{\"leaf_schedules\":{\"501\":{\"done\":2,\"total\":4}}}}",
                "/v2/api/web/logs/learn/101" => "{\"errcode\":0,\"data\":{\"has_more\":false,\"activities\":[{\"type\":4,\"courseware_id\":601,\"title\":\"合成旧版测验\",\"deadline\":" + deadline + ",\"status\":2}]}}",
                "/mooc-api/v1/lms/learn/course/chapter" => session.Partial ? "{\"success\":true,\"data\":{}}"
                    : "{\"success\":true,\"data\":{\"course_chapter\":[{\"id\":201,\"name\":\"第一章\",\"section_leaf_list\":[{\"id\":501,\"name\":\"合成作业\",\"leaf_type\":6,\"chapter_id\":201,\"score_deadline\":" + deadline + "}]}]}}",
                _ => throw new InvalidOperationException("Unexpected fixture metadata path.")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
