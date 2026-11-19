using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AccessProbe;

namespace TaskDashboardHarness;

internal static class CourseLifecycleDesktopChecks
{
    public static async Task VerifyAsync(TaskDashboardWindow dashboard, string runRoot, Action<bool, string> check)
    {
        var labels = new List<string>();
        void Check(bool condition, string label)
        {
            check(condition, label); labels.Add(label);
            check(Application.Current.Windows.Count == 1, label + " keeps one native window");
        }
        var store = new TaskStore(Path.Combine(runRoot, "course-lifecycle", "tasks.db"));
        store.SaveSettings(new(false, false) { AutomaticSyncEnabled = false });
        var now = DateTimeOffset.UtcNow;
        var ended = CourseRecordingRules.Xuetang("91001", new("已结课 · 合成课程", "91101", "product-one", "course-one", 3, false));
        var active = CourseRecordingRules.Xuetang("91001", new("正在上课 · 合成课程", "91102", "product-two", "course-two", 1, false));
        var unknown = CourseRecordingRules.Xuetang("91001", new("未知状态 · 合成课程", "91103", "product-three", "course-three", 0, false));
        var catalog = store.ObserveRecordingCourses("xuetang", [ended, active, unknown], true, now);
        store.SaveCourseRecording("xuetang", false, [ended.Course.Key], true, catalog.Revision, now);
        store.ImportXuetangTasks([new("91001", "91101", "product-one", "course-one", "901", "exercise",
            "已结束课程的历史任务", ended.Course.Name, TaskState.Completed, "已完成", "官网状态", "",
            null, now.AddDays(-2), "合成任务截止日期", 100m, null)], now);
        var tasksBefore = JsonSerializer.Serialize(store.LoadTasks()); var applies = 0;
        var page = new CourseSelectionPage(store, "xuetang", _ => Task.CompletedTask, _ => { applies++; return Task.CompletedTask; }, _ => { }, true);
        dashboard.ShowWorkspacePage("synthetic-course-lifecycle:" + Guid.NewGuid().ToString("N"), page);
        var list = (ListBox)page.FindName("RecordingCourseList");
        var filter = (ComboBox)page.FindName("CourseLifecycleFilter");
        var summary = (TextBlock)page.FindName("CourseFilterSummary");
        await WaitAsync(() => list.ItemsSource is not null && ((Button)page.FindName("RecordingSaveButton")).IsEnabled);
        Check(store.LoadTasks().Count == 1 && store.LoadTasks().Single().RecordingCourseKey == ended.Course.Key,
            "course filter fixture includes a retained ended-course history task");
        var choices = ((IEnumerable<CourseSelectionPage.CourseChoice>)list.ItemsSource).ToArray();
        Check(filter.SelectedIndex == 0 && list.Items.Count == 2, "default course view hides only verified ended course");
        Check(summary.Text.Contains("隐藏 1 门", StringComparison.Ordinal), "hidden ended course count explained");
        Check(choices.Single(choice => choice.Key == ended.Course.Key).Selected, "hidden ended course retains selected checkbox");
        Check(list.Items.Cast<CourseSelectionPage.CourseChoice>().Any(choice => choice.Key == unknown.Course.Key), "unknown course stays visible");
        ((Button)page.FindName("RecordingSelectAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(choices.All(choice => choice.Selected), "select visible leaves previously selected hidden course selected");
        ((Button)page.FindName("RecordingSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => applies == 1 && ((Button)page.FindName("RecordingSaveButton")).IsEnabled);
        Check(store.LoadCourseRecording("xuetang").SelectedCourseKeys.Contains(ended.Course.Key), "saving visible choices preserves hidden prior selection");
        Check(JsonSerializer.Serialize(store.LoadTasks()) == tasksBefore, "course filtering and saving retain task history");
        filter.SelectedIndex = 1;
        Check(list.Items.Count == 3, "show all restores ended course");
        filter.SelectedIndex = 2;
        Check(list.Items.Count == 1 && ((CourseSelectionPage.CourseChoice)list.Items[0]).Key == ended.Course.Key, "ended-only finds official ended course");
        Check(((CourseSelectionPage.CourseChoice)list.Items[0]).Detail.Contains("官网已确认结课", StringComparison.Ordinal), "course row provides trustworthy end explanation");
        filter.SelectedIndex = 0;
        ((Button)page.FindName("RecordingSelectNoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        choices = ((IEnumerable<CourseSelectionPage.CourseChoice>)list.ItemsSource).ToArray();
        Check(choices.Single(choice => choice.Key == ended.Course.Key).Selected && choices.Where(choice => choice.Key != ended.Course.Key).All(choice => !choice.Selected),
            "clear current list cannot deselect hidden ended course");
        var search = (Wpf.Ui.Controls.TextBox)page.FindName("RecordingCourseSearch");
        search.Text = "没有该课程";
        Check(list.Items.Count == 0 && summary.Text.Contains("显示 0 / 3", StringComparison.Ordinal), "course text search combines with expiry filter");
        search.Clear();
        Check(list.Items.Count == 2, "clearing search retains ended-course filter");
        typeof(TaskDashboardWindow).GetMethod("ShowTasks", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dashboard, [1]);
        Check(dashboard.ActivePage is null, "course page returns to task homepage within same window");
        File.WriteAllText(Path.Combine(runRoot, "course-lifecycle-ui-checks.json"), JsonSerializer.Serialize(new
        { outcome = "passed", scenarios = labels.Count, labels, syntheticOnly = true, windows = 1 }));
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Course lifecycle UI did not reach expected state.");
            await Task.Delay(40);
        }
    }
}
