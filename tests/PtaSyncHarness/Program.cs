using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AccessProbe;
using Microsoft.Web.WebView2.Wpf;
using F = PlatformMetadataTestData.PtaFixtures;

namespace PtaSyncHarness;

internal static class Program
{
    private static Application app = null!;
    private static TaskDashboardWindow dashboard = null!;
    private static TaskStore store = null!;
    private static MetadataSyncService service = null!;
    private static WebView2 browser = null!;
    private static readonly F.Handler fixture = new();
    private static string runRoot = "", last = "startup";
    private static int checks;
    private static string? failure;
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--session-child") return SessionRestartChecks.RunChild(args[1], args[2], args[3]);
        if (args.Length != 3 || args[0] != "--project-root" || args[2] != "--verify") return 2;
        var projectRoot = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(projectRoot, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var harnessRoot = Path.Combine(projectRoot, ".local", "pta-sync-harness");
        runRoot = Path.Combine(harnessRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(runRoot); File.WriteAllText(Path.Combine(harnessRoot, "latest-run.txt"), runRoot);
        Environment.SetEnvironmentVariable("TEMP", runRoot); Environment.SetEnvironmentVariable("TMP", runRoot);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        store = FixtureTaskStore.Create(Path.Combine(runRoot, "tasks.db"), pendingPlatforms: ["pta"]); store.SaveSettings(new(false, true));
        store.Create(new("合成其他平台历史任务", "mooc", "合成其他课程", DeadlineKind.Unknown, null, null, null, null, "", "", null), DateTimeOffset.UtcNow);
        dashboard = new TaskDashboardWindow(store, runRoot, () => { }, () => { }, _ => checks++) { Title = "PTA · 合成隔离回归" };
        browser = (WebView2)dashboard.FindName("PtaSessionBrowser"); BrowserFixtures.BlockNetwork(browser);
        service = new MetadataSyncService(MetadataPlatform.Pta, store, runRoot, browser, () => fixture, TimeSpan.FromMilliseconds(50));
        service.StatusChanged += (message, _, _) => File.AppendAllText(Path.Combine(runRoot, "pta-status.log"), message + Environment.NewLine);
        dashboard.AttachMetadataSync(service); store.SaveMetadataSettings("pta", new() { Connected = true });
        app.MainWindow = dashboard; dashboard.Loaded += async (_, _) => await VerifyAsync(); app.Run(dashboard);
        return failure is null ? 0 : 1;
    }
    private static void Check(bool condition, string label)
    { last = label; if (!condition) throw new InvalidOperationException(label); checks++; }
    private static async Task VerifyAsync()
    {
        try
        {
            await service.EnsureSessionAsync(); BrowserFixtures.SetCookie(browser, fixture.Session); await Task.Delay(80);
            fixture.RenewNext = true; await service.StartAsync(true);
            var recording = store.LoadCourseRecording("pta");
            Check(recording is { SelectionConfirmed: false, CatalogComplete: true } && recording.Courses.Count == 2, "full teaching course catalog before selection");
            Check(store.LoadMetadataSettings("pta").LastSummary is null && store.LoadTasks().Count == 1, "catalog-only login preserves task summary and other platform history");
            Check(store.LoadPlatformSessionInfo("pta").State == "ready" && service.LoginDiagnostics.Snapshot().AccountConfirmed, "official profile confirms connected session");
            Check((await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Single(cookie => cookie.Name == "PTASession").Value == fixture.Session,
                "HTTP renewal updates reusable WebView session");
            Check(fixture.Paths.All(path => !path.EndsWith("/exams", StringComparison.Ordinal) && !path.Contains("/user-groups/20001", StringComparison.Ordinal)),
                "no task details before course choice");
            foreach (var popup in app.Windows.OfType<CourseSelectionWindow>().ToArray()) popup.Close();
            var key = CourseRecordingRules.MetadataKey("pta", F.Account, F.Course.Id, "class");
            store.SaveCourseRecording("pta", false, [key], true, recording.Revision, DateTimeOffset.UtcNow); fixture.Paths.Clear();
            await service.RecordingCoursesChangedAsync(); await WaitAsync(() => ((DataGrid)dashboard.FindName("TaskTable")).Items.Count == 6);
            var summary = store.LoadMetadataSettings("pta").LastSummary!;
            Check(summary is { Tasks: 5, Added: 5, SelectedCourses: 1, StatusNeedsReview: 1, AccountVerified: true }, "selected class updates unified dashboard");
            Check(fixture.Paths.All(path => !path.Contains("20002", StringComparison.Ordinal) && !path.Contains("40001", StringComparison.Ordinal)), "unselected course task routes not requested");
            Check(((TextBlock)dashboard.FindName("AggregationSummaryText")).Text.Contains("PTA 5 条", StringComparison.Ordinal), "PTA source total visible");
            Check(store.LoadTasks().Single(task => task.PlatformTitle == "合成一直可用题目集").DeadlineKind == DeadlineKind.None, "explicit no deadline shown without guess");
            Check(store.LoadTasks().Single(task => task.PlatformTitle == "合成已超时答卷题目集").State == TaskState.NeedsReview, "timed-out exam requires review in dashboard");
            await VerifyPopupAsync(); await VerifyTaskActionsAsync(); await VerifyLoginWindowAsync();
            var task = store.LoadTasks().Single(task => task.PlatformTitle == "合成尚未开始题目集");
            var edited = store.SetState(task.Id, task.Revision, TaskState.Completed, DateTimeOffset.UtcNow);
            await service.StartAsync(true); Check(store.Get(edited.Id)!.State == TaskState.Completed, "manual state survives periodic PTA resync");
            var trackingChanged = false;
            fixture.BeforeReply = async (request, _) =>
            {
                if (!trackingChanged && request.RequestUri!.AbsolutePath.EndsWith("/exams", StringComparison.Ordinal))
                {
                    trackingChanged = true;
                    var analytics = browser.CoreWebView2.CookieManager.CreateCookie("_ga_ZHCNP8KECW", "synthetic-statistics-updated", ".pintia.cn", "/");
                    browser.CoreWebView2.CookieManager.AddOrUpdateCookie(analytics); await Task.Delay(100);
                }
            };
            var beforeRequests = fixture.Paths.Count; await service.StartAsync(true); fixture.BeforeReply = null;
            Check(trackingChanged && store.LoadMetadataSettings("pta").LastSummary is { AccountVerified: true, Tasks: 5 }
                && fixture.Paths.Skip(beforeRequests).Count(path => path.EndsWith("/exams", StringComparison.Ordinal)) == 5,
                "statistics-cookie change during selected PTA reads cannot cause PTA003");
            var serverSessionChanged = false; var browserCookiePreferred = false; fixture.RenewServerSession = true;
            fixture.BeforeReply = async (request, _) =>
            {
                if (!serverSessionChanged && request.RequestUri!.AbsolutePath.EndsWith("/exams", StringComparison.Ordinal))
                {
                    serverSessionChanged = true;
                    var renewed = browser.CoreWebView2.CookieManager.CreateCookie("JSESSIONID", "synthetic-official-page-renewal", "pintia.cn", "/");
                    renewed.IsHttpOnly = true; browser.CoreWebView2.CookieManager.AddOrUpdateCookie(renewed); await Task.Delay(100);
                }
                else if (serverSessionChanged && request.Headers.TryGetValues("Cookie", out var values)
                    && values.Any(value => value.Contains("JSESSIONID=synthetic-official-page-renewal", StringComparison.Ordinal))) browserCookiePreferred = true;
            };
            beforeRequests = fixture.Paths.Count; await service.StartAsync(true); fixture.BeforeReply = null; fixture.RenewServerSession = false;
            Check(serverSessionChanged && store.LoadMetadataSettings("pta").LastSummary is { AccountVerified: true, Tasks: 5 }
                && fixture.Paths.Skip(beforeRequests).Count(path => path.EndsWith("/exams", StringComparison.Ordinal)) == 5,
                "API and official-page JSESSIONID renewals do not interrupt selected PTA collection");
            Check(browserCookiePreferred, "pending HTTP response cannot overwrite a newer official-page server cookie");
            var currentServerCookie = (await browser.CoreWebView2.CookieManager.GetCookiesAsync("https://pintia.cn/"))
                .Single(cookie => cookie.Name == "JSESSIONID");
            Check(new MetadataSessionStore(MetadataPlatform.Pta, Path.Combine(runRoot, "server-session.bin"), store)
                .Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Name == "JSESSIONID" && cookie.Value == currentServerCookie.Value && cookie.HttpOnly),
                "latest server session still saved with its flags in encrypted repository");
            var catalogBefore = store.LoadCourseRecording("pta"); fixture.RejectLogin = true;
            await service.StartAsync(true);
            Check(store.LoadPlatformSessionInfo("pta").State == "expired" && store.LoadMetadataSettings("pta").LastSummary!.LoginRequired, "expired official profile invalidates saved cookie");
            Check(store.LoadTasks().Count == 6 && store.LoadCourseRecording("pta").SelectedCourseKeys.SequenceEqual(catalogBefore.SelectedCourseKeys), "expiry preserves selection and history");
            Check(((TextBlock)dashboard.FindName("PtaPlatformSummary")).Text.Contains("登录过期", StringComparison.Ordinal), "PTA expiry prompts relogin on platform card");
            fixture.RejectLogin = false; fixture.Session = "synthetic-pta-relogin"; BrowserFixtures.SetCookie(browser, fixture.Session); await Task.Delay(80);
            service.ConnectionOpened(); await service.StartAsync(true, afterLogin: true); service.ConnectionClosed();
            Check(store.LoadPlatformSessionInfo("pta").State == "ready" && new MetadataSessionStore(MetadataPlatform.Pta, Path.Combine(runRoot, "pta.bin"), store)
                .Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Value == fixture.Session), "relogin replaces expired current cookie");
            foreach (var popup in app.Windows.OfType<CourseSelectionWindow>().ToArray()) popup.Close();
            var oldCount = store.LoadTasks().Count; var changed = false;
            fixture.BeforeReply = async (request, _) =>
            {
                if (!changed && request.RequestUri!.AbsolutePath.EndsWith("/exams", StringComparison.Ordinal))
                {
                    changed = true; BrowserFixtures.SetCookie(browser, "synthetic-pta-external-new-login");
                    fixture.Session = "synthetic-pta-external-new-login";
                    await Task.Delay(100);
                }
            };
            await service.StartAsync(true);
            Check(changed && store.LoadTasks().Count == oldCount, "cookie changed during sync cancels old batch"); fixture.BeforeReply = null;
            Check(service.LoginDiagnostics.Snapshot() is { SessionChangeReason: "profile_changed" }
                && service.LoginDiagnostics.Snapshot().Events.Any(item => item.Result == MetadataLoginResult.SessionChanged),
                "actual account switch records safe reason for PTA003");
            service.Cancel(); await WaitAsync(() => !service.IsRunning); await Task.Delay(100); await service.StartAsync(true);
            Check(store.LoadMetadataSettings("pta").LastSummary!.AccountVerified, "fresh sync can recover after session change");
            var selectionChanged = false;
            fixture.Override = request =>
            {
                if (!selectionChanged && request.RequestUri!.AbsolutePath.EndsWith("/exams", StringComparison.Ordinal))
                {
                    selectionChanged = true; var current = store.LoadCourseRecording("pta");
                    store.SaveCourseRecording("pta", false, [], true, current.Revision, DateTimeOffset.UtcNow);
                }
                return null;
            };
            await service.StartAsync(true);
            Check(selectionChanged && store.LoadTasks().Count == oldCount, "selection revision cancels old per-course batch"); fixture.Override = null;
            var empty = store.LoadCourseRecording("pta"); store.SaveCourseRecording("pta", false, [key], true, empty.Revision, DateTimeOffset.UtcNow);
            await service.StartAsync(true);
            fixture.Hidden = true;
            await service.RefreshCourseCatalogAsync();
            var hiddenSelection = store.LoadCourseRecording("pta");
            store.SaveCourseRecording("pta", false, [CourseRecordingRules.MetadataKey("pta", F.Account, F.HiddenCourse.Id, "class")], true,
                hiddenSelection.Revision, DateTimeOffset.UtcNow);
            await service.RecordingCoursesChangedAsync();
            Check(store.LoadMetadataSettings("pta") is { LastSyncOutcome: "completed", LastSummary: { Tasks: 1, TaskListCoursesRead: 1,
                TeachingDirectoryFallbackCourses: 1, CoursesRead: 1, FullTaskListsCovered: true } }, "hidden-class fallback completes without stale permission warning");
            var details = new StackPanel();
            typeof(TaskDashboardWindow).GetMethod("AddMetadataSyncDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dashboard, [details]);
            var detailsText = string.Join("\n", details.Children.OfType<TextBlock>().Select(block => block.Text));
            Check(detailsText.Contains("已读取题目集列表 1/1 门", StringComparison.Ordinal) && detailsText.Contains("通过官方教学目录读取", StringComparison.Ordinal)
                && !detailsText.Contains("PTA006", StringComparison.Ordinal), "desktop details report recovered list without unresolved permission error");
            Check(((TextBlock)dashboard.FindName("PtaSyncStatusText")).Text.Contains("已读取 1/1", StringComparison.Ordinal), "PTA banner shows actual read list count");
            fixture.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/50001/exams", StringComparison.Ordinal)
                ? F.Handler.Reply(request, HttpStatusCode.OK, F.Exam(F.Set("50001"), F.Account, "END", "END", false)) : null;
            await service.StartAsync(true);
            Check(store.LoadMetadataSettings("pta") is { LastSyncOutcome: "partial", LastSummary: { TaskListCoursesRead: 1, CoursesRead: 0,
                StatusNeedsReview: 1, TeachingDirectoryFallbackCourses: 1 } }, "recovered directory still reports uncertain personal completion");
            var reviewStatus = ((TextBlock)dashboard.FindName("PtaSyncStatusText")).Text;
            Check(reviewStatus.Contains("已读取 1/1", StringComparison.Ordinal) && reviewStatus.Contains("完整核对答卷 0 门", StringComparison.Ordinal),
                "PTA banner distinguishes list coverage from answer review");
            fixture.Override = null; fixture.Hidden = false;
            var restoreSelection = store.LoadCourseRecording("pta");
            store.SaveCourseRecording("pta", false, [key], true, restoreSelection.Revision, DateTimeOffset.UtcNow);
            await service.RecordingCoursesChangedAsync();
            Check(await service.SaveForExitAsync(), "exit saves current protected session");
            await LayoutAsync();
            Check(await SessionRestartChecks.VerifyAsync(runRoot) > 0, "fresh-process cookie recovery and expiry verified");
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message + " (after " + last + ")"; Capture(dashboard, "pta-failure.png"); }
        finally
        {
            foreach (var window in app.Windows.OfType<Window>().Where(window => window != dashboard).ToArray()) window.Close();
            File.WriteAllText(Path.Combine(runRoot, "pta-desktop-checks.json"), JsonSerializer.Serialize(new
            { outcome = failure is null ? "passed" : "failed", checks, requests = fixture.Paths.Count, data = "synthetic_only",
                network = "local_handler_and_webview_fixture_only", externalBrowser = "captured_without_launch", failure }, new JsonSerializerOptions { WriteIndented = true }));
            service.Dispose(); dashboard.CloseForExit(); app.Shutdown(failure is null ? 0 : 1);
        }
    }
    private static async Task VerifyPopupAsync()
    {
        typeof(TaskDashboardWindow).GetMethod("ShowPlatformManager", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dashboard, ["pta"]);
        await Task.Delay(100); var popup = app.Windows.OfType<CourseSelectionWindow>().Single();
        Check(popup.Title == "PTA · 平台管理" && ((ComboBox)popup.FindName("RecordingPlatformSelector")).Visibility == Visibility.Collapsed, "single platform management popup");
        Check(((Expander)popup.FindName("PlatformCoursesExpander")).IsExpanded, "course area starts expanded");
        ((Button)popup.FindName("PlatformCollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
        Check(!((Expander)popup.FindName("PlatformCoursesExpander")).IsExpanded && popup.Height < 650, "course area collapses to compact popup"); Capture(popup, "pta-popup-collapsed.png");
        ((Button)popup.FindName("PlatformCollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
        Check(((Expander)popup.FindName("PlatformCoursesExpander")).IsExpanded, "course area expands again"); Capture(popup, "pta-popup-expanded.png");
        Check(!Visuals<TextBlock>(popup).Any(text => text.Text.Contains("???", StringComparison.Ordinal) || text.Text.Contains('\uFFFD')), "PTA popup Chinese contains no replacement characters");
        popup.Close();
    }
    private static async Task VerifyLoginWindowAsync()
    {
        var window = new MetadataConnectionWindow(service, () => { }) { Owner = dashboard };
        var loginBrowser = typeof(MetadataConnectionWindow).GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) as WebView2;
        var blockedBefore = service.LoginDiagnostics.Snapshot().BlockedNavigations;
        BrowserFixtures.BlockNetwork(loginBrowser!, includeCaptcha: true); window.Show();
        await WaitAsync(() => loginBrowser!.CoreWebView2 is not null);
        var captchaReady = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await loginBrowser!.CoreWebView2.ExecuteScriptAsync("window.syntheticPtaCaptchaReady === true") == "true")
            { captchaReady = true; break; }
            await Task.Delay(30);
        }
        Check(captchaReady && service.LoginDiagnostics.Snapshot().BlockedNavigations == blockedBefore,
            "official verifier iframe completes in actual login browser using local responses");
        Check(window.Title.Contains("PTA", StringComparison.Ordinal) && Visuals<TextBlock>(window).Any(text => text.Text.Contains("下方 PTA 官网", StringComparison.Ordinal)), "PTA-specific login instruction");
        Check(!Visuals<TextBlock>(window).Any(text => text.Text.Contains("旧版 U 校园", StringComparison.Ordinal) || text.Text.Contains("???", StringComparison.Ordinal)), "login page has correct platform and Chinese text");
        loginBrowser!.CoreWebView2.Navigate("https://turing.captcha.qcloud.com/template/drag_ele.html?aid=synthetic-captcha");
        await WaitAsync(() => service.LoginDiagnostics.Snapshot().BlockedNavigations > blockedBefore);
        Check(new Uri(loginBrowser.CoreWebView2.Source).IdnHost == "pintia.cn", "verifier cannot replace official login main document");
        window.Close(); await Task.Delay(80);
        service.Cancel(); await WaitAsync(() => !service.IsRunning);
    }
    private static async Task VerifyTaskActionsAsync()
    {
        var table = (DataGrid)dashboard.FindName("TaskTable");
        var row = table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Single(item => item.Task.Source == TaskSource.Manual);
        table.SelectedItem = row;
        var more = (Button)dashboard.FindName("MoreTaskActionsButton");
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
        var popup = (System.Windows.Controls.Primitives.Popup)dashboard.FindName("TaskActionsPopup");
        Check(popup.IsOpen && ((Button)dashboard.FindName("IgnoreButton")).IsVisible, "additional task actions can be opened");
        ((Button)dashboard.FindName("IgnoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => store.Get(row.Task.Id)?.State == TaskState.Ignored && !popup.IsOpen);
        Check(store.Get(row.Task.Id)?.State == TaskState.Ignored, "popup action updates selected task and dismisses popup");
        await WaitAsync(() => table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Any(item => item.Task.Id == row.Task.Id && item.Task.State == TaskState.Ignored));
        table.SelectedItem = table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Single(item => item.Task.Id == row.Task.Id);
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
        ((Button)dashboard.FindName("RestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => store.Get(row.Task.Id)?.State == TaskState.Pending);
        Check(!popup.IsOpen, "restoring a task closes additional actions");
    }
    private static async Task LayoutAsync()
    {
        foreach (var (width, height, file) in new[] { (1460.0, 900.0, "pta-dashboard-normal.png"), (1160.0, 740.0, "pta-dashboard-minimum.png") })
        {
            dashboard.Width = width; dashboard.Height = height; dashboard.UpdateLayout(); await Task.Delay(100);
            var card = (Button)dashboard.FindName("PtaConnectionButton");
            Check(card.IsVisible && card.ActualWidth > 100 && card.ActualHeight > 35, "PTA platform card accessible at window size");
            Check(!Visuals<TextBlock>(dashboard).Any(text => text.Text.Contains("???", StringComparison.Ordinal) || text.Text.Contains('\uFFFD')), "dashboard labels have no mojibake");
            Capture(dashboard, file);
        }
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) foreach (var value in Visuals<T>(VisualTreeHelper.GetChild(node, i))) yield return value;
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap(Math.Max(1, (int)window.ActualWidth), Math.Max(1, (int)window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(runRoot, name)); png.Save(output);
    }
    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(30); }
        throw new TimeoutException("PTA desktop did not update.");
    }
}
