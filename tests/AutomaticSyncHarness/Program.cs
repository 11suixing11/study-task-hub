using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AutomaticSyncHarness;

// Separate executable, private data/profile, and local HttpMessageHandler fixtures only.
internal static class Program
{
    private static int checks;
    private static int requestCount;
    private static bool loggedOut;
    private static TaskDashboardWindow dashboard = null!;
    private static Application app = null!;
    private static ChaoxingSyncService service = null!;
    private static TaskStore store = null!;
    private static string runRoot = "";
    private static bool verifyOnly;
    private static string? failure;
    private static bool verified;
    private static readonly List<string> openedTaskLinks = [];
    private static string taskRoute = "view";
    private static int taskNavigationChecks;
    private static int sessionRestartChecks;
    private static int sessionRaceChecks;
    private static TaskCompletionSource<bool>? portalEntered;
    private static TaskCompletionSource<bool>? releasePortal;
    private static bool staleRenewal;
    private static bool renewEachPortal;
    private const string SessionCookieName = "SYNTHETIC_AUTH";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--session-child") return SessionRestartChecks.RunChild(args[1], args[2], args[3]);
        if (args.Length is not (2 or 3) || args[0] != "--project-root" || args.Length == 3 && args[2] != "--verify") return 2;
        var root = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        verifyOnly = args.Length == 3;
        var harnessRoot = Path.Combine(root, ".local", "automatic-sync-harness");
        Directory.CreateDirectory(harnessRoot);
        runRoot = Path.Combine(harnessRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(Path.Combine(harnessRoot, "latest-run.txt"), runRoot);
        Environment.SetEnvironmentVariable("TEMP", runRoot); Environment.SetEnvironmentVariable("TMP", runRoot);
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        store = new TaskStore(Path.Combine(runRoot, "tasks.db"));
        store.SaveSettings(new(false, true));
        dashboard = new TaskDashboardWindow(store, runRoot, () => { loggedOut = !loggedOut; dashboard.ShowSuccess("合成会话切换完成，可点击立即同步。"); }, () => app.Shutdown(), openedTaskLinks.Add)
        { Title = "学习任务聚合 · 自动同步离线验收" };
        var browser = (WebView2)dashboard.FindName("SessionBrowser");
        browser.CoreWebView2InitializationCompleted += (_, result) =>
        {
            if (!result.IsSuccess) return;
            var core = browser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) => request.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(), 403, "Offline Only", "Cache-Control: no-store\r\n");
        };
        service = new ChaoxingSyncService(store, runRoot, browser, () => new FixtureHandler(), TimeSpan.FromMilliseconds(75));
        dashboard.AttachSync(service);
        dashboard.AutomaticSyncSettingChanged += service.SettingsChanged;
        dashboard.Loaded += (_, _) =>
        {
            dashboard.ShowSuccess("独立合成数据库 · 所有同步请求在本地响应。");
            dashboard.Dispatcher.BeginInvoke(new Action(async () => await VerifyAsync(browser)));
        };
        dashboard.Closed += (_, _) => { service.Dispose(); app.Shutdown(); };
        app.MainWindow = dashboard;
        app.Run(dashboard);
        service.Dispose();
        return failure is null && verified ? 0 : 1;
    }

    private static async Task VerifyAsync(WebView2 browser)
    {
        try
        {
            loggedOut = true;
            var initial = await RunAsync(() => service.StartAutomatic());
            Check(browser.CoreWebView2 is not null && browser.Visibility == Visibility.Hidden);
            Check(app.Windows.Count == 1);
            Check(initial.LoginRequired && store.LoadTasks().Count == 0);
            Check(store.LoadSettings().LastSyncOutcome == "login_required");
            // Completing official login is enough: no SyncNow, course click or import button.
            loggedOut = false;
            var firstLogin = await RunAsync(() =>
            {
                service.LoginConnectionOpened();
                service.LoginNavigationCompleted(ChaoxingSyncParser.Portal);
            });
            Check(firstLogin.Courses == 2 && firstLogin.CoursesRead == 2 && firstLogin.Added == 2 && firstLogin.Problems.Count == 0);
            Check(firstLogin.DetailsSaved == 2); // Dates survive the observed task -> view redirect without replacing task links.
            Check(store.LoadTasks().Count == 2 && store.LoadTasks().All(task => task.DetailTimeText.Length > 0));
            Check(store.LoadTasks().All(task => task.State == TaskState.Completed && task.DeadlineUtc is not null
                && task.DeadlineEvidence == DeadlineEvidence.InferredYear));
            Check(store.LoadSettings().LastSyncSummary?.CoursesRead == 2);
            Check(store.LoadSettings().LastSyncOutcome == "completed");
            await VerifyTaskClicksAsync();
            await service.StartAsync(true);
            Check(store.LoadTasks().Count == 2 && store.LoadSettings().LastSyncSummary?.Added == 0);
            Check(store.LoadSettings().LastSyncSummary?.Updated == 2);
            var settings = store.LoadSettings() with { AutomaticSyncEnabled = false };
            store.SaveSettings(settings); service.SettingsChanged(false);
            Check(((System.Windows.Controls.TextBlock)dashboard.FindName("SyncStatusText")).Text.Contains("关闭自动记录", StringComparison.Ordinal));
            var before = requestCount;
            service.StartAutomatic();
            service.LoginConnectionOpened();
            service.LoginNavigationCompleted(ChaoxingSyncParser.Portal);
            await Task.Delay(200);
            Check(requestCount == before);
            loggedOut = true;
            await service.StartAsync(true);
            Check(store.LoadSettings().LastSyncOutcome == "login_required");
            Check(store.LoadTasks().Count == 2);
            Check(((System.Windows.Controls.TextBlock)dashboard.FindName("SyncStatusText")).Text.Contains("SYNC001", StringComparison.Ordinal));
            loggedOut = false;
            store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = true });
            var enabled = await RunAsync(() => service.SettingsChanged(true));
            Check(enabled.Updated == 2 && !enabled.LoginRequired);
            Check(store.LoadSettings().AutomaticSyncEnabled && store.LoadSettings().LastSyncOutcome == "completed");
            Check(((System.Windows.Controls.Button)dashboard.FindName("SyncButton")).IsEnabled);

            // Login finishes while an older HTTP request is still returning the expired session.
            loggedOut = true;
            portalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releasePortal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleRun = service.StartAsync(true);
            await portalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(service.IsRunning);
            loggedOut = false;
            var busyRecovery = await RunAsync(() =>
            {
                service.LoginConnectionOpened();
                service.LoginNavigationCompleted(ChaoxingSyncParser.Portal);
                releasePortal.TrySetResult(true);
            }, result => !result.LoginRequired);
            await staleRun;
            Check(busyRecovery.Updated == 2 && busyRecovery.Added == 0);
            Check(store.LoadSettings().LastSyncOutcome == "completed" && store.LoadTasks().Count == 2);
            portalEntered = releasePortal = null;

            // Some login pages set the session cookie without a top-level navigation.
            loggedOut = true;
            await service.StartAsync(true);
            var withoutNavigation = await RunAsync(() => { service.LoginConnectionOpened(); loggedOut = false; });
            Check(!withoutNavigation.LoginRequired && withoutNavigation.Updated == 2);

            service.SessionClearing();
            before = requestCount;
            await service.StartAsync(true);
            Check(requestCount == before && store.LoadTasks().Count == 2);
            loggedOut = true;
            var clearRecovery = await RunAsync(() =>
            {
                service.SessionClearCompleted(true);
                loggedOut = false;
                service.LoginNavigationCompleted(ChaoxingSyncParser.Portal);
            });
            Check(!clearRecovery.LoginRequired && clearRecovery.Updated == 2);
            service.LoginConnectionClosed();
            await VerifySessionRacesAsync(browser);
            sessionRestartChecks = await SessionRestartChecks.VerifyAsync(runRoot);
            verified = true;
            WriteReport("passed");
            dashboard.ShowSuccess($"{checks} 项自动同步桌面检查、{taskNavigationChecks} 项任务直达检查通过：登录后自动采集、任务名称与按钮点击、做题 / 查看 / 提交记录、关闭开关、并发登录与恢复。");
            if (verifyOnly) app.Shutdown();
        }
        catch (Exception error)
        {
            failure = error.GetType().Name;
            WriteReport("failed");
            dashboard.ShowError("SYNC_TEST", $"离线桌面验收失败：第 {checks} 项（{failure}）。");
            if (verifyOnly) app.Shutdown(1);
        }
    }

    private static async Task VerifySessionRacesAsync(WebView2 browser)
    {
        void RaceCheck(bool value, string name)
        { if (!value) throw new InvalidOperationException("Session race check failed: " + name); sessionRaceChecks++; }
        async Task<string?> CookieValueAsync()
        {
            var observed = await browser.CoreWebView2.CookieManager.GetCookiesAsync("");
            var value = observed.SingleOrDefault(cookie => cookie.Name == SessionCookieName)?.Value;
            observed.Clear(); return value;
        }
        void SetCookie(string value)
        {
            var manager = browser.CoreWebView2.CookieManager;
            var cookie = manager.CreateCookie(SessionCookieName, value, ".chaoxing.com", "/");
            cookie.IsSecure = cookie.IsHttpOnly = true; cookie.Expires = DateTime.MinValue;
            manager.AddOrUpdateCookie(cookie);
        }
        var archivePath = Path.Combine(runRoot, ".local", "sessions", "chaoxing.bin");
        var archive = new ChaoxingSessionStore(archivePath);
        SetCookie("synthetic-before-renewal");
        renewEachPortal = true;
        await service.StartAsync(true);
        RaceCheck(await CookieValueAsync() == "synthetic-next-renewal", "HTTP renewal reaches WebView");
        RaceCheck(archive.Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Name == SessionCookieName && cookie.Value == "synthetic-next-renewal"),
            "renewal saved before exit");
        renewEachPortal = false;
        await service.StartAsync(true);
        RaceCheck(store.LoadSettings().LastSyncOutcome == "completed" && await CookieValueAsync() == "synthetic-next-renewal", "next sync reader keeps renewal");
        var completions = 0;
        void Completed(ChaoxingSyncSummary _) => completions++;
        service.Completed += Completed;
        try
        {
            // A successful new login changes cookies without a top-level navigation while an
            // older request still carries a response that would otherwise replace the login.
            portalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releasePortal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            staleRenewal = true;
            var old = service.StartAsync(true);
            await portalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            SetCookie("synthetic-new-login");
            RaceCheck(await CookieValueAsync() == "synthetic-new-login", "new login installed during request");
            releasePortal.TrySetResult(true);
            await old;
            RaceCheck(completions == 0 && await CookieValueAsync() == "synthetic-new-login", "stale response cannot overwrite new login or record expired result");
            portalEntered = releasePortal = null;
            staleRenewal = false;
            var recovered = await RunAsync(() => { }, result => !result.LoginRequired);
            RaceCheck(recovered.Updated == 2 && archive.Load(DateTimeOffset.UtcNow).Single(cookie => cookie.Name == SessionCookieName).Value == "synthetic-new-login",
                "automatic queued check saves latest login");

            var unchanged = store.LoadTasks();
            portalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releasePortal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            staleRenewal = true;
            var clearingRun = service.StartAsync(true);
            await portalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var completedBeforeClear = completions;
            service.SessionClearing();
            await service.PrepareSessionClearAsync();
            browser.CoreWebView2.CookieManager.DeleteAllCookies();
            await browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
            releasePortal.TrySetResult(true);
            await clearingRun;
            RaceCheck(await CookieValueAsync() is null && !File.Exists(archivePath), "clear during pending request removes profile and archive");
            RaceCheck(completions == completedBeforeClear && store.LoadTasks().SequenceEqual(unchanged), "cancelled clear preserves tasks and suppresses old result");
            portalEntered = releasePortal = null; staleRenewal = false;
            service.SessionClearCompleted(true);
            SetCookie("synthetic-after-clear");
            await service.StartAsync(true);
            RaceCheck(archive.Load(DateTimeOffset.UtcNow).Single(cookie => cookie.Name == SessionCookieName).Value == "synthetic-after-clear",
                "new login after clear can be archived");

            // Exit must first cancel and settle HTTP work, then save the current profile.
            portalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releasePortal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            staleRenewal = true;
            var exitRun = service.StartAsync(true);
            await portalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var completedBeforeExit = completions;
            RaceCheck(await service.SaveForExitAsync(), "graceful exit finishes pending run and saves session");
            releasePortal.TrySetResult(true); await exitRun;
            RaceCheck(!service.IsRunning && completions == completedBeforeExit && await CookieValueAsync() == "synthetic-after-clear",
                "late response cannot write after exit begins");
            RaceCheck(archive.Load(DateTimeOffset.UtcNow).Single(cookie => cookie.Name == SessionCookieName).Value == "synthetic-after-clear",
                "exit archive uses current login rather than pending renewal");
            var before = requestCount;
            await service.StartAsync(true); service.StartAutomatic();
            RaceCheck(requestCount == before, "exit prevents further requests");
        }
        finally
        {
            service.Completed -= Completed;
            portalEntered = releasePortal = null; staleRenewal = renewEachPortal = false;
        }
    }

    private static async Task VerifyTaskClicksAsync()
    {
        void NavigationCheck(bool value) { if (!value) throw new InvalidOperationException("Task-click desktop check failed: " + (taskNavigationChecks + 1)); taskNavigationChecks++; }
        var table = (System.Windows.Controls.DataGrid)dashboard.FindName("TaskTable");
        ((System.Windows.Controls.ComboBox)dashboard.FindName("StateFilter")).SelectedIndex = 1;
        await WaitUntilAsync(() => table.Items.Count == 2);
        var tasksBefore = store.LoadTasks();
        var toolbar = (System.Windows.Controls.Button)dashboard.FindName("GoButton");
        NavigationCheck(toolbar.IsEnabled && toolbar.Content.ToString()!.Contains("查看", StringComparison.Ordinal));
        var before = requestCount;
        await ClickAndWaitAsync(toolbar);
        NavigationCheck(openedTaskLinks.Count == 1 && openedTaskLinks[0].Contains("/work/view?", StringComparison.Ordinal));
        NavigationCheck(requestCount == before + 1 && app.Windows.Count == 1);
        var clicked = (TaskDashboardWindow.TaskDisplayRow)table.Items[1];
        table.SelectedIndex = 0;
        table.ScrollIntoView(clicked); table.UpdateLayout();
        var container = (System.Windows.Controls.DataGridRow)table.ItemContainerGenerator.ContainerFromItem(clicked);
        var title = FindVisual<System.Windows.Controls.Button>(container)
            ?? throw new InvalidOperationException("Task title link was not rendered.");
        taskRoute = "dowork";
        before = requestCount;
        await ClickAndWaitAsync(title);
        NavigationCheck(openedTaskLinks.Count == 2 && openedTaskLinks.Last().Contains("/work/dowork?", StringComparison.Ordinal));
        NavigationCheck(TaskStore.ObservedWorkKey(WorkListParser.ParseTaskLocation(openedTaskLinks.Last()
            .Replace("/work/dowork?", "/work/task?", StringComparison.Ordinal))) == clicked.Task.SourceKey);
        NavigationCheck(requestCount == before + 1 && ReferenceEquals(table.SelectedItem, clicked));
        taskRoute = "prompt";
        await ClickAndWaitAsync(toolbar);
        NavigationCheck(openedTaskLinks.Last().Contains("/work/prompt?", StringComparison.Ordinal));
        loggedOut = true;
        await ClickAndWaitAsync(toolbar);
        NavigationCheck(openedTaskLinks.Last().Contains("/work/task?", StringComparison.Ordinal));
        loggedOut = false; taskRoute = "view";
        NavigationCheck(store.LoadTasks().SequenceEqual(tasksBefore));
        var status = ((System.Windows.Controls.TextBlock)dashboard.FindName("ResultText")).Text;
        NavigationCheck(status.Contains("登录", StringComparison.Ordinal) && toolbar.IsEnabled);
    }

    private static async Task ClickAndWaitAsync(System.Windows.Controls.Button button)
    {
        var previous = openedTaskLinks.Count;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
        await WaitUntilAsync(() => openedTaskLinks.Count > previous);
        await Task.Delay(50);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > expires) throw new TimeoutException("Synthetic UI action did not complete.");
            await Task.Delay(25);
        }
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private static async Task<ChaoxingSyncSummary> RunAsync(Action start, Func<ChaoxingSyncSummary, bool>? expected = null)
    {
        var completed = new TaskCompletionSource<ChaoxingSyncSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(ChaoxingSyncSummary result)
        { if (expected is null || expected(result)) completed.TrySetResult(result); }
        service.Completed += Finished;
        try
        {
            start();
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            // Completion is emitted just before the run's finally; settle the UI dispatcher first.
            await Task.Delay(50);
            return result;
        }
        finally { service.Completed -= Finished; }
    }

    private static void Check(bool condition)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("Synthetic desktop sync check failed.");
    }

    private static void WriteReport(string outcome) => File.WriteAllText(Path.Combine(runRoot, "automatic-sync-checks.json"),
        JsonSerializer.Serialize(new { outcome, checks, taskNavigationChecks, sessionRaceChecks, sessionRestartChecks, requestCount, externalBrowser = "captured_without_launch", data = "synthetic_only", network = "local_handler_only", failure }, new JsonSerializerOptions { WriteIndented = true }));

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            requestCount++;
            if (request.Method != HttpMethod.Get || !ChaoxingSyncParser.IsReadAddress(request.RequestUri!))
                throw new InvalidOperationException("Unexpected fixture request.");
            var uri = request.RequestUri!;
            var expired = loggedOut;
            var staleResponse = staleRenewal && uri.AbsolutePath == "/base" && releasePortal is { Task.IsCompleted: false };
            if (uri.AbsolutePath == "/base" && releasePortal is { Task.IsCompleted: false } pending)
            {
                portalEntered!.TrySetResult(true);
                await pending.Task.WaitAsync(cancellation);
            }
            if (expired) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (staleResponse)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                response.Headers.TryAddWithoutValidation("Set-Cookie", SessionCookieName + "=synthetic-stale; Domain=chaoxing.com; Path=/; Secure; HttpOnly");
                return response;
            }
            string html;
            if (uri.AbsolutePath == "/base") html = "<script>var fid='901';</script>";
            else if (uri.AbsolutePath.Contains("courselistdata", StringComparison.Ordinal)) html = "<div class='course-list'>"
                + "<div><a href='https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid=101&amp;clazzid=201&amp;cpi=301'><span class='course-name' title='合成课程 A'>A</span></a></div>"
                + "<div><a href='https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid=102&amp;clazzid=202&amp;cpi=302'><span class='course-name' title='合成课程 B'>B</span></a></div></div>";
            else if (uri.AbsolutePath == "/visit/stucoursemiddle")
                return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(
                    "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/stu" + uri.Query + "&enc=SYNTHETIC%2BONLY&v=2") } };
            else if (uri.AbsolutePath == "/mooc2-ans/mycourse/stu") html = "<input id='workEnc' value='SYNTHETIC+ENC'><input id='enc' value='SYNTHETIC+PAGE'><input id='openc' value='SYNTHETIC+OPEN'>";
            else if (uri.AbsolutePath == "/mooc2/work/list")
            {
                var first = uri.Query.Contains("courseId=101", StringComparison.Ordinal);
                var target = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?"
                    + (first ? "courseId=101&amp;classId=201&amp;cpi=301&amp;workId=401" : "courseId=102&amp;classId=202&amp;cpi=302&amp;workId=402") + "&amp;enc=SYNTHETIC%2BONLY";
                html = $"<ul id='workList'><li onclick='goTask(this);' data='{target}'><div class='right-content'><p class='overHidden2'>合成作业 {(first ? "A" : "B")}</p><p class='status'>已完成</p></div><div class='time'>剩余 48 小时</div></li></ul>";
            }
            else if (uri.AbsolutePath == "/mooc-ans/mooc2/work/task")
                return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(
                    "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/" + taskRoute + uri.Query) } };
            else if (uri.AbsolutePath == "/mooc-ans/mooc2/work/view") html = "<div class='detailsHead'><h2 class='mark_title'>合成作业</h2><div class='infoHead'><p class='rightBord'>作答时间:<em>10-14 19:04</em>至<em>10-25 23:59</em></p></div></div>";
            else throw new InvalidOperationException("Unexpected fixture route.");
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
            if (uri.AbsolutePath == "/base" && renewEachPortal)
                result.Headers.TryAddWithoutValidation("Set-Cookie", SessionCookieName + "=synthetic-next-renewal; Domain=chaoxing.com; Path=/; Secure; HttpOnly");
            return result;
        }
    }
}
