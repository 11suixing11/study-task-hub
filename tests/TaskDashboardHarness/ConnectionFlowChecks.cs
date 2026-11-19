using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AccessProbe;
using Microsoft.Web.WebView2.Wpf;
using MoocSyncHarness;

namespace TaskDashboardHarness;

internal static class ConnectionFlowChecks
{
    public static async Task VerifyAsync(TaskDashboardWindow dashboard, TaskStore store, string runRoot, Action<bool, string> check)
    {
        var labels = new List<string>();
        void Check(bool value, string label)
        {
            check(value, label); labels.Add(label);
            check(Application.Current.Windows.Count == 1 && Application.Current.Windows[0] == dashboard,
                label + " keeps the existing main window");
        }
        dashboard.ShowDashboard();
        var root = Path.Combine(runRoot, "single-window-flow"); Directory.CreateDirectory(root);
        var initialTasks = store.LoadTasks().Count;
        var fixture = new Fixtures();
        var browser = (WebView2)dashboard.FindName("MoocSessionBrowser"); fixture.BlockBrowser(browser);
        using var service = new MoocSyncService(store, root, browser, fixture.CreateHandler, TimeSpan.FromMinutes(30));
        dashboard.AttachMoocSync(service);
        MoocConnectionPage? connection = null;
        var factories = 0;
        WorkspacePage CreateConnection()
        {
            factories++;
            connection = new MoocConnectionPage(service, dashboard.ShowDashboard);
            var loginBrowser = ((Panel)connection.Content).Children.OfType<WebView2>().Single();
            fixture.BlockBrowser(loginBrowser);
            return connection;
        }
        dashboard.ShowConnectionPage("mooc", CreateConnection);
        await WaitAsync(() => browser.CoreWebView2 is not null && connection!.IsLoaded);
        var officialBrowser = ((Panel)connection!.Content).Children.OfType<WebView2>().Single();
        await WaitAsync(() => officialBrowser.CoreWebView2?.Source == MoocPlatform.Login.AbsoluteUri);
        var documentDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await officialBrowser.CoreWebView2.ExecuteScriptAsync("document.readyState === 'complete' && document.title === 'MOOC synthetic official page'") != "true")
        { if (DateTimeOffset.UtcNow > documentDeadline) throw new TimeoutException("local official login document"); await Task.Delay(25); }
        Fixtures.SetCookie(browser, fixture.ExpectedToken);
        await service.StartAsync(true, afterLogin: true);
        Check(dashboard.ActivePage == connection && service.Connection is { CatalogOnly: true, SelectionConfirmed: false },
            "verified first login stays on the official page and leaves course selection to the user");
        var choose = Visuals<Button>(connection).First(item => Equals(item.Content, "确认课程并同步") && item.IsVisible);
        choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => dashboard.ActivePage is CourseSelectionPage);
        var selection = (CourseSelectionPage)dashboard.ActivePage!;
        Check(!connection.IsPageActive && !connection.IsClosed && dashboard.ActivePageKey == "platform:mooc",
            "user-requested course selection replaces the official page in the main workspace");
        await WaitAsync(() => selection.FindName("RecordingCourseList") is ListBox { ItemsSource: not null }
            && ((Button)selection.FindName("RecordingSaveButton")).IsEnabled);
        ((Button)selection.FindName("RecordingSelectAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)selection.FindName("RecordingSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => !service.IsRunning && dashboard.ActivePage is null);
        var recordingAfterSave = store.LoadCourseRecording("mooc");
        Check(store.LoadTasks().Count == initialTasks + 8 && recordingAfterSave.SelectionConfirmed
            && recordingAfterSave.Courses.Where(course => course.Available).All(course => recordingAfterSave.SelectedCourseKeys.Contains(course.Key)),
            "first selection saves exact courses, collects tasks and returns to the unified task workspace");
        Check(service.Connection is { CatalogOnly: false, TaskListsCovered: true, SessionSaved: true }
            && new MoocSessionStore(Path.Combine(root, "mooc-check.bin"), store).Load(DateTimeOffset.UtcNow).Count > 0,
            "successful flow saves the protected login before returning home");
        var profile = officialBrowser.CoreWebView2.Environment.UserDataFolder;
        dashboard.ShowConnectionPage("mooc", CreateConnection);
        Check(factories == 1 && dashboard.ActivePage == connection && officialBrowser.CoreWebView2.Environment.UserDataFolder == profile,
            "reopening an established connection reuses its official browser and profile");
        await officialBrowser.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('single-window-synthetic','kept');");
        var officialSource = officialBrowser.CoreWebView2.Source;
        dashboard.ShowDashboard(); dashboard.ShowConnectionPage("mooc", CreateConnection);
        Check(officialBrowser.CoreWebView2.Source == officialSource
            && await officialBrowser.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('single-window-synthetic') === 'kept'") == "true",
            "internal navigation retains official-origin local storage");
        var before = fixture.Requests;
        await service.StartAsync(true, afterLogin: true);
        await WaitAsync(() => !service.IsRunning && dashboard.ActivePage is null);
        Check(fixture.Requests >= before + 8 && store.LoadTasks().Count == initialTasks + 8 && service.Connection is { CatalogOnly: false, TaskListsCovered: true },
            "known selection automatically follows login confirmation with task sync and home navigation");

        dashboard.ShowConnectionPage("mooc", CreateConnection); fixture.UnknownNewExams = true;
        await service.StartAsync(true, afterLogin: true);
        await WaitAsync(() => !service.IsRunning && service.Connection is { CatalogOnly: false, TaskListsCovered: false });
        Check(dashboard.ActivePage == connection && service.Connection!.ProblemCodes.Count > 0,
            "partial task sync stays on the connection page with its recorded failure reason");
        fixture.UnknownNewExams = false;
        await service.StartAsync(true, afterLogin: true);
        await WaitAsync(() => !service.IsRunning && dashboard.ActivePage is null);
        Check(store.LoadTasks().Count == initialTasks + 8, "retry completes in the same flow without duplicating tasks");

        dashboard.ShowConnectionPage("mooc", CreateConnection);
        fixture.PauseTerm();
        await service.StartAsync(true, afterLogin: true);
        await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var elsewhere = new WorkspaceContentPage("另一流程", new TextBlock { Text = "合成页面" });
        dashboard.ShowWorkspacePage("flow-elsewhere", elsewhere);
        fixture.Release!.TrySetResult(true);
        await WaitAsync(() => !service.IsRunning); await Task.Delay(100); fixture.ResetPause();
        Check(dashboard.ActivePage == elsewhere && store.LoadTasks().Count == initialTasks + 8,
            "pending login task sync finishes without taking over the workflow selected afterward");
        dashboard.NavigateBack();
        Check(dashboard.ActivePage == connection, "back resumes the existing official page after another workflow");
        dashboard.ShowDashboard(); connection.Close();
        Check(await service.SaveForExitAsync() && service.SessionPersistenceOutcome == "saved",
            "closing the business page still saves the session during graceful service exit");
        var recording = store.LoadCourseRecording("mooc");
        store.SaveCourseRecording("mooc", true, [], true, recording.Revision, DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(runRoot, "single-window-connection-flow.json"), JsonSerializer.Serialize(new
        {
            outcome = "passed", checks = labels.Count, windows = 1, tasks = store.LoadTasks().Count,
            data = "synthetic_only", network = "local_handler_and_webview_fixture_only", officialNetworkAccessed = false,
            productionDataAccessed = false, labels
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task WaitAsync(Func<bool> ready)
    { var deadline = DateTimeOffset.UtcNow.AddSeconds(15); while (!ready()) { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("single-window connection flow"); await Task.Delay(25); } }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Visuals<T>(System.Windows.Media.VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
