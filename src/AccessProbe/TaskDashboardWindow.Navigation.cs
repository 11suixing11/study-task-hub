using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AccessProbe;

public partial class TaskDashboardWindow
{
    private sealed record WorkspaceRoute(string Key, WorkspacePage Page, bool Retained);
    private sealed class ConnectionFlow(string platformId, DateTimeOffset startedAt)
    {
        public string PlatformId { get; } = platformId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public bool Synchronizing { get; set; }
        public ConnectionReport? PendingSelectionReport { get; set; }
        public long SelectionNavigationRevision { get; set; }
    }

    private readonly Dictionary<string, WorkspaceRoute> workspaceRoutes = new(StringComparer.Ordinal);
    private readonly List<WorkspaceRoute> workspaceHistory = [];
    private WorkspaceRoute? activeRoute;
    private ConnectionFlow? connectionFlow;
    private bool workspaceExiting;
    private long workspaceNavigationRevision;
    private bool detailsVisibleBeforePage;
    public WorkspacePage? ActivePage => activeRoute?.Page;
    public string? ActivePageKey => activeRoute?.Key;

    private void ConfigureWorkspaceNavigation()
    {
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && activeRoute is not null && !TaskActionsPopup.IsOpen)
            { NavigateBack(); args.Handled = true; }
        };
        Closed += (_, _) => DisposeWorkspacePages();
    }

    public void ShowConnectionPage(string platformId, Func<WorkspacePage> create)
    {
        if (permitClose || workspaceExiting) return;
        var key = "connection:" + platformId;
        if (activeRoute?.Key == key && activeRoute.Page.CanReuse) return;
        var route = GetOrCreateRoute(key, create);
        connectionFlow = new(platformId, DateTimeOffset.UtcNow);
        NavigateTo(route);
    }

    public void ShowWorkspacePage(string key, WorkspacePage page, bool retained = false, bool replace = false)
    {
        if (permitClose || workspaceExiting) { page.Dispose(); return; }
        var route = RegisterRoute(key, page, retained);
        NavigateTo(route, replace);
    }

    private WorkspaceRoute GetOrCreateRoute(string key, Func<WorkspacePage> create)
    {
        if (workspaceRoutes.TryGetValue(key, out var route) && !route.Page.IsClosed)
        {
            if (route.Page.CanReuse) return route;
            route.Page.Dispose();
        }
        return RegisterRoute(key, create(), true);
    }

    private WorkspaceRoute RegisterRoute(string key, WorkspacePage page, bool retained)
    {
        if (workspaceRoutes.TryGetValue(key, out var existing) && !existing.Page.IsClosed)
        {
            if (!ReferenceEquals(existing.Page, page)) throw new InvalidOperationException("The page key is already registered.");
            return existing;
        }
        var route = new WorkspaceRoute(key, page, retained); workspaceRoutes[key] = route;
        if (key == "connection:pta" && page is MetadataConnectionPage metadataPage)
            metadataPage.PageFailureChanged += message =>
            {
                if (!permitClose && !workspaceExiting && connectionFlow is { PlatformId: "pta" }
                    && workspaceRoutes.TryGetValue(key, out var currentRoute) && ReferenceEquals(currentRoute, route)
                    && activeRoute is { Key: "platform:pta", Page: CourseSelectionPage selection })
                    selection.ShowConnectionPageFailure(message);
            };
        page.BackRequested += () => { if (ReferenceEquals(activeRoute, route)) NavigateBack(); };
        page.NavigationRequested += child =>
        {
            if (ReferenceEquals(activeRoute, route)) ShowWorkspacePage("content:" + Guid.NewGuid().ToString("N"), child);
            else child.Dispose();
        };
        page.Closed += (_, _) =>
        {
            workspaceRoutes.Remove(key); workspaceHistory.RemoveAll(item => ReferenceEquals(item, route));
            if (!permitClose && !workspaceExiting && ReferenceEquals(activeRoute, route)) NavigateBack();
        };
        return route;
    }

    private void NavigateTo(WorkspaceRoute route, bool replace = false)
    {
        if (ReferenceEquals(activeRoute, route)) return;
        workspaceNavigationRevision++;
        workspaceHistory.RemoveAll(item => ReferenceEquals(item, route));
        if (activeRoute is { } previous && !replace) workspaceHistory.Add(previous);
        if (connectionFlow is { } flow && route.Key != "connection:" + flow.PlatformId && route.Key != "platform:" + flow.PlatformId)
            connectionFlow = null;
        if (route.Key.StartsWith("connection:", StringComparison.Ordinal)
            && (connectionFlow is null || route.Key != "connection:" + connectionFlow.PlatformId))
            connectionFlow = new(route.Key["connection:".Length..], DateTimeOffset.UtcNow);
        var leaving = activeRoute;
        leaving?.Page.DeactivatePage();
        WorkspacePageHost.Content = null;
        activeRoute = route;
        WorkspacePageHost.Content = route.Page;
        route.Page.ActivatePage();
        if (route.Page is CourseSelectionPage selection)
            selection.ShowConnectionPageFailure(connectionFlow is { PlatformId: "pta" } && route.Key == "platform:pta"
                && workspaceRoutes.TryGetValue("connection:pta", out var loginRoute) && loginRoute.Page is MetadataConnectionPage loginPage
                    ? loginPage.PageFailureMessage : null);
        if (replace && leaving is { Retained: false }) leaving.Page.Dispose();
        UpdateWorkspaceNavigation();
    }

    public void NavigateBack()
    {
        if (permitClose || workspaceExiting) return;
        var leaving = activeRoute;
        if (workspaceHistory.Count > 0)
        {
            var previous = workspaceHistory[^1]; workspaceHistory.RemoveAt(workspaceHistory.Count - 1);
            NavigateTo(previous, true);
        }
        else ShowTaskWorkspace();
        if (leaving is { Retained: false } && !ReferenceEquals(activeRoute, leaving)) leaving.Page.Dispose();
    }

    private void WorkspaceBack(object sender, RoutedEventArgs args) => NavigateBack();
    private void WorkspaceHome(object sender, RoutedEventArgs args) => ShowDashboard();

    private void ShowTaskWorkspace()
    {
        if (workspaceExiting) return;
        workspaceNavigationRevision++;
        var leaving = activeRoute;
        connectionFlow = null;
        leaving?.Page.DeactivatePage(); WorkspacePageHost.Content = null; activeRoute = null;
        var transient = workspaceHistory.Where(route => !route.Retained).Concat(leaving is { Retained: false } ? [leaving] : [])
            .Distinct().ToArray();
        workspaceHistory.Clear();
        foreach (var route in transient) route.Page.Dispose();
        UpdateWorkspaceNavigation();
    }

    private void UpdateWorkspaceNavigation()
    {
        var showingPage = activeRoute is not null;
        if (showingPage && TaskWorkspaceFrame.Visibility == Visibility.Visible)
            detailsVisibleBeforePage = TaskDetailPanel.Visibility == Visibility.Visible;
        TaskWorkspaceFrame.Visibility = showingPage ? Visibility.Collapsed : Visibility.Visible;
        TaskDetailPanel.Visibility = showingPage || !detailsVisibleBeforePage ? Visibility.Collapsed : Visibility.Visible;
        DashboardHeading.Visibility = DashboardToolbarActions.Visibility = showingPage ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceHeading.Visibility = WorkspacePageHost.Visibility = showingPage ? Visibility.Visible : Visibility.Collapsed;
        WorkspacePageTitleText.Text = activeRoute?.Page.Title ?? "";
        DetailsToggleButton.IsEnabled = !showingPage;
        SidebarPanel.IsEnabled = WorkspaceBackButton.IsEnabled = WorkspaceHomeButtonInPage.IsEnabled = !workspaceExiting;
        TaskActionsPopup.IsOpen = false;
        DetailsColumn.Width = new GridLength(showingPage || !detailsVisibleBeforePage ? 0 : ActualWidth < 1180 ? 256 : ActualWidth < 1280 ? 280 : 300);
        if (showingPage) activeRoute!.Page.Focus();
    }

    private void DisposeWorkspacePages()
    {
        activeRoute?.Page.DeactivatePage();
        WorkspacePageHost.Content = null; activeRoute = null; connectionFlow = null;
        workspaceHistory.Clear();
        foreach (var route in workspaceRoutes.Values.ToArray()) route.Page.Dispose();
        workspaceRoutes.Clear();
    }

    public void DisposeWorkspaceForExit()
    {
        workspaceExiting = true;
        connectionFlow = null;
        DisposeWorkspacePages();
    }

    public async Task ShowExitNoticeAsync(string message)
    {
        if (permitClose) return;
        var page = new WorkspaceMessagePage("登录状态保存提示", message, "关闭程序", cancel: null);
        activeRoute = new WorkspaceRoute("exit-notice", page, false);
        WorkspacePageHost.Content = page;
        page.ActivatePage();
        IsEnabled = true;
        UpdateWorkspaceNavigation();
        await page.Completion;
        page.Dispose();
        WorkspacePageHost.Content = null;
        activeRoute = null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string accept = "确认", string? cancel = "取消")
    {
        if (permitClose || workspaceExiting) return false;
        var origin = activeRoute;
        var flow = connectionFlow;
        var page = new WorkspaceMessagePage(title, message, accept, cancel);
        ShowWorkspacePage("message:" + Guid.NewGuid().ToString("N"), page);
        var promptRevision = workspaceNavigationRevision;
        var accepted = await page.Completion;
        if (activeRoute?.Page == page) NavigateBack();
        else page.Dispose();
        var returnedToOrigin = ReferenceEquals(activeRoute, origin) && workspaceNavigationRevision == promptRevision + 1;
        if (!permitClose && !workspaceExiting && flow is not null && returnedToOrigin
            && activeRoute?.Key == "connection:" + flow.PlatformId)
            connectionFlow = flow;
        return accepted && !permitClose && !workspaceExiting && returnedToOrigin;
    }

    // Only a fresh report for the active flow can navigate. Background or old-page results cannot.
    private void ObserveConnectionReport(ConnectionReport report)
    {
        var flow = connectionFlow;
        if (workspaceExiting || flow is null || flow.PlatformId != report.PlatformId || report.CheckedAtUtc < flow.StartedAt || flow.Synchronizing) return;
        Dispatcher.BeginInvoke(new Action(async () => await AdvanceConnectionAsync(flow, report)));
    }

    private async Task AdvanceConnectionAsync(ConnectionFlow flow, ConnectionReport report)
    {
        var onConnectionPage = activeRoute?.Key == "connection:" + flow.PlatformId;
        var awaitingSelectionReturn = activeRoute?.Key == "platform:" + flow.PlatformId
            && flow.PendingSelectionReport is { } pending && report.CheckedAtUtc >= pending.CheckedAtUtc
            && workspaceNavigationRevision == flow.SelectionNavigationRevision
            && activeRoute.Page is CourseSelectionPage { HasUnsavedChanges: false };
        if (permitClose || workspaceExiting || !ReferenceEquals(connectionFlow, flow) || !onConnectionPage && !awaitingSelectionReturn
            || !IsCurrentConnectionReport(report)
            || !report.SessionAvailable || report.LoginRequired || !report.AccountVerified || !report.SessionSaved || !report.CatalogCovered
            || report.CatalogOnly && report.ProblemCodes.Count > 0) return;
        if (CanReturnAfterConnection(report))
        { ReturnAfterConnection(flow, report); return; }
        if (!onConnectionPage) return;
        // A verified empty official catalog stays in the connection workflow for a retry;
        // it is not a completed result and must not send the user back with a success message.
        if (report.Courses == 0) return;
        // Course selection stays with the user: an unselected catalog keeps the connection page
        // with its own "选择课程并同步" action instead of opening the selection page automatically.
        if (!report.SelectionConfirmed || report.SelectedCourses == 0) return;
        if (!report.CatalogOnly) return;
        flow.Synchronizing = true;
        try
        {
            await SynchronizeRecordingPlatformAsync(flow.PlatformId);
            if (!permitClose && ReferenceEquals(connectionFlow, flow) && activeRoute?.Key == "connection:" + flow.PlatformId
                && store.LoadConnectionReport(flow.PlatformId) is { } latest && latest.CheckedAtUtc >= report.CheckedAtUtc
                && latest.SessionAvailable && latest.AccountVerified && !latest.LoginRequired && latest.SessionSaved
                && CanReturnAfterConnection(latest) && IsCurrentConnectionReport(latest))
                ReturnAfterConnection(flow, latest);
        }
        catch (Exception error) when (error is TaskValidationException or OperationCanceledException or InvalidOperationException)
        {
            if (!permitClose && ReferenceEquals(connectionFlow, flow))
                ShowError("COURSE001", "登录已确认，本轮任务同步未完成；请查看接入原因后重试。");
        }
        finally { flow.Synchronizing = false; }
    }

    private void ReturnAfterConnection(ConnectionFlow flow, ConnectionReport report)
    {
        if (!ReferenceEquals(connectionFlow, flow)) return;
        ShowTasks(1);
        ShowConnectionCompletion(report);
    }

    private bool IsCurrentConnectionReport(ConnectionReport report) => store.LoadConnectionReport(report.PlatformId)?.CheckedAtUtc == report.CheckedAtUtc
        && (report.PlatformId != "pta" || !metadataServices.TryGetValue("pta", out var service)
            || service.IsCurrentConnection(report) && service.LoginDiagnostics.Snapshot().PageFailure is null
                or { Result: MetadataLoginResult.Blocked, Stage: MetadataLoginStage.MainNavigation or MetadataLoginStage.FrameNavigation or MetadataLoginStage.Popup });

    private bool IsPtaPageReturnReady(ConnectionReport report) => report.PlatformId != "pta"
        || !metadataServices.TryGetValue("pta", out var service) || service.LoginDiagnostics.PtaHomeConfirmed;

    private bool CanReturnAfterConnection(ConnectionReport report, bool requirePtaHome = true) => report is
        { SessionAvailable: true, AccountVerified: true, LoginRequired: false, SessionSaved: true, CatalogCovered: true,
            CatalogOnly: false, SelectionConfirmed: true }
        && (report.TaskListsCovered || report.PlatformId == "pta") && (!requirePtaHome || IsPtaPageReturnReady(report));

    private void ShowConnectionCompletion(ConnectionReport report)
    {
        if (report.TaskListsCovered || report.Courses == 0)
        {
            ShowSuccess(PlatformCatalog.Name(report.PlatformId) + (report.Courses == 0
                ? "已接入，官网当前没有课程。" : $"已接入，已同步 {report.Tasks} 条任务。"));
            return;
        }
        var reason = ConnectionFeedback.Reason(report.ProblemOutcomes);
        ResultText.Foreground = Brushes.Firebrick;
        ResultText.Text = PlatformCatalog.Name(report.PlatformId) + $"已接入，已读取 {report.Tasks} 条任务；本轮同步未完整确认。"
            + (reason.Length > 0 ? reason : "部分题目集或答卷状态仍需核对。") + "可在平台管理中重试同步。";
    }
}
