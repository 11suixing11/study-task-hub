using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Data.Sqlite;
using F = PlatformMetadataTestData.PtaFixtures;

namespace PtaSyncHarness;

internal static class PtaConnectionRecoveryChecks
{
    public static async Task VerifyAsync(TaskDashboardWindow dashboard, TaskStore store, string runRoot, Action<bool, string> check)
    {
        var labels = new List<string>();
        void Check(bool value, string label)
        {
            check(value, label); labels.Add(label);
            check(Application.Current.Windows.Count == 1 && Application.Current.Windows[0] == dashboard,
                label + " keeps one native main window");
        }
        dashboard.ShowDashboard();
        var root = Path.Combine(runRoot, "pta-return-recovery"); Directory.CreateDirectory(root);
        var fixture = new F.Handler { RejectLogin = true };
        var sessionBrowser = new WebView2 { Visibility = Visibility.Collapsed };
        var panel = (Panel)dashboard.Content; panel.Children.Add(sessionBrowser);
        BrowserFixtures.BlockNetwork(sessionBrowser);
        using var service = new MetadataSyncService(MetadataPlatform.Pta, store, root, sessionBrowser, () => fixture, TimeSpan.FromMinutes(30));
        dashboard.AttachMetadataSync(service);
        var reportsPublishedAfterPersistence = true;
        void CheckPublishedPersistence(ConnectionReport report)
        {
            var persisted = report.CatalogOnly ? store.LoadCourseRecording("pta").LastCatalogRefresh?.CheckedAtUtc
                : store.LoadMetadataSettings("pta").LastSyncAtUtc;
            reportsPublishedAfterPersistence &= persisted is not null && persisted <= report.CheckedAtUtc;
        }
        service.ConnectionChanged += CheckPublishedPersistence;
        await service.EnsureSessionAsync(); BrowserFixtures.SetCookie(sessionBrowser, fixture.Session); await Task.Delay(100);
        var blockHome = false;
        var delayHomeCompletion = false;
        var homeResourceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var homeDocumentReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var homeFinalNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHomeCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void DelayNextHomeCompletion()
        {
            homeResourceEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            homeDocumentReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            homeFinalNavigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseHomeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            delayHomeCompletion = true;
        }
        var passport = "https://passport.pintia.cn/login?synthetic=landing";
        string? Document(Uri route)
        {
            if (route.IdnHost == "pintia.cn" && route.AbsolutePath == "/auth/login")
                return "<!doctype html><meta charset='utf-8'><title>PTA synthetic login</title><script>location.href="
                    + JsonSerializer.Serialize(passport) + ";</script>";
            if (route.IdnHost == "passport.pintia.cn")
                return "<!doctype html><meta charset='utf-8'><title>PTA synthetic passport</title><body>合成百腾通行证落地页"
                    + "<script>location.href='http://passport.pintia.cn/api/login/dal?redir=synthetic-return';</script></body>";
            if (route.IdnHost == "pintia.cn" && route.AbsolutePath == "/course-class")
                return "<!doctype html><meta charset='utf-8'><title>PTA synthetic student home</title><body>合成 PTA 课程主页"
                    + (delayHomeCompletion ? "<img src='/synthetic-pta-home-delay.svg' alt='合成加载等待'>" : "") + "</body>";
            return null;
        }
        MetadataConnectionPage CreateConnection()
        {
            var page = new MetadataConnectionPage(service, dashboard.ShowDashboard);
            BrowserFixtures.BlockNetwork(Browser(page), document: Document, status: route =>
                blockHome && route.IdnHost == "pintia.cn" && route.AbsolutePath == "/course-class" ? 503 : 200);
            Browser(page).CoreWebView2InitializationCompleted += (_, initialized) =>
            {
                if (!initialized.IsSuccess) return;
                var pageCore = Browser(page).CoreWebView2;
                var homeLoadHeld = false;
                ulong? homeNavigationId = null;
                pageCore.NavigationStarting += (_, navigation) =>
                {
                    if (delayHomeCompletion && navigation.Uri == MetadataPlatform.Pta.Home.AbsoluteUri)
                        homeNavigationId = navigation.NavigationId;
                };
                pageCore.WebResourceRequested += async (_, request) =>
                {
                    if (!delayHomeCompletion || !Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var route)
                        || route.IdnHost != "pintia.cn" || route.AbsolutePath != "/synthetic-pta-home-delay.svg") return;
                    // Keep the real Home navigation unfinished after DOMContentLoaded without accessing the network.
                    var deferral = request.GetDeferral(); homeLoadHeld = true; homeResourceEntered.TrySetResult(true);
                    try
                    {
                        await releaseHomeCompletion.Task;
                        if (page.IsClosed) return;
                        request.Response = pageCore.Environment.CreateWebResourceResponse(
                            new MemoryStream(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>")),
                            200, "OK", "Content-Type: image/svg+xml\r\nCache-Control: no-store\r\n");
                    }
                    finally { deferral.Complete(); }
                };
                pageCore.DOMContentLoaded += (_, navigation) =>
                {
                    if (delayHomeCompletion && navigation.NavigationId == homeNavigationId
                        && pageCore.Source == MetadataPlatform.Pta.Home.AbsoluteUri)
                        homeDocumentReady.TrySetResult(true);
                };
                pageCore.NavigationCompleted += (_, navigation) =>
                {
                    if (homeLoadHeld && navigation.NavigationId == homeNavigationId && pageCore.Source == MetadataPlatform.Pta.Home.AbsoluteUri
                        && navigation.IsSuccess && navigation.HttpStatusCode < 400) homeFinalNavigation.TrySetResult(true);
                };
            };
            return page;
        }

        var connection = CreateConnection();
        dashboard.ShowWorkspacePage("fixture-pta-recovery", connection);
        var core = await ReadyAsync(connection, service);
        await service.StartAsync(true, afterLogin: true); service.Cancel();
        Check(core.Source == passport && service.Connection is { LoginRequired: true, AccountVerified: false }
            && service.LoginDiagnostics.Snapshot().PageFailure is { Result: MetadataLoginResult.Blocked },
            "unverified passport landing stays in login page with the real blocked return reason");
        var profile = core.Environment.UserDataFolder;
        fixture.RejectLogin = false;
        await service.StartAsync(true, afterLogin: true);
        await WaitAsync(() => core.Source == MetadataPlatform.Pta.Home.AbsoluteUri);
        await DocumentCompletedAsync(core, "PTA synthetic student home");
        await service.RefreshCourseCatalogAsync();
        Check(core.Environment.UserDataFolder == profile && profile == sessionBrowser.CoreWebView2.Environment.UserDataFolder,
            "fixed HTTPS PTA home return reuses the existing signed-in profile");
        Check(service.Connection is { AccountVerified: true, CatalogCovered: true, SessionSaved: true }
            && service.LoginDiagnostics.PtaHomeConfirmed && service.LoginDiagnostics.Snapshot().PageFailure is null,
            "successful student return and fresh official account check clear the stale login failure");
        Check(!Text(connection).Any(text => text.Contains("PTA003：", StringComparison.Ordinal)
                || text.Contains("此登录跳转尚未适配", StringComparison.Ordinal)),
            "stale PTA003 no longer covers the verified connection status");
        await service.StartAsync(true);
        var firstTaskCheckAt = service.Connection!.CheckedAtUtc;
        var firstTaskRequestCount = fixture.Paths.Count;
        var guide = (StackPanel)((ScrollViewer)((Grid)connection.Content).Children[0]).Content;
        var statusPanel = guide.Children.OfType<ConnectionStatusPanel>().Single();
        var retryTasks = ((StackPanel)statusPanel.Child).Children.OfType<WrapPanel>().Single().Children.OfType<Button>()
            .Single(button => button.Visibility == Visibility.Visible && Equals(button.Content, "重试同步"));
        retryTasks.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => !service.IsRunning && service.Connection?.CheckedAtUtc > firstTaskCheckAt);
        Check(service.Connection is { AccountVerified: true, CatalogOnly: false, Tasks: > 0 }
            && fixture.Paths.Skip(firstTaskRequestCount).Any(path => path.EndsWith("/exams", StringComparison.Ordinal))
            && core.Source == MetadataPlatform.Pta.Home.AbsoluteUri,
            "the task retry button rereads actual answer metadata without reopening official login");

        var beforeBlock = service.LoginDiagnostics.Snapshot().BlockedNavigations;
        await core.ExecuteScriptAsync("(() => { const frame=document.createElement('iframe'); frame.src='https://unconfirmed.example/login'; document.body.appendChild(frame); })()");
        await WaitAsync(() => service.LoginDiagnostics.Snapshot().BlockedNavigations > beforeBlock);
        await service.RefreshCourseCatalogAsync();
        Check(service.LoginDiagnostics.Snapshot().PageFailure is { Stage: MetadataLoginStage.FrameNavigation },
            "new iframe failure is not erased by an old successful home or background account check");
        Check(core.Source == MetadataPlatform.Pta.Home.AbsoluteUri, "PTA automatic recovery does not loop after its one attempted return");
        connection.Close(); service.Cancel(); await WaitAsync(() => !service.IsRunning);

        var factories = 0;
        MetadataConnectionPage? flowPage = null;
        WorkspacePage CreateFlow()
        { factories++; return flowPage = CreateConnection(); }
        dashboard.ShowConnectionPage("pta", CreateFlow);
        core = await ReadyAsync(flowPage!, service);
        await service.StartAsync(true, afterLogin: true);
        await WaitAsync(() => !service.IsRunning && dashboard.ActivePage is null && service.Connection is { CatalogOnly: false });
        Check(service.Connection is { AccountVerified: true, SessionSaved: true, SupportedTaskListsCovered: true,
                TaskListsCovered: false, Tasks: > 0, StatusNeedsReview: > 0 }
            && service.Connection.CheckedAtUtc > firstTaskCheckAt,
            "unverified PTA answers preserve the current task report after the async test");
        var partial = service.Connection!;
        Check(partial is { AccountVerified: true, SessionSaved: true, TaskListsCovered: false, Tasks: 5 }
            && partial.ProblemCodes.Count > 0, "verified PTA partial task sync returns home while keeping its coverage and issue evidence");
        var result = (TextBlock)dashboard.FindName("ResultText");
        Check(result.Text.Contains("PTA已接入", StringComparison.Ordinal) && result.Text.Contains("本轮同步未完整确认", StringComparison.Ordinal)
            && !result.Text.Contains("已同步 5 条", StringComparison.Ordinal), "home feedback explains partial PTA results without claiming complete sync");
        Check(store.LoadTasks().Any(task => task.Source == TaskSource.Manual)
            && store.LoadCourseRecording("pta").SelectionConfirmed, "partial login flow keeps history and the existing course choice");

        fixture.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/30004/exams", StringComparison.Ordinal)
            ? F.Handler.Reply(request, HttpStatusCode.OK, F.Exam(F.Set("30004"), F.Account, "END", "END", true)) : null;
        dashboard.ShowConnectionPage("pta", CreateFlow);
        await service.StartAsync(true, afterLogin: true);
        await WaitAsync(() => !service.IsRunning && dashboard.ActivePage is null && service.Connection is { CatalogOnly: false, TaskListsCovered: true });
        Check(factories == 1 && !flowPage!.IsClosed && result.Text.Contains("已同步 5 条任务", StringComparison.Ordinal),
            "complete PTA sync returns home and reuses the existing single-window connection page");

        var observe = typeof(TaskDashboardWindow).GetMethod("ObserveConnectionReport", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Action<ConnectionReport>? interfere = null;
        foreach (var interference in new[] { "session_changed", "new_generation", "new_login_required" })
        {
            dashboard.ShowConnectionPage("pta", CreateFlow);
            ConnectionReport? published = null;
            interfere = report =>
            {
                if (report.CatalogOnly || !report.AccountVerified || published is not null) return;
                published = report;
                if (interference == "session_changed") service.LoginDiagnostics.RecordSessionChange("profile_changed");
                else if (interference == "new_generation") service.ConnectionOpened();
                else store.SaveConnectionReport(report with { CheckedAtUtc = report.CheckedAtUtc.AddTicks(1), LoginRequired = true, AccountVerified = false });
            };
            service.ConnectionChanged += interfere;
            await service.StartAsync(true);
            service.ConnectionChanged -= interfere; service.Cancel(); await Task.Delay(150);
            Check(published is not null && dashboard.ActivePage == flowPage,
                "queued successful report stays on the connection page after " + interference);
            if (interference == "session_changed")
            {
                service.LoginDiagnostics.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.MetadataReady);
                observe.Invoke(dashboard, [published]); await Task.Delay(100);
                Check(!service.IsCurrentConnection(published!) && dashboard.ActivePage == flowPage,
                    "a new diagnostic verification without publishing a new report cannot revive an invalidated report");
            }
            dashboard.ShowDashboard();
        }
        dashboard.ShowDashboard(); flowPage!.Close(); service.Cancel();

        var retryPage = CreateConnection(); dashboard.ShowConnectionPage("pta", () => retryPage);
        await ReadyAsync(retryPage, service);
        var oldReportAt = service.Connection!.CheckedAtUtc;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeReply = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/u/current") { entered.TrySetResult(true); await release.Task; }
        };
        var oldCheck = service.StartAsync(true, afterLogin: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retryButton = (Button)typeof(MetadataConnectionPage).GetField("login", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(retryPage)!;
        retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        release.TrySetResult(true); await oldCheck; fixture.BeforeReply = null; service.Cancel(); await Task.Delay(150);
        Check(service.Connection?.CheckedAtUtc == oldReportAt && dashboard.ActivePage == retryPage
            && service.LoginDiagnostics.AccountValidationRevision == 0,
            "reopening official login cancels an old in-flight account check before it can publish or navigate");
        await service.StartAsync(true, afterLogin: true);
        await WaitAsync(() => !service.IsRunning && dashboard.ActivePage is null);
        Check(service.Connection is { AccountVerified: true, TaskListsCovered: true }, "a fresh check recovers normally after login retry cancelled the old run");
        retryPage.Close();

        blockHome = true;
        var failurePage = CreateConnection(); dashboard.ShowConnectionPage("pta", () => failurePage);
        var failedCore = await ReadyAsync(failurePage, service);
        await service.StartAsync(true, afterLogin: true);
        await WaitAsync(() => service.LoginDiagnostics.Snapshot().PageFailure is { Result: MetadataLoginResult.NetworkFailed });
        await WaitAsync(() => !service.IsRunning); await service.StartAsync(true);
        Check(dashboard.ActivePage == failurePage && service.Connection is { AccountVerified: true } && service.LoginDiagnostics.Snapshot().PageFailure is { Result: MetadataLoginResult.NetworkFailed }
            && Text(failurePage).Any(text => text.Contains("PTA002：官网加载失败", StringComparison.Ordinal)),
            "real failed home navigation remains actionable even when the account API still succeeds");
        failurePage.Close(); service.Cancel();

        blockHome = false;
        fixture.Override = null;
        // Remove only this isolated fixture's previous course choice to exercise genuine first-login defaults.
        void ResetFixtureCourseChoice()
        {
            using var fixtureDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
            fixtureDb.Open(); using var command = fixtureDb.CreateCommand();
            command.CommandText = "DELETE FROM settings WHERE key = $key"; command.Parameters.AddWithValue("$key", "course-recording:pta");
            command.ExecuteNonQuery();
        }
        ResetFixtureCourseChoice(); DelayNextHomeCompletion();
        try
        {
            dashboard.ShowConnectionPage("pta", CreateFlow);
            await ReadyAsync(flowPage!, service);
            await service.StartAsync(true, afterLogin: true);
            Check(dashboard.ActivePage == flowPage && service.Connection is { CatalogOnly: true, SelectionConfirmed: false },
                "first verified PTA login stays on the connection page and leaves course selection to the user");
            service.RequestCourseSelection();
            await WaitAsync(() => dashboard.ActivePage is CourseSelectionPage);
            Check(dashboard.ActivePageKey == "platform:pta",
                "course selection opens on user request inside the same main window");
            await homeResourceEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await homeDocumentReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!homeFinalNavigation.Task.IsCompleted && !service.LoginDiagnostics.PtaHomeConfirmed,
                "first PTA course selection can open after Home DOM readiness but before final navigation completion");
            var selection = (CourseSelectionPage)dashboard.ActivePage!;
            await WaitAsync(() => selection.FindName("RecordingCourseList") is ListBox { ItemsSource: not null }
                && ((Button)selection.FindName("RecordingSaveButton")).IsEnabled);
            var catalogAt = service.Connection!.CheckedAtUtc;
            var saveSelection = (Button)selection.FindName("RecordingSaveButton");
            ((Button)selection.FindName("RecordingSelectAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            saveSelection.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !service.IsRunning && saveSelection.IsEnabled
                && store.LoadCourseRecording("pta").SelectionConfirmed
                && service.Connection is { CatalogOnly: false } savedReport && savedReport.CheckedAtUtc > catalogAt);
            var savedSelectionReport = service.Connection!;
            Check(dashboard.ActivePage == selection && !homeFinalNavigation.Task.IsCompleted
                && !service.LoginDiagnostics.PtaHomeConfirmed && service.IsCurrentConnection(savedSelectionReport)
                && savedSelectionReport is { AccountVerified: true, CatalogCovered: true, SessionSaved: true, TaskListsCovered: false, Tasks: 6 },
                "saved first PTA course sync waits on selection while the real Home navigation is unfinished");
            releaseHomeCompletion.TrySetResult(true);
            await homeFinalNavigation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitAsync(() => dashboard.ActivePage is null && service.LoginDiagnostics.PtaHomeConfirmed);
            Check(service.Connection?.CheckedAtUtc == savedSelectionReport.CheckedAtUtc
                && service.Connection is { CatalogOnly: false, TaskListsCovered: false, Tasks: 6 }
                && result.Text.Contains("本轮同步未完整确认", StringComparison.Ordinal)
                && !result.Text.Contains("已同步 6 条", StringComparison.Ordinal),
                "saving first PTA course selection returns partial results to home with their warning");
        }
        finally { delayHomeCompletion = false; releaseHomeCompletion.TrySetResult(true); }

        async Task<(CourseSelectionPage Page, Button Save, CoreWebView2 Core, ConnectionReport Report)> SaveFreshChoiceBeforeHomeAsync()
        {
        dashboard.ShowDashboard(); flowPage!.Close(); service.Cancel(); await WaitAsync(() => !service.IsRunning);
        ResetFixtureCourseChoice(); DelayNextHomeCompletion();
        dashboard.ShowConnectionPage("pta", CreateFlow);
        var heldCore = await ReadyAsync(flowPage!, service);
        await service.StartAsync(true, afterLogin: true);
        Check(dashboard.ActivePage == flowPage && service.Connection is { CatalogOnly: true, SelectionConfirmed: false },
            "repeated verified PTA login also leaves course selection to the user");
        service.RequestCourseSelection();
        await WaitAsync(() => dashboard.ActivePage is CourseSelectionPage
            && service.Connection is { CatalogOnly: true, SelectionConfirmed: false });
            await homeResourceEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await homeDocumentReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var page = (CourseSelectionPage)dashboard.ActivePage!;
            var save = (Button)page.FindName("RecordingSaveButton");
            await WaitAsync(() => page.FindName("RecordingCourseList") is ListBox { ItemsSource: not null } && save.IsEnabled);
            var catalogAt = service.Connection!.CheckedAtUtc;
            ((Button)page.FindName("RecordingSelectAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !service.IsRunning && save.IsEnabled && store.LoadCourseRecording("pta").SelectionConfirmed
                && service.Connection is { CatalogOnly: false } saved && saved.CheckedAtUtc > catalogAt);
            return (page, save, heldCore, service.Connection!);
        }

        try
        {
            var changed = await SaveFreshChoiceBeforeHomeAsync();
            var persisted = store.LoadCourseRecording("pta");
            var hideHistory = (CheckBox)changed.Page.FindName("RecordingHideHistory");
            var modifiedHistory = !persisted.HideUnselectedTasks;
            Check(dashboard.ActivePage == changed.Page && !changed.Page.HasUnsavedChanges
                && !homeFinalNavigation.Task.IsCompleted && service.IsCurrentConnection(changed.Report),
                "saved first PTA choice restores editable controls while Home completion is still pending");
            hideHistory.IsChecked = modifiedHistory;
            Check(changed.Page.HasUnsavedChanges && store.LoadCourseRecording("pta").HideUnselectedTasks == persisted.HideUnselectedTasks,
                "changing PTA history visibility after the first save remains an unsaved local choice");
            releaseHomeCompletion.TrySetResult(true);
            await homeFinalNavigation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitAsync(() => service.LoginDiagnostics.PtaHomeConfirmed);
            await dashboard.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(dashboard.ActivePage == changed.Page && changed.Page.HasUnsavedChanges && hideHistory.IsChecked == modifiedHistory
                && store.LoadCourseRecording("pta").Revision == persisted.Revision
                && service.Connection?.CheckedAtUtc == changed.Report.CheckedAtUtc,
                "late successful PTA Home completion leaves newer unsaved choices intact on course selection");
            changed.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !service.IsRunning && changed.Save.IsEnabled && dashboard.ActivePage is null
                && service.Connection is { CatalogOnly: false } latest && latest.CheckedAtUtc > changed.Report.CheckedAtUtc);
            Check(store.LoadCourseRecording("pta") is { SelectionConfirmed: true } saved && saved.Revision > persisted.Revision
                && saved.HideUnselectedTasks == modifiedHistory && !changed.Page.HasUnsavedChanges
                && result.Text.Contains("本轮同步未完整确认", StringComparison.Ordinal),
                "saving the newer PTA choice after Home completion returns home with the real partial warning");
        }
        finally { delayHomeCompletion = false; releaseHomeCompletion.TrySetResult(true); }

        try
        {
            var failed = await SaveFreshChoiceBeforeHomeAsync();
            var sessionStatus = (TextBlock)failed.Page.FindName("PlatformSessionStatus");
            bool HasFrameFailureExplanation() => sessionStatus.Text.Contains("PTA003：", StringComparison.Ordinal)
                && sessionStatus.Text.Contains("验证窗口跳转尚未适配", StringComparison.Ordinal)
                && sessionStatus.Text.Contains("重新打开", StringComparison.Ordinal) && sessionStatus.Text.Contains("登录", StringComparison.Ordinal);
            var priorBlocks = service.LoginDiagnostics.Snapshot().BlockedNavigations;
            await failed.Core.ExecuteScriptAsync("(() => { const frame=document.createElement('iframe'); frame.src='https://unconfirmed.example/login'; document.body.appendChild(frame); })()");
            await WaitAsync(() => service.LoginDiagnostics.Snapshot().BlockedNavigations > priorBlocks
                && service.LoginDiagnostics.Snapshot().PageFailure is { Stage: MetadataLoginStage.FrameNavigation, Result: MetadataLoginResult.Blocked }
                && HasFrameFailureExplanation());
            Check(dashboard.ActivePage == failed.Page && !homeFinalNavigation.Task.IsCompleted && HasFrameFailureExplanation(),
                "new unsupported PTA iframe return explains its page failure and login retry on the active course-selection page");
            failed.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !service.IsRunning && failed.Save.IsEnabled
                && service.Connection is { CatalogOnly: false } latest && latest.CheckedAtUtc > failed.Report.CheckedAtUtc);
            Check(dashboard.ActivePage == failed.Page && HasFrameFailureExplanation()
                && service.Connection is { AccountVerified: true, SessionSaved: true }
                && service.LoginDiagnostics.Snapshot().PageFailure is { Stage: MetadataLoginStage.FrameNavigation, Result: MetadataLoginResult.Blocked },
                "saving and resyncing PTA courses cannot overwrite a newer official-page failure explanation");
            releaseHomeCompletion.TrySetResult(true);
            await homeFinalNavigation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await DocumentCompletedAsync(failed.Core, "PTA synthetic student home");
            await dashboard.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(dashboard.ActivePage == failed.Page && !service.LoginDiagnostics.PtaHomeConfirmed && HasFrameFailureExplanation()
                && service.LoginDiagnostics.Snapshot().PageFailure is { Stage: MetadataLoginStage.FrameNavigation, Result: MetadataLoginResult.Blocked },
                "successful late PTA Home completion keeps the newer iframe failure visible and cannot return selection home");
            dashboard.ShowDashboard(); failed.Page.Close(); flowPage!.Close(); service.Cancel();
            await WaitAsync(() => !service.IsRunning);
        }
        finally { delayHomeCompletion = false; releaseHomeCompletion.TrySetResult(true); }
        Check(await service.SaveForExitAsync(), "recovered PTA login can still be saved during graceful exit");
        var previousReport = service.Connection!;
        dashboard.ShowDashboard(); flowPage!.Close(); service.Dispose(); panel.Children.Remove(sessionBrowser);

        // A fresh service has no transient Home navigation evidence, just the isolated saved login and course choice.
        fixture.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/30004/exams", StringComparison.Ordinal)
            ? F.Handler.Reply(request, HttpStatusCode.OK, F.Exam(F.Set("30004"), F.Account, "END", "END", true)) : null;
        var restartedBrowser = new WebView2 { Visibility = Visibility.Collapsed }; panel.Children.Add(restartedBrowser);
        BrowserFixtures.BlockNetwork(restartedBrowser);
        using var restartedService = new MetadataSyncService(MetadataPlatform.Pta, store, root, restartedBrowser, () => fixture, TimeSpan.FromMinutes(30));
        dashboard.AttachMetadataSync(restartedService); restartedService.ConnectionChanged += CheckPublishedPersistence;
        await restartedService.EnsureSessionAsync();
        Check(!restartedService.LoginDiagnostics.PtaHomeConfirmed && restartedService.LoginDiagnostics.AccountValidationRevision == 0
            && !restartedService.IsCurrentConnection(previousReport)
            && new MetadataSessionStore(MetadataPlatform.Pta, Path.Combine(root, ".local", "sessions", "pta.bin"), store)
                .Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Value == fixture.Session),
            "simulated PTA startup keeps saved login without reviving old report or Home navigation qualification");
        var openPlatform = typeof(TaskDashboardWindow).GetMethod("ShowPlatformManager", BindingFlags.NonPublic | BindingFlags.Instance)!;
        openPlatform.Invoke(dashboard, ["pta", false]);
        var ordinarySelection = (CourseSelectionPage)dashboard.ActivePage!;
        await WaitAsync(() => ordinarySelection.FindName("RecordingCourseList") is ListBox { ItemsSource: not null }
            && ((Button)ordinarySelection.FindName("RecordingSaveButton")).IsEnabled);
        var previousSelectionRevision = store.LoadCourseRecording("pta").Revision;
        ((Button)ordinarySelection.FindName("RecordingSelectNoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBox)ordinarySelection.FindName("RecordingCourseSearch")).Text = F.Course.Name;
        ((Button)ordinarySelection.FindName("RecordingSelectAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)ordinarySelection.FindName("RecordingSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => !restartedService.IsRunning && dashboard.ActivePage is null
            && restartedService.Connection is { CatalogOnly: false, TaskListsCovered: true } latest && latest.CheckedAtUtc > previousReport.CheckedAtUtc);
        Check(restartedService.Connection is { AccountVerified: true, SessionSaved: true, Tasks: 5, SelectedCourses: 1 }
            && restartedService.IsCurrentConnection(restartedService.Connection!)
            && !restartedService.LoginDiagnostics.PtaHomeConfirmed
            && !restartedService.LoginDiagnostics.Snapshot().Events.Any(item => item.Stage == MetadataLoginStage.MainNavigation)
            && store.LoadCourseRecording("pta").Revision > previousSelectionRevision
            && result.Text.Contains("已同步 5 条任务", StringComparison.Ordinal),
            "ordinary PTA course selection returns complete saved-session sync to home without this process opening official login");
        Check(await restartedService.SaveForExitAsync(), "ordinary saved-session PTA course sync remains reusable at exit");
        Check(reportsPublishedAfterPersistence, "connection reports become observable only after their catalog or task result has been saved");
        dashboard.ShowDashboard(); ordinarySelection.Close(); restartedService.Dispose(); panel.Children.Remove(restartedBrowser);
        File.WriteAllText(Path.Combine(root, "pta-return-recovery-checks.json"), JsonSerializer.Serialize(new
        {
            outcome = "passed", checks = labels.Count, assertionCount = labels.Count * 2, windows = 1,
            data = "synthetic_only", network = "local_handler_and_webview_fixture_only", officialNetworkAccessed = false,
            productionDataAccessed = false, labels
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static WebView2 Browser(MetadataConnectionPage page) =>
        (WebView2)typeof(MetadataConnectionPage).GetField("browser", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
    private static async Task<CoreWebView2> ReadyAsync(MetadataConnectionPage page, MetadataSyncService service)
    {
        await WaitAsync(() => Browser(page).CoreWebView2?.Source == "https://passport.pintia.cn/login?synthetic=landing"
            && service.LoginDiagnostics.Snapshot().PageFailure is { Result: MetadataLoginResult.Blocked });
        return Browser(page).CoreWebView2;
    }
    private static async Task DocumentCompletedAsync(CoreWebView2 core, string title)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await core.ExecuteScriptAsync("document.readyState === 'complete' && document.title === " + JsonSerializer.Serialize(title)) != "true")
        { if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("PTA fixture document completion"); await Task.Delay(25); }
    }
    private static IEnumerable<string> Text(DependencyObject node)
    {
        if (node is TextBlock block) yield return block.Text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) foreach (var text in Text(VisualTreeHelper.GetChild(node, i))) yield return text;
    }
    private static async Task WaitAsync(Func<bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!ready()) { if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("PTA return recovery fixture"); await Task.Delay(25); }
    }
}
