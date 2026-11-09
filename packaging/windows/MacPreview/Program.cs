using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AccessProbe;

namespace LearningTaskAggregator.MacPreview;

// This host reuses the product window without starting the installed app, sync services or reminders.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var smoke = args.Length == 1 && args[0] == "--smoke-check";
        if (args.Length != 0 && !smoke) return 2;
        var runRoot = Path.Combine(AppContext.BaseDirectory, "demo-data",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(runRoot);
            var store = new TaskStore(Path.Combine(runRoot, "tasks.db"));
            store.SaveSettings(new AppSettings(false, true)
                { AutomaticSyncEnabled = false, ChaoxingAutomaticSyncEnabled = false });
            Seed(store);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/AccessProbe;component/FluentTheme.xaml", UriKind.Absolute) });
            TaskDashboardWindow? dashboard = null;
            var exiting = false;
            var passed = !smoke;
            void Exit()
            {
                if (exiting) return;
                exiting = true;
                dashboard?.CloseForExit();
                app.Shutdown();
            }
            void ConnectionNotice() => MessageBox.Show(dashboard!,
                "这是演示清单。连接真实平台请使用聚合平台正式版。", "界面预览", MessageBoxButton.OK, MessageBoxImage.Information);
            dashboard = new TaskDashboardWindow(store, runRoot, ConnectionNotice, Exit,
                _ => MessageBox.Show(dashboard!, "已记录打开入口操作。", "界面预览"))
                { Title = "聚合平台 · macOS 风格预览", ShowActivated = !smoke };
            dashboard.PlatformConnectionRequested += _ => ConnectionNotice();
            dashboard.Closed += (_, _) => { if (!exiting) Exit(); };
            dashboard.ShowSuccess("演示清单 · 搜索、筛选、编辑和详情均可操作。");
            app.MainWindow = dashboard;
            if (smoke) dashboard.Loaded += (_, _) => dashboard.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var labels = new List<string>();
                string? failure = null;
                try
                {
                    for (var attempt = 0; attempt < 100 && Table(dashboard).Items.Count != 16; attempt++)
                        await Task.Delay(100);
                    Check(Table(dashboard).Items.Count == 16, "product dashboard loads all 16 local demo tasks", labels);
                    Check(dashboard.GetType().Assembly.GetName().Name == "AccessProbe", "preview uses the real product dashboard assembly", labels);
                    Check(dashboard.FindResource("PrimaryBrush") is SolidColorBrush { Color: var color }
                        && color == Color.FromRgb(0, 107, 230), "product macOS theme and accent resolve", labels);
                    Check(Table(dashboard).Columns.Count == 3, "three task table columns load", labels);
                    Check(dashboard.FindName("SidebarPanel") is FrameworkElement { ActualWidth: > 0 }
                        && dashboard.FindName("TaskDetailPanel") is FrameworkElement { ActualHeight: > 0 }, "sidebar and inspector load", labels);
                    Table(dashboard).SelectedIndex = 0;
                    await Task.Delay(100);
                    dashboard.UpdateLayout();
                    var icons = Descendants(dashboard).OfType<ProductIcon>().Where(icon => icon.IsVisible).ToArray();
                    File.WriteAllText(Path.Combine(runRoot, "icon-render-metrics.json"), JsonSerializer.Serialize(
                        icons.Select(icon => new { icon.Key, icon.Tile, icon.ActualWidth, icon.ActualHeight, rendersInk = RendersInk(icon) }),
                        new JsonSerializerOptions { WriteIndented = true }));
                    Capture(dashboard, Path.Combine(runRoot, "icon-render-dashboard.png"));
                    Check(icons.Length >= 20 && icons.All(RendersInk),
                        "visible product and Fluent icons render at their actual dashboard size", labels);
                    var selected = (TaskDashboardWindow.TaskDisplayRow)Table(dashboard).SelectedItem;
                    Check(((ProductIcon)dashboard.FindName("SelectedPlatformIcon")).Key == selected.PlatformIconKey
                        && Descendants(Table(dashboard)).OfType<ProductIcon>().Any(icon => icon.Key == selected.PlatformIconKey && icon.Tile),
                        "selected task uses the same platform artwork in its row and inspector", labels);
                    var add = (Wpf.Ui.Controls.Button)dashboard.FindName("AddTaskButton");
                    Check(add.Icon is ProductIcon { Foreground: SolidColorBrush { Color: var iconColor } }
                        && add.Foreground is SolidColorBrush { Color: var buttonColor } && iconColor == buttonColor,
                        "toolbar artwork follows the primary button foreground", labels);
                    Capture(dashboard, Path.Combine(runRoot, "preview-default.png"));
                    dashboard.Width = 1100;
                    dashboard.Height = 700;
                    await Task.Delay(100);
                    dashboard.UpdateLayout();
                    Check(Table(dashboard).ActualHeight > 250, "small window retains usable task rows", labels);
                    Check(Table(dashboard).Columns.Sum(column => column.ActualWidth) <= Table(dashboard).ActualWidth,
                        "small window columns fit the task table", labels);
                    Capture(dashboard, Path.Combine(runRoot, "preview-small.png"));
                    var task = store.LoadTasks().First(item => item.State == TaskState.Pending);
                    store.SetState(task.Id, task.Revision, TaskState.Completed, DateTimeOffset.UtcNow);
                    Check(store.Get(task.Id)?.State == TaskState.Completed, "demo completion saves in the isolated database", labels);
                    Check(store.LoadTasks().All(item => item.Link is null), "seeded demo tasks contain no live or encrypted links", labels);
                    Check(!store.LoadSettings().AutomaticSyncEnabled && !store.LoadSettings().NotificationsEnabled,
                        "demo starts with sync and notifications disabled", labels);
                    await VerifyPresentationAsync(dashboard, runRoot, labels);
                    passed = true;
                }
                catch (Exception error) { failure = error.Message; }
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-smoke-checks.json"), JsonSerializer.Serialize(new
                {
                    outcome = passed ? "passed" : "failed", checks = labels.Count, labels, failure, runRoot,
                    runtime = Environment.Version.ToString(), data = "synthetic_only", theme = "product_resource_dictionary",
                    officialNetworkAccessed = false, productionDataAccessed = false, visualAcceptance = "pending"
                }, new JsonSerializerOptions { WriteIndented = true }));
                Exit();
            }));
            app.Run(dashboard);
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(runRoot, "preview-startup-error.txt"), error.ToString());
            if (!smoke) MessageBox.Show("预览未能启动。请将完整预览文件夹解压到可写目录后重试。", "界面预览");
            return 1;
        }
    }

    private static DataGrid Table(TaskDashboardWindow dashboard) => (DataGrid)dashboard.FindName("TaskTable");

    private static async Task VerifyPresentationAsync(TaskDashboardWindow dashboard, string runRoot, List<string> labels)
    {
        var dialogStore = new TaskStore(Path.Combine(runRoot, "dialog-checks", "tasks.db"));
        dialogStore.SaveSettings(new AppSettings(false, true) { AutomaticSyncEnabled = false });
        var now = DateTimeOffset.UtcNow;
        var managerMetrics = new List<object>();
        var managerIconsMatch = true;
        var managerLayoutsFit = true;
        var collapsedManagersFit = true;
        var courseChoicesWork = true;
        var cancelKeepsSelections = true;
        foreach (var platform in PlatformCatalog.Sites.Where(site => CourseRecordingRules.SupportsAutomatic(site.Id)))
        {
            var demo = Enumerable.Range(1, 3).Select(index => dialogStore.Create(new TaskDraft(
                $"界面验收任务 {index}", platform.Id, $"演示课程 {index}", DeadlineKind.Unknown,
                null, null, null, null, "", "独立合成数据", null), now)).ToArray();
            var courses = demo.Select(task => new DiscoveredRecordingCourse(
                new RecordingCourse(CourseRecordingRules.TaskCourseKey(task), task.CourseName, "合成课程 · 本地界面检查"), _ => false)).ToArray();
            var catalog = dialogStore.ObserveRecordingCourses(platform.Id, courses, true, now);
            var saved = dialogStore.SaveCourseRecording(platform.Id, false, courses.Take(2).Select(course => course.Course.Key).ToArray(),
                true, catalog.Revision, now);
            var manager = new CourseSelectionWindow(dialogStore, platform.Id, _ => Task.CompletedTask, _ => Task.CompletedTask,
                _ => { }, true, (_, _) => { }, _ => { }) { Owner = dashboard, ShowActivated = false };
            try
            {
                manager.Show();
                var list = (ListBox)manager.FindName("RecordingCourseList");
                for (var attempt = 0; attempt < 100 && list.ItemsSource is null; attempt++) await Task.Delay(20);
                if (list.ItemsSource is null) throw new InvalidOperationException("The manager course list did not load.");
                manager.UpdateLayout();
                var brand = (ProductIcon)manager.FindName("RecordingPlatformArtwork");
                managerIconsMatch &= manager.Icon is not null && brand.Key == platform.Id && RendersInk(brand)
                    && new[] { "RecordingConnectionButton", "RecordingSaveButton", "RecordingRefreshButton", "PlatformSyncButton" }
                        .All(name => ((Wpf.Ui.Controls.Button)manager.FindName(name)).Icon is ProductIcon icon && RendersInk(icon));
                if (platform.Id == "pta") Capture(manager, Path.Combine(runRoot, "preview-courses.png"));
                manager.Width = manager.MinWidth; manager.Height = manager.MinHeight;
                await Task.Delay(60); manager.UpdateLayout();
                var expected = new[] { "RecordingCourseSearch", "RecordingCourseList", "RecordingSaveButton",
                    "RecordingCancelButton", "RecordingHideHistory", "RecordingConnectionButton", "PlatformSyncButton" };
                var fits = expected.All(name => FitsContent(manager, (FrameworkElement)manager.FindName(name))) && list.ActualHeight >= 70;
                managerLayoutsFit &= fits;
                managerMetrics.Add(new { platform = platform.Id, width = manager.Width, height = manager.Height,
                    courseListHeight = list.ActualHeight, controlsFit = fits, iconSize = brand.ActualWidth });
                if (platform.Id == "pta") Capture(manager, Path.Combine(runRoot, "preview-courses-small.png"));
                ((Button)manager.FindName("PlatformCollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(60); manager.UpdateLayout();
                collapsedManagersFit &= !((Expander)manager.FindName("PlatformCoursesExpander")).IsExpanded
                    && new[] { "RecordingConnectionButton", "PlatformSyncButton", "RecordingCancelButton" }
                        .All(name => FitsContent(manager, (FrameworkElement)manager.FindName(name)));
                ((Button)manager.FindName("PlatformCollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(60); manager.UpdateLayout();
                var search = (TextBox)manager.FindName("RecordingCourseSearch"); search.Text = "演示课程 2";
                manager.UpdateLayout();
                ((Button)manager.FindName("RecordingSelectNoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var choices = ((IEnumerable<CourseSelectionWindow.CourseChoice>)list.ItemsSource).ToArray();
                courseChoicesWork &= list.Items.Count == 1 && choices.Single(choice => choice.Name == "演示课程 1").Selected
                    && !choices.Single(choice => choice.Name == "演示课程 2").Selected;
                ((Button)manager.FindName("RecordingSelectAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                courseChoicesWork &= choices.Single(choice => choice.Name == "演示课程 2").Selected
                    && !choices.Single(choice => choice.Name == "演示课程 3").Selected;
                search.Clear();
                ((Button)manager.FindName("RecordingSelectNoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                manager.Close();
                cancelKeepsSelections &= dialogStore.LoadCourseRecording(platform.Id).SelectedCourseKeys.SequenceEqual(saved.SelectedCourseKeys);
            }
            finally { if (manager.IsVisible) manager.Close(); }
        }
        File.WriteAllText(Path.Combine(runRoot, "dialog-layout-metrics.json"), JsonSerializer.Serialize(managerMetrics,
            new JsonSerializerOptions { WriteIndented = true }));
        Check(managerIconsMatch, "all eight platform managers render their platform and action artwork", labels);
        Check(managerLayoutsFit, "all eight minimum-size managers retain course rows and visible actions", labels);
        Check(collapsedManagersFit, "collapsed platform managers retain connection and sync actions", labels);
        Check(courseChoicesWork, "course search and bulk choices affect only the visible courses", labels);
        Check(cancelKeepsSelections, "closing a manager preserves the previously saved course selection", labels);

        var editor = new TaskEditorWindow { Owner = dashboard, ShowActivated = false };
        Exception? editorFailure = null;
        var editorFits = false;
        var validationShows = false;
        var footerStaysVisible = false;
        editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Task.Delay(60); editor.UpdateLayout();
                Capture(editor, Path.Combine(runRoot, "preview-editor.png"));
                editor.Width = editor.MinWidth; editor.Height = editor.MinHeight;
                await Task.Delay(60); editor.UpdateLayout();
                var save = (Wpf.Ui.Controls.Button)editor.FindName("TaskEditorSave");
                var cancel = (Button)editor.FindName("TaskEditorCancel");
                var scroller = (ScrollViewer)editor.FindName("TaskEditorScroll");
                editorFits = FitsContent(editor, save) && FitsContent(editor, cancel) && scroller.ViewportHeight >= 200
                    && scroller.ScrollableHeight > 0 && save.Icon is ProductIcon saveIcon && RendersInk(saveIcon);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                validationShows = editor.Result is null && ((TextBlock)editor.FindName("TaskEditorError")).Text.Length > 0;
                var title = (TextBox)editor.FindName("TaskEditorTitle"); title.Text = "界面验收 · 新增任务";
                ((ComboBox)editor.FindName("TaskEditorPlatform")).SelectedValue = "pta";
                scroller.ScrollToBottom(); editor.UpdateLayout();
                footerStaysVisible = FitsContent(editor, save) && save.IsVisible && scroller.VerticalOffset > 0;
                Capture(editor, Path.Combine(runRoot, "preview-editor-small.png"));
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { editorFailure = error; editor.Close(); }
        }));
        var accepted = editor.ShowDialog();
        if (editorFailure is not null) throw editorFailure;
        Check(editorFits, "minimum-size editor retains a scrollable form and visible save controls", labels);
        Check(validationShows, "editor rejects an empty title and displays the validation message", labels);
        Check(footerStaysVisible, "editor save controls stay visible while scrolling supplementary fields", labels);
        Check(accepted == true && editor.Result is { Title: "界面验收 · 新增任务", PlatformId: "pta" },
            "editor save returns the entered task name and platform", labels);

        var taskSearch = (TextBox)dashboard.FindName("SearchBox"); taskSearch.Text = "NO-MATCH-REFINED-PREVIEW";
        dashboard.UpdateLayout();
        var reset = (Button)dashboard.FindName("EmptyResetFiltersButton");
        Check(Table(dashboard).Items.Count == 0 && ((Border)dashboard.FindName("EmptyStatePanel")).IsVisible
            && FitsContent(dashboard, reset) && reset.IsVisible
            && ((TextBlock)dashboard.FindName("EmptyStateTitle")).Text == "没有匹配的任务",
            "empty search presents a visible recovery action without stale task details", labels);
        Capture(dashboard, Path.Combine(runRoot, "preview-empty.png"));
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(taskSearch.Text.Length == 0 && Table(dashboard).Items.Count == 16
            && !((Border)dashboard.FindName("EmptyStatePanel")).IsVisible,
            "empty-state recovery restores all demo tasks and hides the placeholder", labels);

        var firstStore = new TaskStore(Path.Combine(runRoot, "empty-checks", "tasks.db"));
        firstStore.SaveSettings(new AppSettings(false, true) { AutomaticSyncEnabled = false });
        var firstDashboard = new TaskDashboardWindow(firstStore, runRoot, () => { }, () => { }, _ => { })
            { Owner = dashboard, ShowActivated = false, Width = 1100, Height = 700 };
        try
        {
            firstDashboard.Show();
            var firstPanel = (Border)firstDashboard.FindName("EmptyStatePanel");
            for (var attempt = 0; attempt < 100 && !firstPanel.IsVisible; attempt++) await Task.Delay(20);
            firstDashboard.UpdateLayout();
            Check(firstPanel.IsVisible && ((TextBlock)firstDashboard.FindName("EmptyStateTitle")).Text == "从一门课程开始"
                && FitsContent(firstDashboard, (Button)firstDashboard.FindName("EmptyAddTaskButton"))
                && FitsContent(firstDashboard, (Button)firstDashboard.FindName("EmptyCoursesButton")),
                "a fresh empty database offers visible add-task and course-management actions", labels);
            Capture(firstDashboard, Path.Combine(runRoot, "preview-first-use.png"));
            firstStore.Create(new TaskDraft("历史任务 · 保留验收", "chaoxing", "演示课程", DeadlineKind.Unknown,
                null, null, null, null, "", "独立合成数据", null), now);
            var firstRecording = firstStore.LoadCourseRecording("chaoxing");
            firstStore.SaveCourseRecording("chaoxing", false, [], true, firstRecording.Revision, now);
            for (var attempt = 0; attempt < 100
                && ((TextBlock)firstDashboard.FindName("EmptyStateTitle")).Text != "历史任务已保留"; attempt++) await Task.Delay(20);
            firstDashboard.UpdateLayout();
            Check(Table(firstDashboard).Items.Count == 0 && firstStore.LoadTasks().Count == 1
                && ((TextBlock)firstDashboard.FindName("EmptyText")).Text.Contains("已保留", StringComparison.Ordinal)
                && FitsContent(firstDashboard, (Button)firstDashboard.FindName("EmptyCoursesButton"))
                && !((Button)firstDashboard.FindName("EmptyResetFiltersButton")).IsVisible,
                "hidden course history stays preserved and directs the user to course selection", labels);
            Capture(firstDashboard, Path.Combine(runRoot, "preview-retained.png"));
        }
        finally { firstDashboard.CloseForExit(); }
    }

    private static bool FitsContent(Window window, FrameworkElement element)
    {
        var content = (FrameworkElement)window.Content;
        var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
        return element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0 && bounds.Left >= -1
            && bounds.Top >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static bool RendersInk(ProductIcon icon)
    {
        if (icon.ActualWidth <= 0 || icon.ActualHeight <= 0) return false;
        var width = (int)Math.Ceiling(icon.ActualWidth);
        var height = (int)Math.Ceiling(icon.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(icon) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return Enumerable.Range(0, width * height).Count(index => pixels[index * 4 + 3] > 16) >= 5;
    }

    private static void Check(bool condition, string label, List<string> labels)
    {
        if (!condition) throw new InvalidOperationException(label);
        labels.Add(label);
    }

    private static void Capture(FrameworkElement view, string path)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Seed(TaskStore store)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (title, platform, course, hours, state) in new[]
        {
            ("极限与连续 · 课后练习", "chaoxing", "高等数学", -4, TaskState.Pending),
            ("Unit 4 · 阅读与词汇", "ucampus", "大学英语", 6, TaskState.Pending),
            ("栈与队列 · 题目集", "pta", "数据结构", 10, TaskState.Pending),
            ("第三章 · 学习总结", "mooc", "线性代数", 18, TaskState.Pending),
            ("第六讲 · 随堂测验", "yuketang", "计算机网络", 30, TaskState.Pending),
            ("模型与推理 · 课程测验", "zhihuishu", "人工智能导论", 48, TaskState.Pending),
            ("模块三 · 课后复习", "xuetang", "程序设计基础", 72, TaskState.Pending),
            ("电路与磁场 · 课堂测试", "changjiang", "大学物理", 96, TaskState.Pending),
            ("矩阵运算 · 单元测试", "mooc", "线性代数", 120, TaskState.Pending),
            ("链表应用 · 上机练习", "pta", "数据结构", 144, TaskState.Pending),
            ("课程报告 · 第一轮修改", "chaoxing", "学术写作", 36, TaskState.Returned),
            ("第四讲 · 随堂测验", "yuketang", "计算机网络", -48, TaskState.Completed),
            ("模块二 · 课后练习", "xuetang", "程序设计基础", -36, TaskState.Completed),
            ("Unit 3 · 词汇复习", "ucampus", "大学英语", -24, TaskState.Completed),
            ("向量空间 · 基础练习", "mooc", "线性代数", -12, TaskState.Completed)
        })
        {
            var task = store.Create(new TaskDraft(title, platform, course, DeadlineKind.Known, null, now.AddHours(hours),
                null, null, "", "先复习课堂笔记，再核对完成情况；需要时可在这里补充备注。", null), now);
            if (state == TaskState.Returned) task = store.SetState(task.Id, task.Revision, TaskState.Completed, now);
            if (state != TaskState.Pending) store.SetState(task.Id, task.Revision, state, now);
        }
        store.Create(new TaskDraft("学期项目 · 选题与提纲", "chaoxing", "综合课程", DeadlineKind.Unknown, null, null,
            null, null, "", "确认截止时间后，可以编辑任务并补充日期。", null), now);
    }
}
