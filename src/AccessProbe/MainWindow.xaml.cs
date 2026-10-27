using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace AccessProbe;

public partial class MainWindow : Window
{
    private readonly string projectRoot;
    private readonly TaskStore taskStore;
    private readonly Action? showDashboard;
    private readonly ChaoxingSyncService? sessionService;
    private readonly Dictionary<string, int> responseCounts = new();
    private bool closing;
    private bool clearing;
    private bool initialized;
    private ulong activeNavigationId;
    private ulong? blockedNavigationId;
    private string navigationOutcome = "not_started";
    private string blockedCategory = "none";
    public event Action<string>? OfficialNavigationCompleted;
    public event Action? SessionClearing;
    public event Action<bool>? SessionClearCompleted;

    public MainWindow(string root, TaskStore? store = null, Action? openDashboard = null, ChaoxingSyncService? sessionService = null)
    {
        projectRoot = root;
        taskStore = store ?? new TaskStore(Path.Combine(root, ".local", "data", "tasks.db"));
        showDashboard = openDashboard;
        this.sessionService = sessionService;
        InitializeComponent();
        Loaded += InitializeBrowser;
        Closed += (_, _) => { closing = true; InvalidateCollection(); Browser.Dispose(); };
    }

    private async void InitializeBrowser(object sender, RoutedEventArgs args)
    {
        try
        {
            var profile = Path.Combine(projectRoot, ".local", "webview-profile");
            var environment = sessionService is null ? await CoreWebView2Environment.CreateAsync(null, profile)
                : await sessionService.GetLoginEnvironmentAsync();
            if (closing) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (closing) return;
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            // WebView can begin a GET while NavigationStarting is being handled. Fulfil HTTP
            // document requests locally; the navigation handler opens their HTTPS counterpart.
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.Document,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                if (Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var address)
                    && address.Scheme == Uri.UriSchemeHttp)
                    request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "HTTPS Required",
                        "Cache-Control: no-store\r\nContent-Type: text/plain; charset=utf-8\r\n");
            };
            core.FrameCreated += (_, created) => TrackWorkFrame(created.Frame);
            core.NavigationStarting += (_, navigation) => MainNavigationStarting(core, navigation);
            core.FrameNavigationStarting += (_, navigation) =>
            {
                InvalidateCollection();
                if (!ProbePolicy.IsAllowedNavigation(navigation.Uri) || clearing)
                {
                    navigation.Cancel = true;
                    // The frame-specific handler replaces official HTTP links using HTTPS.
                    if (!clearing && ProbePolicy.TryGetHttpsNavigation(navigation.Uri, out var httpsFrameAddress)) return;
                    blockedCategory = clearing ? "clearing" : ProbePolicy.NavigationCategory(navigation.Uri);
                    DiagnosticText.Text = $"最近拦截：内嵌页面 / {blockedCategory}；可能影响登录组件，尚未确认。";
                }
            };
            core.NewWindowRequested += (_, request) => NewWindowRequested(core, request);
            core.PermissionRequested += (_, request) => request.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, download) => download.Cancel = true;
            core.ServerCertificateErrorDetected += (_, error) => error.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            core.NavigationCompleted += (_, navigation) =>
            {
                if (closing || clearing || navigation.NavigationId != activeNavigationId) return;
                OriginText.Text = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)
                    ? "当前站点：" + uri.Host : "当前站点：未打开";
                if (blockedNavigationId == navigation.NavigationId) return;
                if (navigation.IsSuccess) mainHttpsUpgrade.Reset();
                navigationOutcome = navigation.IsSuccess ? "loaded" : navigation.WebErrorStatus.ToString();
                StatusText.Text = navigation.IsSuccess
                    ? "页面已加载。登录成功后程序会在首页后台同步课程与作业，无需逐个打开作业页。"
                    : navigation.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled
                        ? "本次页面导航已取消（例如刷新或跳转），不等同于账号登录失败。可重新打开官方登录。"
                        : $"页面导航失败：{navigation.WebErrorStatus}，HTTP {navigation.HttpStatusCode}。未跳过证书验证；此提示不能判断账号状态。";
                if (navigation.IsSuccess) OfficialNavigationCompleted?.Invoke(core.Source);
            };
            core.WebResourceResponseReceived += (_, response) =>
            {
                if (closing || clearing || ObserveCheck.IsChecked != true) return;
                if (response.Response is not { StatusCode: var statusCode }) return;
                var category = ProbePolicy.SafeResponseCategory(response.Request.Uri, statusCode);
                responseCounts[category] = responseCounts.GetValueOrDefault(category) + 1;
            };
            initialized = true;
            LoginButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            ClearButton.IsEnabled = true;
            CollectButton.IsEnabled = true;
            BrowserProbeButton.IsEnabled = true;
            ReadWorkButton.IsEnabled = true;
            StatusText.Text = "正在打开官方登录。登录成功后可回到首页，程序会自动同步各课程作业。";
            if (OfficialNavigationCompleted is not null) core.Navigate("https://i.chaoxing.com/");
        }
        catch
        {
            if (!closing) StatusText.Text = "登录环境初始化失败。请确认 WebView2 运行时可用，且项目目录可写。未记录异常正文或账号数据。";
        }
    }

    private void OpenTaskDashboard(object sender, RoutedEventArgs args)
    {
        if (showDashboard is not null) showDashboard();
        else new TaskDashboardWindow(taskStore, projectRoot, () => Activate(), () => Close()) { Owner = this }.Show();
    }

    public void ShowSyncStatus(string message, bool warning)
    {
        if (closing) return;
        LoginSyncStatusText.Text = message;
        LoginSyncStatusText.Foreground = warning ? System.Windows.Media.Brushes.Firebrick
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 90, 195));
    }

    private void OpenLogin(object sender, RoutedEventArgs args)
    {
        if (initialized && !clearing) Browser.CoreWebView2.Navigate("https://i.chaoxing.com/");
    }

    private void RefreshLogin(object sender, RoutedEventArgs args)
    {
        if (!initialized || clearing) return;
        InvalidateCollection();
        var address = Browser.CoreWebView2.Source;
        if (address == "about:blank" || !ProbePolicy.IsAllowedNavigation(address))
            Browser.CoreWebView2.Navigate("https://i.chaoxing.com/");
        else
            Browser.CoreWebView2.Reload();
        StatusText.Text = "正在刷新官方页面；二维码是否更新取决于平台，请等待页面加载后重试扫码。";
    }

    private async Task NavigateToBlankAsync()
    {
        var core = Browser.CoreWebView2;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs navigation)
        {
            if (core.Source == "about:blank") completion.TrySetResult(navigation.IsSuccess);
        }
        core.NavigationCompleted += Completed;
        try
        {
            core.Stop();
            core.Navigate("about:blank");
            if (!await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)))
                throw new InvalidOperationException("Blank navigation failed.");
        }
        finally { core.NavigationCompleted -= Completed; }
    }

    private async void ClearSession(object sender, RoutedEventArgs args)
    {
        if (!initialized || clearing) return;
        if (MessageBox.Show("将清除此独立登录环境的 Cookie 和浏览数据，不影响你平常使用的浏览器。继续？",
            "清除登录数据", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        clearing = true;
        SessionClearing?.Invoke();
        InvalidateCollection();
        CollectButton.IsEnabled = BrowserProbeButton.IsEnabled = ReadWorkButton.IsEnabled = false;
        LoginButton.IsEnabled = ClearButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        Browser.Visibility = Visibility.Hidden;
        StatusText.Text = "正在清理独立登录数据，请稍候……";
        var cleared = false;
        try
        {
            if (sessionService is not null) await sessionService.PrepareSessionClearAsync();
            else new ChaoxingSessionStore(Path.Combine(projectRoot, ".local", "sessions", "chaoxing.bin")).Clear();
            await NavigateToBlankAsync();
            if (closing) return;
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
            if (closing) return;
            responseCounts.Clear();
            navigationOutcome = "cleared";
            blockedCategory = "none";
            mainHttpsUpgrade.Reset();
            httpsUpgradeCount = 0;
            lastHttpsUpgradeSurface = "none";
            DiagnosticText.Text = "诊断仅记录导航状态与拦截类别，不包含二维码、地址参数或账号信息。";
            nextCollectionAt = DateTimeOffset.MinValue;
            LoginObserved.IsChecked = CoursesObserved.IsChecked = TasksObserved.IsChecked = StatusObserved.IsChecked = false;
            OriginText.Text = "当前站点：未打开";
            StatusText.Text = "登录数据已清理。可以直接点击“打开官方登录”重新登录，无需重启；二维码过期可点击“刷新登录页”。";
            cleared = true;
        }
        catch
        {
            if (!closing) StatusText.Text = "清理未完成，不能确认已退出登录。操作按钮已恢复，可重试清理；不要把本次失败当作会话已删除。";
        }
        finally
        {
            clearing = false;
            SessionClearCompleted?.Invoke(cleared);
            if (!closing)
            {
                Browser.Visibility = Visibility.Visible;
                LoginButton.IsEnabled = ClearButton.IsEnabled = RefreshButton.IsEnabled = initialized;
                CollectButton.IsEnabled = BrowserProbeButton.IsEnabled = ReadWorkButton.IsEnabled = initialized && collectionCancellation == null;
            }
        }
    }

    private void ExportReport(object sender, RoutedEventArgs args)
    {
        try
        {
            var directory = Path.Combine(projectRoot, "artifacts", "probe-reports");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"access-probe-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            var report = new
            {
                schemaVersion = 8,
                generatedAtUtc = DateTimeOffset.UtcNow,
                environmentInitialized = initialized,
                sessionRecovery = sessionService?.SessionRecoveryOutcome ?? "standalone",
                sessionPersistence = sessionService?.SessionPersistenceOutcome ?? "standalone",
                navigation = new
                {
                    outcome = navigationOutcome, lastBlockedCategory = blockedCategory,
                    httpsUpgradeCount, lastHttpsUpgradeSurface
                },
                manualObservations = new
                {
                    login = LoginObserved.IsChecked == true,
                    courses = CoursesObserved.IsChecked == true,
                    assignments = TasksObserved.IsChecked == true,
                    submissionState = StatusObserved.IsChecked == true
                },
                responseCategories = responseCounts,
                automatedAccountVerification = "not_implemented",
                automatedCourseCollection = "available_via_dashboard_sync",
                automatedAssignmentCollection = "available_via_dashboard_sync",
                noticeProbe = new
                {
                    outcome = lastCollectionOutcome,
                    taskNoticeCount = collectedTaskCount,
                    notificationCount = collectedNoticeCount,
                    pageCount = collectedPageCount,
                    terminationReason = collectionTerminationReason,
                    nextCursorPresent = collectedCursorPresent,
                    http = lastNoticeHttpDiagnostic,
                    browserContext = lastBrowserNoticeDiagnostic,
                    coverage = lastCollectionOutcome == "parsed_partial"
                        ? (collectedPageCount > 1 ? "notification_recent_window_not_complete" : "notification_page_only_not_complete")
                        : "not_verified"
                },
                workPageRead = new
                {
                    outcome = lastWorkReadOutcome,
                    taskCount = workReadTaskCount,
                    pageCount = workReadPageCount,
                    skippedFrameCount = workReadSkippedFrameCount,
                    invalidLinkCount = workReadInvalidLinkCount,
                    missingTitleCount = workReadMissingTitleCount,
                    unrecognizedPageCount = workReadUnrecognizedPageCount,
                    rowLimitReached = workReadRowLimitReached,
                    coverage = lastWorkReadOutcome is "parsed_partial" or "parsed_time_window"
                        ? "loaded_page_only_not_complete" : "not_verified"
                },
                completeness = "not_verified"
            };
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = "脱敏记录已写入 artifacts/probe-reports；记录不包含地址、标题、账号、密码、Cookie 或响应正文。";
        }
        catch
        {
            StatusText.Text = "报告保存失败，请检查项目目录写入权限。";
        }
    }
}
