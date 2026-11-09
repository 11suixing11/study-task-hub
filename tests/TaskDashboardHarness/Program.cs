using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using AccessProbe;

namespace TaskDashboardHarness;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var verify = args.Contains("--verify", StringComparer.Ordinal);
        var syntheticLinks = args.Contains("--synthetic-links", StringComparer.Ordinal);
        if (syntheticLinks && !verify) return 2;
        var options = args.Where(arg => arg is not ("--verify" or "--synthetic-links")).ToArray();
        if (options.Length is not (2 or 4) || options[0] != "--project-root"
            || options.Length == 4 && options[2] != "--run-root") return 2;
        var root = Path.GetFullPath(options[1]);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var harnessRoot = Path.Combine(root, ".local", "task-dashboard-harness");
        Directory.CreateDirectory(harnessRoot);
        var runRoot = options.Length == 4
            ? Path.GetFullPath(options[3]) : Path.Combine(harnessRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        if (!runRoot.StartsWith(harnessRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(Path.Combine(harnessRoot, "latest-run.txt"), runRoot);
        Environment.SetEnvironmentVariable("TEMP", runRoot);
        Environment.SetEnvironmentVariable("TMP", runRoot);
        var database = Path.Combine(runRoot, "tasks.db");
        if (syntheticLinks && File.Exists(database)) return 2;
        var store = FixtureTaskStore.Create(database, syntheticLinks ? new SyntheticLinkProtector() : null);
        if (store.LoadTasks().Count == 0)
        {
            var now = DateTimeOffset.UtcNow;
            var draft = new TaskDraft("离线验收 · 临期任务", "mooc", "演示课程", DeadlineKind.Known, null,
                now.AddHours(8), "https://example.invalid/synthetic-task", null, "", "本清单全部使用合成数据", null);
            store.Create(draft, now);
            store.Create(draft with { Title = "离线验收 · 已逾期任务", DeadlineUtc = now.AddHours(-4) }, now);
            store.Create(draft with { Title = "离线验收 · 未知时间", DeadlineKind = DeadlineKind.Unknown, DeadlineUtc = null }, now);
            const string link = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=202&cpi=303&workId=404&answerId=505&enc=SYNTHETIC-ONLY";
            store.ImportWorks([new("离线验收 · 平台已完成", "已完成", "剩余48小时", WorkListParser.ParseTaskLocation(link))], "合成学习通课程", now);
            foreach (var (title, platform, course, hours) in new[] {
                ("栈与队列 · 题目集", "pta", "数据结构", 10),
                ("Unit 4 · 阅读与词汇", "ucampus", "大学英语", 6),
                ("极限与连续 · 课后练习", "chaoxing", "高等数学", 28),
                ("第六讲 · 随堂测验", "yuketang", "计算机网络", 48),
                ("模型与推理 · 课程测验", "zhihuishu", "人工智能导论", 72),
                ("矩阵运算 · 单元测试", "mooc", "线性代数", 96),
                ("模块三 · 课后复习", "xuetang", "程序设计基础", 120),
                ("导论 · 课堂测试", "changjiang", "大学物理", 144) })
                store.Create(draft with { Title = title, PlatformId = platform, CourseName = course,
                    DeadlineUtc = now.AddHours(hours), Notes = "离线合成任务，用于验证界面。可在右侧查看状态、截止时间和备注。" }, now);
            store.SaveSettings(new(false, true) { AutomaticSyncEnabled = false });
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        TaskDashboardWindow? dashboard = null;
        ReminderService? reminders = null;
        var exiting = false;
        void Exit()
        {
            if (exiting) return; exiting = true;
            reminders?.Dispose(); dashboard?.CloseForExit(); app.Shutdown();
        }
        var opened = new List<string>();
        var passed = !verify;
        dashboard = new TaskDashboardWindow(store, runRoot,
            () => MessageBox.Show(dashboard!, "采集流程请使用独立 WorkPageHarness 验证。本窗口的所有任务均为合成数据。", "离线测试提示"), Exit, opened.Add)
        { Title = "聚合平台 · 离线桌面验收" };
        if (!verify)
        {
            reminders = new ReminderService(store, () => dashboard.ShowDashboard(), Exit);
            dashboard.AttachReminders(reminders);
        }
        if (verify) dashboard.Loaded += (_, _) => dashboard.Dispatcher.BeginInvoke(new Action(async () =>
        {
            passed = await VerifyAsync(dashboard, store, opened, runRoot, syntheticLinks);
            Exit();
        }));
        dashboard.ShowSuccess("离线合成清单；任务保存在独立测试目录，可重启本程序验证。");
        app.MainWindow = dashboard;
        app.Run(dashboard);
        reminders?.Dispose(); return passed ? 0 : 1;
    }

    private static async Task<bool> VerifyAsync(TaskDashboardWindow dashboard, TaskStore store, List<string> opened, string runRoot, bool syntheticLinks)
    {
        var checks = new List<string>();
        var heights = new Dictionary<string, double>();
        var layouts = new Dictionary<string, object>();
        var passed = false;
        string? failure = null;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        T Find<T>(string name) where T : FrameworkElement => (T)dashboard.FindName(name);
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            var table = Find<DataGrid>("TaskTable");
            await WaitAsync(() => table.Items.Count == store.LoadRecordedTasks().Count && table.Items.Count >= 4);
            Check(Find<Button>("SyncButton") is Wpf.Ui.Controls.Button { Appearance: Wpf.Ui.Controls.ControlAppearance.Transparent, Icon: not null }
                && System.Windows.Automation.AutomationProperties.GetName(Find<Button>("SyncButton")) == "同步所有平台",
                "toolbar sync action loads its icon and an accessible name");
            Check(Find<TextBox>("SearchBox") is Wpf.Ui.Controls.TextBox { PlaceholderText.Length: > 0, Icon: not null },
                "search loads the adapted input template and placeholder");
            Check(Find<Button>("SyncButton").Template is not null && Find<TextBox>("SearchBox").Template is not null
                && Find<ComboBox>("StateFilter").Template is not null && table.Template is not null,
                "WPF UI and adapted control templates resolve at runtime");
            Check(Find<ComboBox>("StateFilter").SelectedIndex == 1, "initial view retains all task states");
            Capture(dashboard, runRoot, "windows-desktop.png");
            foreach (var (width, height, label) in new[] { (1440d, 960d, "desktop"), (1180d, 800d, "minimum") })
            {
                dashboard.Width = width; dashboard.Height = height;
                Find<Expander>("PlatformSyncExpander").IsExpanded = false;
                await Task.Delay(100); dashboard.UpdateLayout();
                var content = (FrameworkElement)dashboard.Content;
                foreach (var name in new[] { "ChaoxingConnectionButton", "PtaConnectionButton", "AggregationTitleText",
                    "AddTaskButton", "ChooseRecordingCoursesButton", "ShowAllTasksButton", "SearchBox", "StateFilter",
                    "PlatformFilter", "SortFilter", "TaskTable", "TaskDetailPanel", "GoButton", "CompleteButton", "EditButton", "MoreTaskActionsButton" })
                {
                    var element = Find<FrameworkElement>(name);
                    var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                    Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= content.ActualWidth + 1
                        && bounds.Bottom <= content.ActualHeight + 1 && element.ActualWidth > 0 && element.ActualHeight > 0,
                        label + " keeps " + name + " visible");
                }
                var tableBounds = table.TransformToAncestor(content).TransformBounds(new Rect(table.RenderSize));
                var detail = Find<FrameworkElement>("TaskDetailPanel");
                var detailBounds = detail.TransformToAncestor(content).TransformBounds(new Rect(detail.RenderSize));
                heights[label] = table.ActualHeight;
                layouts[label] = new { contentWidth = content.ActualWidth, contentHeight = content.ActualHeight,
                    titleHeight = Find<TextBlock>("AggregationTitleText").ActualHeight,
                    summaryHeight = Find<TextBlock>("AggregationSummaryText").ActualHeight,
                    metricsHeight = Find<Button>("ActiveTasksButton").ActualHeight,
                    syncHeight = Find<Expander>("PlatformSyncExpander").ActualHeight,
                    searchHeight = Find<TextBox>("SearchBox").ActualHeight,
                    stateHeight = Find<ComboBox>("StateFilter").ActualHeight,
                    filterCountHeight = Find<TextBlock>("FilterCountText").ActualHeight,
                    tableHeight = table.ActualHeight, tableWidth = table.ActualWidth };
                Capture(dashboard, runRoot, "windows-" + label + ".png");
                Check(tableBounds.Right <= detailBounds.Left, label + " task list and detail pane do not overlap");
                Check(table.ActualHeight >= 230, label + " leaves usable task list height");
                Check(table.Columns.Sum(column => column.ActualWidth) <= table.ActualWidth, label + " task columns fit without horizontal scrolling");
                heights[label] = table.ActualHeight;
                Find<Expander>("PlatformSyncExpander").IsExpanded = true;
                Find<Expander>("SelectedDetailsExpander").IsExpanded = true;
                await Task.Delay(100); dashboard.UpdateLayout();
                Check(table.ActualHeight >= 100, label + " expanded evidence preserves task rows");
                heights[label + "Expanded"] = table.ActualHeight;
                Capture(dashboard, runRoot, "windows-" + label + "-expanded.png");
            }
            dashboard.Width = 1100; dashboard.Height = 700;
            Find<Expander>("PlatformSyncExpander").IsExpanded = true;
            await Task.Delay(100); dashboard.UpdateLayout();
            Check(table.ActualHeight >= 250 && table.Columns.Sum(column => column.ActualWidth) <= table.ActualWidth,
                "short window retains usable task rows and columns");
            var shortContent = (FrameworkElement)dashboard.Content;
            foreach (var name in new[] { "SearchBox", "StateFilter", "PlatformFilter", "SortFilter", "ShowAllTasksButton", "GoButton", "CompleteButton" })
            {
                var element = Find<FrameworkElement>(name);
                var bounds = element.TransformToAncestor(shortContent).TransformBounds(new Rect(element.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= shortContent.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= shortContent.ActualHeight
                    && element.ActualWidth > 0 && element.ActualHeight > 0, "short window keeps " + name + " accessible");
            }
            Find<FrameworkElement>("PtaConnectionButton").BringIntoView();
            await Task.Delay(100); dashboard.UpdateLayout();
            var ptaBounds = Find<FrameworkElement>("PtaConnectionButton").TransformToAncestor(shortContent).TransformBounds(new Rect(Find<FrameworkElement>("PtaConnectionButton").RenderSize));
            Check(ptaBounds.Top > 0 && ptaBounds.Bottom < shortContent.ActualHeight, "short sidebar scrolls to the last platform");
            Find<FrameworkElement>("WorkspaceHomeButton").BringIntoView();
            Capture(dashboard, runRoot, "windows-short.png");
            dashboard.Width = 1180; dashboard.Height = 800; await Task.Delay(100); dashboard.UpdateLayout();
            var selectedBeforeToggle = (table.SelectedItem as TaskDashboardWindow.TaskDisplayRow)?.Task.Id;
            var itemsBeforeToggle = table.Items.Count;
            var widthBeforeToggle = table.ActualWidth;
            Click("SidebarToggleButton"); dashboard.UpdateLayout();
            Check(Find<FrameworkElement>("SidebarPanel").Visibility == Visibility.Collapsed && table.ActualWidth > widthBeforeToggle
                && table.Items.Count == itemsBeforeToggle && (table.SelectedItem as TaskDashboardWindow.TaskDisplayRow)?.Task.Id == selectedBeforeToggle,
                "hiding sidebar expands task list and retains selection and filters");
            var sidebarShortcut = dashboard.InputBindings.OfType<System.Windows.Input.KeyBinding>().Single(binding => binding.Key == System.Windows.Input.Key.L
                && binding.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift));
            var sidebarCommand = (System.Windows.Input.RoutedCommand)sidebarShortcut.Command;
            Check(sidebarCommand.CanExecute(null, dashboard), "sidebar keyboard command is available");
            sidebarCommand.Execute(null, dashboard); dashboard.UpdateLayout();
            Check(Find<FrameworkElement>("SidebarPanel").Visibility == Visibility.Visible
                && System.Windows.Automation.AutomationProperties.GetName(Find<Button>("SidebarToggleButton")) == "隐藏侧栏",
                "sidebar keyboard command restores the pane and accessible action name");
            Click("MoreTaskActionsButton"); Click("DetailsToggleButton"); dashboard.UpdateLayout();
            Check(Find<FrameworkElement>("TaskDetailPanel").Visibility == Visibility.Collapsed
                && !((System.Windows.Controls.Primitives.Popup)dashboard.FindName("TaskActionsPopup")).IsOpen
                && table.Items.Count == itemsBeforeToggle && (table.SelectedItem as TaskDashboardWindow.TaskDisplayRow)?.Task.Id == selectedBeforeToggle,
                "hiding details closes the task menu and preserves selection");
            dashboard.Width = 1440; dashboard.Height = 960; await Task.Delay(100); dashboard.UpdateLayout();
            Check(Find<FrameworkElement>("TaskDetailPanel").Visibility == Visibility.Collapsed, "resizing keeps hidden details collapsed");
            Click("DetailsToggleButton"); dashboard.UpdateLayout();
            Check(Find<FrameworkElement>("TaskDetailPanel").Visibility == Visibility.Visible
                && Find<Button>("CompleteButton").ActualWidth > 0, "detail pane restores with task actions");
            foreach (var (button, state, expectedTitle) in new[] { ("ActiveTasksButton", 0, "待办"), ("SoonTasksButton", 7, "即将到期"),
                ("OverdueTasksButton", 6, "已逾期"), ("CompletedTasksButton", 3, "已完成") })
            {
                Find<TextBox>("SearchBox").Text = "NO-MATCH-CLASSIFICATION";
                Click(button);
                Check(Find<ComboBox>("StateFilter").SelectedIndex == state && Find<ComboBox>("PlatformFilter").SelectedIndex == 0
                    && Find<TextBox>("SearchBox").Text.Length == 0 && Find<TextBlock>("AggregationTitleText").Text == expectedTitle
                    && Equals(Find<Button>(button).Tag, "selected")
                    && new[] { "WorkspaceHomeButton", "ActiveTasksButton", "SoonTasksButton", "OverdueTasksButton", "CompletedTasksButton" }
                        .Count(name => Equals(Find<Button>(name).Tag, "selected")) == 1,
                    button + " clears search and selects one matching sidebar category");
            }
            Click("ShowAllTasksButton");
            await WaitAsync(() => table.Items.Count == store.LoadRecordedTasks().Count);
            Check(Find<TextBlock>("ToolbarCountText").Text == table.Items.Count + " 项", "toolbar count matches the current task list");
            var searchShortcut = dashboard.InputBindings.OfType<System.Windows.Input.KeyBinding>().Single(binding => binding.Key == System.Windows.Input.Key.F
                && binding.Modifiers == System.Windows.Input.ModifierKeys.Control);
            ((System.Windows.Input.RoutedCommand)searchShortcut.Command).Execute(null, dashboard);
            Check(Find<TextBox>("SearchBox").IsKeyboardFocusWithin, "search shortcut focuses the input");
            Find<TextBox>("SearchBox").Text = "NO-MATCH-CLEAR";
            var search = (Wpf.Ui.Controls.TextBox)Find<TextBox>("SearchBox"); search.ApplyTemplate();
            var clearButton = (Button)search.Template.FindName("ClearButton", search);
            clearButton.Command.Execute(null);
            Check(search.Text.Length == 0 && table.Items.Count == store.LoadRecordedTasks().Count, "adapted search clear button restores the task list");
            dashboard.Width = 1180; dashboard.Height = 800; await Task.Delay(100); dashboard.UpdateLayout();
            var row = table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().First(item => item.Task.Source == TaskSource.Chaoxing);
            table.SelectedItem = row; table.ScrollIntoView(row); dashboard.UpdateLayout();
            Check(Find<TextBlock>("SelectedTaskTitleText").Text == row.Title && Find<TextBlock>("SelectedPlatformStateText").Text == row.Task.PlatformStatusText,
                "detail pane matches selected task and platform original state");
            Check(Find<TextBlock>("SelectedDetails").Text.Contains(row.Task.DisplayedTimeText, StringComparison.Ordinal), "complete evidence retains original platform time");
            Check(Find<TextBlock>("SelectedDeadlineText").Text == TaskRules.DeadlineName(row.Task), "detail date retains uncertainty markers");
            Check(row.ShortDeadline.StartsWith("约 ", StringComparison.Ordinal) && row.DeadlineContext.Contains("估算", StringComparison.Ordinal), "condensed date labels an estimated deadline");
            var original = row.Task.PlatformStatusText;
            Click("MoreTaskActionsButton");
            Check(((System.Windows.Controls.Primitives.Popup)dashboard.FindName("TaskActionsPopup")).IsOpen, "more actions popup opens from detail pane");
            Click("RestoreButton");
            await WaitAsync(() => store.Get(row.Task.Id)?.State == TaskState.Pending);
            await WaitAsync(() => (table.SelectedItem as TaskDashboardWindow.TaskDisplayRow)?.Task.State == TaskState.Pending);
            Check(Find<TextBlock>("SelectedPlatformStateText").Text == original && Find<TextBlock>("SelectedSourceText").Text.Contains("手动状态", StringComparison.Ordinal),
                "local override retains platform evidence in the detail pane");
            Click("CompleteButton");
            await WaitAsync(() => (table.SelectedItem as TaskDashboardWindow.TaskDisplayRow)?.Task.State == TaskState.Completed);
            Check(!Find<Button>("CompleteButton").IsEnabled, "completed task cannot be completed twice");
            var manual = table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().First(item => item.Task.Source == TaskSource.Manual);
            table.SelectedItem = manual; Click("GoButton");
            await WaitAsync(() => opened.Count == 1);
            Check(opened[0] == manual.Task.Link, "official action targets the selected task without launching a browser");
            Find<TextBox>("SearchBox").Text = "NO-MATCH-UI-VERIFY";
            Check(table.Items.Count == 0 && Find<TextBlock>("SelectedPlatformStateText").Text == "—" && !Find<Button>("GoButton").IsEnabled,
                "empty search clears stale task details and disables actions");
            Capture(dashboard, runRoot, "windows-empty.png");
            Click("ShowAllTasksButton");
            await WaitAsync(() => table.Items.Count == store.LoadRecordedTasks().Count);
            var longTask = store.Create(new TaskDraft(new string('长', 200), "pta", "超长任务标题与备注的独立布局验收", DeadlineKind.Unknown,
                null, null, null, null, "", string.Concat(Enumerable.Repeat("很长的学习备注，仍可滚动阅读并使用下方操作。", 60)), null), DateTimeOffset.UtcNow);
            await WaitAsync(() => table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Any(item => item.Task.Id == longTask.Id));
            table.SelectedItem = table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Single(item => item.Task.Id == longTask.Id);
            dashboard.UpdateLayout();
            var scroller = Find<ScrollViewer>("DetailContentScroller");
            Check(scroller.ScrollableHeight > 0, "long title and evidence scroll inside detail pane");
            var actionBounds = Find<Button>("CompleteButton").TransformToAncestor((Visual)dashboard.Content).TransformBounds(new Rect(Find<Button>("CompleteButton").RenderSize));
            Check(actionBounds.Bottom < ((FrameworkElement)dashboard.Content).ActualHeight, "long evidence keeps task actions on screen");
            store.Delete(longTask.Id, longTask.Revision);
            await WaitAsync(() => !table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Any(item => item.Task.Id == longTask.Id));
            Find<Expander>("PlatformSyncExpander").IsExpanded = false;
            Find<Expander>("SelectedDetailsExpander").IsExpanded = false;
            Capture(dashboard, runRoot, "windows-minimum.png");
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); }
        File.WriteAllText(Path.Combine(runRoot, "dashboard-ui-checks.json"), JsonSerializer.Serialize(new {
            outcome = passed ? "passed" : "failed", checks = checks.Count, labels = checks, heights, layouts, failure,
            data = "synthetic_only", externalBrowser = "captured_without_launch",
            linkProtection = syntheticLinks ? "synthetic_process_local" : "windows_dpapi"
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!ready()) { if (DateTimeOffset.UtcNow > expires) throw new TimeoutException(); await Task.Delay(25); }
    }

    private static void Capture(Window dashboard, string runRoot, string name)
    {
        dashboard.UpdateLayout();
        var content = (FrameworkElement)dashboard.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(content), null, new Rect(content.RenderSize));
        bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(runRoot, name)); png.Save(stream);
    }

    // UI-only verification can use process-local tokens when the runner has no
    // loaded Windows user profile. The default suite still exercises DPAPI.
    private sealed class SyntheticLinkProtector : ILocalLinkProtector
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> links = new(StringComparer.Ordinal);
        public byte[] Protect(string link)
        {
            var token = Guid.NewGuid().ToString("N");
            links[token] = link;
            return System.Text.Encoding.UTF8.GetBytes(token);
        }
        public string Unprotect(byte[] protectedLink) => links[System.Text.Encoding.UTF8.GetString(protectedLink)];
    }
}
