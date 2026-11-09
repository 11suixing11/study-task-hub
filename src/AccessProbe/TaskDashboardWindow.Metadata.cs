using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccessProbe;

public partial class TaskDashboardWindow
{
    private readonly Dictionary<string, MetadataSyncService> metadataServices = new(StringComparer.Ordinal);
    public void AttachMetadataSync(MetadataSyncService service)
    {
        var platform = service.Platform; metadataServices[platform.Id] = service;
        var prefix = platform.Id switch { "ucampus" => "Ucampus", "fif" => "Fif", "pta" => "Pta", _ => "Zhihuishu" };
        var text = (TextBlock)FindName(prefix + "SyncStatusText"); var banner = (Border)FindName(prefix + "SyncBanner");
        service.StatusChanged += (message, running, warning) =>
        {
            if (permitClose) return; text.Text = message;
            text.Foreground = warning ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(36, 90, 195));
            banner.Background = new SolidColorBrush(warning ? Color.FromRgb(255, 240, 239) : Color.FromRgb(234, 241, 252));
            rainRunning[platform.Id] = running; rainWarnings[platform.Id] = warning;
            UpdatePlatformStatus(platform.Id, message, running, warning);
            if (warning) PlatformSyncExpander.IsExpanded = true; UpdateSyncControls();
        };
        service.Completed += _ => RefreshTasks();
        service.CourseSelectionRequired += () => PromptCourseSelection(platform.Id);
    }
    private void OpenMetadataConnection(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is string id && MetadataPlatform.Enabled.Any(platform => platform.Id == id))
            PlatformConnectionRequested?.Invoke(id);
    }
    private void AddMetadataSyncDetails(StackPanel stack)
    {
        foreach (var platform in MetadataPlatform.Enabled)
        {
            var settings = store.LoadMetadataSettings(platform.Id);
            var session = store.LoadPlatformSessionInfo(platform.Id); var recording = store.LoadCourseRecording(platform.Id);
            stack.Children.Add(new TextBlock { Text = platform.Name + "同步详情", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 10) });
            stack.Children.Add(new TextBlock { Text = "当前登录：" + session.Description, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = "最近课程刷新：" + CourseRecordingRules.CatalogRefreshText(recording), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 6) });
            if (metadataServices.TryGetValue(platform.Id, out var currentService)) stack.Children.Add(new TextBlock
            { Text = currentService.LoginDiagnostics.Describe(), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) });
            stack.Children.Add(new TextBlock { Text = "上次任务同步：" + (settings.LastSyncAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未同步") });
            if (settings.LastSummary is { } summary)
            {
                var message = platform.Id == "pta"
                    ? $"发现教学课程 {summary.Courses} 门、已选 {summary.SelectedCourses} 门；完整核对 {summary.CoursesRead} 门题目集和答卷，采集 {summary.Tasks} 条题目集；新增 {summary.Added}、更新 {summary.Updated}。"
                        + (summary.TaskListCoursesRead is { } listCount ? $"已读取题目集列表 {listCount}/{summary.SelectedCourses} 门。" : "")
                        + (summary.TeachingDirectoryFallbackCourses > 0 ? $"其中 {summary.TeachingDirectoryFallbackCourses} 门已通过官方教学目录读取。" : "")
                        + $"有截止时间 {summary.TasksWithDeadline} 条、官方入口 {summary.TasksWithLink} 条、状态待核对 {summary.StatusNeedsReview} 条。"
                        + (summary.CourseListCovered ? "课程目录已读完。" : "课程目录覆盖未确认。")
                        + (summary.FullTaskListsCovered ? "已选课程题目集和个人答卷已核对。" : "部分题目集、答卷状态或时间需核对。")
                    : platform.Id == "zhihuishu"
                    ? $"发现翻转课 {summary.FlipCourses} 门、共享课 {summary.SharedCourses} 门、智慧课程 {summary.SmartCourses} 门、AI 智课／智能体课 {summary.AgentCourses} 门，已选 {summary.SelectedCourses} 门；该轮任务 {summary.Tasks} 条，新增 {summary.Added}、更新 {summary.Updated}。"
                        + $"有截止时间 {summary.TasksWithDeadline} 条、官方入口 {summary.TasksWithLink} 条、状态待核对 {summary.StatusNeedsReview} 条。"
                        + $"其中机房考试 {summary.RoomExamTasks} 条、线下考试 {summary.OfflineExamTasks} 条、补考分支在线考试 {summary.MakeupExamTasks} 条；缺少任务标识的考试安排记录 {summary.UnidentifiedExamRows} 条未导入。"
                        + (summary.CourseListCovered ? "课程目录已读完。" : "课程目录覆盖未确认。")
                        + (summary.FullTaskListsCovered ? "已选课程的作业、考试列表已读完。" : "部分作业、考试列表未读完或结构待核对。")
                        + $"另有首页共享课聚合提醒 {summary.SharedCourseReminders} 条，仅用于核对。"
                        + (summary.SmartOtherTasks > 0 ? $"智慧课程另有 {summary.SmartOtherTasks} 条学习资源或其他任务暂未覆盖。" : "")
                    : $"发现旧版班课 {summary.Courses} 门、已选 {summary.SelectedCourses} 门；完整读取 {summary.CoursesRead} 门作业、测试与单元学习，采集 {summary.Tasks} 条任务；新增 {summary.Added}、更新 {summary.Updated}。"
                        + $"有截止时间 {summary.TasksWithDeadline} 条、可核对官方入口 {summary.TasksWithLink} 条、状态待核对 {summary.StatusNeedsReview} 条。"
                        + (summary.CourseListCovered ? "课程目录已读完。" : "课程目录覆盖未确认。")
                        + (summary.FullTaskListsCovered ? "已选班课作业、测试列表已读完。" : "部分任务列表或字段待核对。")
                        + $"单元学习 {summary.LearningUnits} 条，其中限时 {summary.LearningUnitsWithDeadline} 条、必学未完或待核对 {summary.RequiredLearningUnitsIncomplete} 条。"
                        + (summary.LearningUnitListsCovered ? "单元期限、目录与必学进度已完整核对。" : "部分单元期限、目录或必学进度未完整核对，原有任务保留。")
                        + "特殊课件及其他学习活动的范围见下方说明。";
                stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
                foreach (var problem in summary.Problems)
                    stack.Children.Add(new TextBlock { Text = (session.State == "ready" && summary.LoginRequired ? "上次任务同步记录（当前登录已确认）：" : "上次任务同步记录：")
                        + problem.Code + "：" + problem.Message, Foreground = session.State == "ready" && summary.LoginRequired ? Brushes.DimGray : Brushes.Firebrick,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            }
            else stack.Children.Add(new TextBlock { Text = settings.Connected ? "等待官方登录或首次检查。" : "尚未连接。", Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 8) });
            stack.Children.Add(new TextBlock { Text = platform.CoverageText, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
        }
    }
    private void AddMetadataSettings(StackPanel stack)
    {
        foreach (var platform in MetadataPlatform.Enabled)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            var automatic = new CheckBox { Content = "自动检查" + platform.Name, IsChecked = store.LoadMetadataSettings(platform.Id).AutomaticSyncEnabled,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            automatic.Click += (_, _) => RunAction(() =>
            {
                var settings = store.LoadMetadataSettings(platform.Id) with { AutomaticSyncEnabled = automatic.IsChecked == true };
                store.SaveMetadataSettings(platform.Id, settings);
                if (metadataServices.TryGetValue(platform.Id, out var service)) service.SettingsChanged(settings.AutomaticSyncEnabled && store.LoadSettings().AutomaticSyncEnabled);
            });
            row.Children.Add(automatic); var connect = new Button { Content = "连接" + platform.Name, Padding = new Thickness(12, 5, 12, 5) };
            connect.Click += (_, _) => PlatformConnectionRequested?.Invoke(platform.Id); row.Children.Add(connect); stack.Children.Add(row);
        }
        stack.Children.Add(new TextBlock { Text = MetadataPlatform.Fif.CoverageText, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
    }
}
