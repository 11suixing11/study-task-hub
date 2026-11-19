using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AccessProbe;

namespace TaskDashboardHarness;

internal static class WorkspaceNavigationChecks
{
    private sealed class TestPage(string title) : WorkspacePage
    {
        public int Activations { get; private set; }
        public int Deactivations { get; private set; }
        public int Disposals { get; private set; }
        public TestPage() : this("合成导航页") { }
        public void GoBack() => RequestBack();
        public void Invalidate() => CanReuse = false;
        public void OpenChild(WorkspacePage child) => ShowChildPage(child);
        public void ObserveLifetime()
        {
            Title = title; Content = new TextBlock { Text = title };
            Activated += (_, _) => Activations++;
            Deactivated += (_, _) => Deactivations++;
            Closed += (_, _) => Disposals++;
        }
    }

    public static async Task VerifyAsync(TaskDashboardWindow dashboard, TaskStore store, string runRoot, Action<bool, string> check)
    {
        var labels = new List<string>();
        void Check(bool value, string label)
        {
            check(value, label); labels.Add(label);
            check(Application.Current.Windows.Count == 1 && Application.Current.Windows[0] == dashboard,
                label + " keeps exactly one native window");
        }
        var pages = new List<TestPage>();
        TestPage Page(string title)
        {
            var page = new TestPage(title); page.ObserveLifetime(); pages.Add(page); return page;
        }
        void Click(string name) => ((Button)dashboard.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Report(ConnectionReport report) => typeof(TaskDashboardWindow).GetMethod("ObserveConnectionReport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dashboard, [store.SaveConnectionReport(report)]);
        ConnectionReport Ready(string id) => new(id)
        {
            AccountVerified = true, CatalogCovered = true, SelectionConfirmed = true,
            Courses = 1, SelectedCourses = 1, TaskListsCovered = true, Tasks = 3
        };
        dashboard.ShowDashboard();
        var details = (FrameworkElement)dashboard.FindName("TaskDetailPanel");
        if (details.Visibility == Visibility.Visible) Click("DetailsToggleButton");
        var connection = Page("合成官方登录");
        dashboard.ShowConnectionPage("chaoxing", () => connection);
        Check(dashboard.ActivePage == connection && connection.IsPageActive && details.Visibility == Visibility.Collapsed,
            "connection opens in the main workspace and hides task details");
        var child = Page("合成作业详情"); connection.OpenChild(child);
        Check(dashboard.ActivePage == child && !connection.IsPageActive && child.IsPageActive,
            "official work details navigate within the same host");
        var lateChild = Page("晚返回的详情"); connection.OpenChild(lateChild);
        Check(lateChild.IsClosed && dashboard.ActivePage == child, "inactive page cannot open a late child result");
        child.GoBack();
        Check(child.IsClosed && connection.IsPageActive && dashboard.ActivePage == connection,
            "back disposes the temporary page and resumes the cached login page");
        dashboard.ShowDashboard();
        Check(dashboard.ActivePage is null && !connection.IsClosed && !connection.IsPageActive && details.Visibility == Visibility.Collapsed,
            "home retains login page and the user's collapsed task-details preference");
        var factories = 0;
        dashboard.ShowConnectionPage("chaoxing", () => { factories++; return Page("不应重建的登录"); });
        Check(factories == 0 && dashboard.ActivePage == connection && connection.Activations == 3,
            "reopening a platform reuses its page instead of reconstructing its browser");

        var blocked = new[]
        {
            Ready("chaoxing") with { SessionAvailable = false },
            Ready("chaoxing") with { LoginRequired = true, ProblemOutcomes = ["login_required"] },
            Ready("chaoxing") with { AccountVerified = false, ProblemOutcomes = ["verification_required"] },
            Ready("chaoxing") with { SessionSaved = false },
            Ready("chaoxing") with { CatalogCovered = false },
            Ready("chaoxing") with { TaskListsCovered = false, ProblemOutcomes = ["unrecognized_response"] },
            Ready("chaoxing") with { CatalogOnly = true, ProblemCodes = ["SYN001"] },
            Ready("mooc"),
            Ready("chaoxing") with { CheckedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) }
        };
        foreach (var report in blocked)
        {
            Report(report); await FlushAsync(dashboard);
            Check(dashboard.ActivePage == connection, "unverified, incomplete, other-platform or stale report preserves the current page");
        }
        Report(Ready("chaoxing"));
        var elsewhere = Page("另一平台的操作"); dashboard.ShowWorkspacePage("navigation-elsewhere", elsewhere);
        await FlushAsync(dashboard);
        Check(dashboard.ActivePage == elsewhere, "queued success cannot take over after the user leaves its connection flow");
        dashboard.NavigateBack();
        Check(dashboard.ActivePage == connection && connection.IsPageActive && elsewhere.IsClosed,
            "back from another workflow restores the login page");
        Report(Ready("chaoxing")); await FlushAsync(dashboard);
        Check(dashboard.ActivePage is null && ((TextBlock)dashboard.FindName("ResultText")).Text.Contains("已同步 3 条任务"),
            "fresh complete result after back returns to tasks with a completion message");
        Check(details.Visibility == Visibility.Collapsed, "automatic return keeps the task-details preference");

        dashboard.ShowConnectionPage("chaoxing", () => connection);
        Report(Ready("chaoxing") with { CatalogOnly = true, SelectionConfirmed = false });
        await FlushAsync(dashboard);
        Check(dashboard.ActivePage == connection,
            "a verified catalog without selection stays on the connection page; opening course selection stays with the user");
        dashboard.NavigateBack();
        Check(dashboard.ActivePage is null && !connection.IsClosed,
            "back from the unselected connection page returns to tasks without leaving a login window");
        dashboard.ShowConnectionPage("chaoxing", () => connection);
        Report(Ready("chaoxing") with { CatalogOnly = true, Courses = 0, SelectedCourses = 0, Tasks = 0 });
        await FlushAsync(dashboard);
        Check(dashboard.ActivePage == connection,
            "verified empty official catalog stays in the connection workflow so the user can retry");
        dashboard.ShowDashboard();

        var messages = Page("确认前的登录页"); dashboard.ShowWorkspacePage("confirmation-origin", messages, retained: true);
        var confirmation = dashboard.ConfirmAsync("合成确认", "仅用于导航回归。");
        await FlushAsync(dashboard);
        Check(dashboard.ActivePage?.Title == "合成确认", "confirmation is an internal page");
        Visuals<Button>(dashboard.ActivePage!).Single(button => Equals(button.Content, "确认")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(await confirmation && dashboard.ActivePage == messages, "accept restores the requesting page before returning success");
        confirmation = dashboard.ConfirmAsync("合成取消", "返回即取消。"); dashboard.NavigateBack();
        Check(!await confirmation && dashboard.ActivePage == messages, "back cancels an internal confirmation");
        confirmation = dashboard.ConfirmAsync("离开确认", "切页即取消。"); dashboard.ShowDashboard();
        Check(!await confirmation && dashboard.ActivePage is null, "home cancels pending confirmation without continuing the requested action");

        var count = store.LoadTasks().Count;
        Click("AddTaskButton");
        var editor = (TaskEditorPage)dashboard.ActivePage!;
        ((Button)editor.FindName("TaskEditorSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(dashboard.ActivePage == editor && ((TextBlock)editor.FindName("TaskEditorError")).Text.Length > 0 && store.LoadTasks().Count == count,
            "invalid editor save keeps the page and shows a field error");
        ((TextBox)editor.FindName("TaskEditorTitle")).Text = "单窗口合成任务";
        ((ComboBox)editor.FindName("TaskEditorPlatform")).SelectedValue = "pta";
        ((Button)editor.FindName("TaskEditorSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushAsync(dashboard);
        Check(dashboard.ActivePage is null && editor.IsClosed && store.LoadTasks().Count == count + 1,
            "valid editor save persists and returns to tasks");
        var created = store.LoadTasks().Single(task => task.Title == "单窗口合成任务");
        await WaitAsync(() => ((DataGrid)dashboard.FindName("TaskTable")).Items.OfType<TaskDashboardWindow.TaskDisplayRow>().Any(row => row.Task.Id == created.Id));
        var table = (DataGrid)dashboard.FindName("TaskTable");
        table.SelectedItem = table.Items.OfType<TaskDashboardWindow.TaskDisplayRow>().Single(row => row.Task.Id == created.Id);
        Click("EditButton"); editor = (TaskEditorPage)dashboard.ActivePage!;
        ((TextBox)editor.FindName("TaskEditorTitle")).Text = "未保存的标题";
        ((Button)editor.FindName("TaskEditorCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(dashboard.ActivePage is null && store.Get(created.Id)!.Title == "单窗口合成任务",
            "editor cancel returns to tasks and preserves saved content");
        foreach (var (button, title) in new[] { ("OpenSettingsButton", "设置与平台状态"), ("SyncDetailsButton", "各平台同步详情") })
        {
            Click(button); await FlushAsync(dashboard);
            var page = dashboard.ActivePage!;
            Check(page.Title == title, title + " opens inside the main workspace");
            dashboard.NavigateBack();
            Check(dashboard.ActivePage is null && page.IsClosed, title + " back releases the temporary page");
        }
        typeof(TaskDashboardWindow).GetMethod("ShowReminderHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dashboard, []);
        Check(dashboard.ActivePage?.Title == "提醒记录", "reminder history opens inside the main workspace");
        dashboard.NavigateBack();
        var failed = Page("失效的浏览环境");
        dashboard.ShowConnectionPage("yuketang", () => failed); failed.Invalidate();
        var recovered = Page("重建的浏览环境");
        dashboard.ShowConnectionPage("yuketang", () => recovered);
        Check(failed.IsClosed && recovered.IsPageActive && dashboard.ActivePage == recovered,
            "reopening a failed environment recreates its page inside the existing main window");
        dashboard.ShowDashboard();
        store.Delete(created.Id, store.Get(created.Id)!.Revision);
        await FlushAsync(dashboard);
        dashboard.ShowConnectionPage("chaoxing", () => connection);
        dashboard.DisposeWorkspaceForExit();
        Report(Ready("chaoxing")); await FlushAsync(dashboard);
        Check(dashboard.ActivePage is null && pages.All(page => page.IsClosed && page.Disposals == 1),
            "exit disposes each cached page exactly once and ignores pending results");
        File.WriteAllText(Path.Combine(runRoot, "single-window-navigation.json"), JsonSerializer.Serialize(new
        {
            outcome = "passed", checks = labels.Count, windows = 1, data = "synthetic_only",
            officialNetworkAccessed = false, productionDataAccessed = false, labels
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task FlushAsync(TaskDashboardWindow dashboard)
    { await dashboard.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); await Task.Delay(30); }
    private static async Task WaitAsync(Func<bool> ready)
    { var end = DateTimeOffset.UtcNow.AddSeconds(10); while (!ready()) { if (DateTimeOffset.UtcNow > end) throw new TimeoutException(); await Task.Delay(25); } }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
