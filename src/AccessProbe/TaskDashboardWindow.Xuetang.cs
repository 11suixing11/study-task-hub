using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccessProbe;

public partial class TaskDashboardWindow
{
    public void AttachXuetangSync(XuetangSyncService service)
    {
        xuetangSync = service;
        service.StatusChanged += (message, running, warning) =>
        {
            if (permitClose) return;
            XuetangSyncStatusText.Text = message;
            XuetangSyncStatusText.Foreground = warning ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(36, 90, 195));
            XuetangSyncBanner.Background = new SolidColorBrush(warning ? Color.FromRgb(255, 240, 239) : Color.FromRgb(234, 241, 252));
            rainRunning[XuetangPlatform.Id] = running; rainWarnings[XuetangPlatform.Id] = warning;
            UpdatePlatformStatus("xuetang", message, running, warning);
            if (warning) PlatformSyncExpander.IsExpanded = true;
            UpdateSyncControls();
        };
        service.Completed += _ => RefreshTasks();
        service.CourseSelectionRequired += () => PromptCourseSelection("xuetang");
    }

    private void AddXuetangSyncDetails(StackPanel stack)
    {
        var settings = store.LoadXuetangSettings();
        stack.Children.Add(new TextBlock { Text = "学堂在线同步详情", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 10) });
        stack.Children.Add(new TextBlock { Text = "上次检查：" + (settings.LastSyncAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未检查") });
        if (xuetangSync is not null) stack.Children.Add(new TextBlock { Text = xuetangSync.LoginDiagnostics.Describe(),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0) });
        if (settings.LastSummary is { } summary)
        {
            stack.Children.Add(new TextBlock { Text = $"已完整读取 {summary.CoursesRead}/{summary.SelectedCourses} 门已选课程（发现 {summary.Courses} 门）；本轮 {summary.Tasks} 条任务，新增 {summary.Added}、更新 {summary.Updated}。"
                + $"取得截止时间 {summary.TasksWithDeadline} 条、官方入口 {summary.TasksWithLink} 条，状态待核对 {summary.StatusNeedsReview} 条；课程请求 {summary.CatalogPages} 页，元数据请求 {summary.Requests} 次。"
                + (summary.CourseListCovered ? "新版普通课程列表已读完。" : "课程列表覆盖未确认，请核对官方课程。")
                + (summary.UnsupportedCourses > 0 ? $"另有 {summary.UnsupportedCourses} 条多课项目未适配。" : ""),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
            foreach (var problem in summary.Problems)
                stack.Children.Add(new TextBlock { Text = problem.Code + "：" + (problem.Course.Length > 0 ? problem.Course + "：" : "") + problem.Message,
                    Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        }
        else stack.Children.Add(new TextBlock { Text = settings.Connected ? "已启用连接，登录后先确认课程选择。" : "尚未连接。登录后保存 Cookie 并获取课程列表，保存选择后采集。", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        stack.Children.Add(new TextBlock { Text = XuetangPlatform.CoverageText + "学习进度按官网课程页显示；未确认延期记录时，截止时间待核对。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
    }

    private void AddXuetangSettings(StackPanel stack)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var automatic = new CheckBox { Content = "自动同步学堂在线", IsChecked = store.LoadXuetangSettings().AutomaticSyncEnabled,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        automatic.Click += (_, _) => RunAction(() =>
        {
            var settings = store.LoadXuetangSettings() with { AutomaticSyncEnabled = automatic.IsChecked == true };
            store.SaveXuetangSettings(settings); xuetangSync?.SettingsChanged(settings.AutomaticSyncEnabled && store.LoadSettings().AutomaticSyncEnabled);
        });
        row.Children.Add(automatic);
        var connect = new Button { Content = "连接学堂在线", Padding = new Thickness(12, 5, 12, 5) };
        connect.Click += (_, _) => PlatformConnectionRequested?.Invoke(XuetangPlatform.Id);
        row.Children.Add(connect); stack.Children.Add(row);
    }
}
