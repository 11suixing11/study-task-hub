using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AccessProbe;

public partial class TaskDashboardWindow : Window
{
    private readonly TaskStore store;
    private readonly Action openConnection;
    private readonly Action exit;
    private readonly string projectRoot;
    private readonly TaskNavigationService taskNavigation;
    private CancellationTokenSource? taskOpenCancellation;
    private readonly DispatcherTimer refreshTimer;
    private IReadOnlyList<TaskItem> tasks = [];
    private bool ready;
    private bool permitClose;
    private bool refreshingTasks;
    private bool refreshAgain;
    private ReminderService? reminders;
    private ChaoxingSyncService? sync;
    private MoocSyncService? moocSync;
    private ChaoxingSyncSummary? latestSync;
    private readonly Dictionary<string, RainSyncService> rainServices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> rainRunning = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> rainWarnings = new(StringComparer.Ordinal);
    private bool chaoxingRunning;
    private bool chaoxingWarning;
    public event Action<bool>? NotificationSettingChanged;
    public event Action<bool>? AutomaticSyncSettingChanged;
    public event Action<string>? PlatformConnectionRequested;

    public TaskDashboardWindow(TaskStore store, string root, Action openConnection, Action exit, Action<string>? openExternal = null)
    {
        this.store = store; projectRoot = root; this.openConnection = openConnection; this.exit = exit;
        taskNavigation = new TaskNavigationService(store,
            (address, token) => sync is null ? Task.FromResult(new TaskLinkResolution("session_unavailable", "none"))
                : sync.ResolveTaskAddressAsync(address, token),
            openExternal ?? (address => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })));
        InitializeComponent();
        StateFilter.ItemsSource = new[] { "待办（含待核对）", "全部状态", "未完成", "已完成", "已忽略", "被打回", "已逾期", "24 小时内到期", "待核对" };
        StateFilter.SelectedIndex = 1;
        PlatformFilter.ItemsSource = new[] { "全部平台" }.Concat(PlatformCatalog.Sites.Select(site => site.Name)).ToArray();
        PlatformFilter.SelectedIndex = 0;
        SortFilter.ItemsSource = new[] { "优先级排序", "截止时间排序", "最近更新" };
        SortFilter.SelectedIndex = 0;
        store.Changed += StoreChanged;
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        refreshTimer.Tick += (_, _) => RefreshTasks(); refreshTimer.Start();
        ready = true;
        Loaded += (_, _) => RefreshTasks();
        Closing += OnClosing;
        Closed += (_, _) => { permitClose = true; taskOpenCancellation?.Cancel(); store.Changed -= StoreChanged; refreshTimer.Stop(); };
    }

    public void AttachReminders(ReminderService service)
    {
        reminders = service; RefreshTasks();
        service.StatusChanged += text =>
        {
            if (!permitClose) { RuntimeText.Text = text + " 关闭窗口后继续在托盘运行；退出程序后停止。"; }
        };
    }

    public void AttachSync(ChaoxingSyncService service)
    {
        sync = service;
        service.StatusChanged += (message, running, warning) =>
        {
            if (permitClose) return;
            SyncStatusText.Text = message;
            SyncStatusText.Foreground = warning ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(36, 90, 195));
            SyncBanner.Background = new SolidColorBrush(warning ? Color.FromRgb(255, 240, 239) : Color.FromRgb(234, 241, 252));
            chaoxingRunning = running;
            chaoxingWarning = warning;
            if (warning) PlatformSyncExpander.IsExpanded = true;
            UpdateSyncControls();
        };
        service.Completed += summary => { latestSync = summary; RefreshTasks(); };
    }

    public void AttachRainSync(RainSyncService service)
    {
        rainServices[service.Platform.Id] = service;
        var text = service.Platform.Id == "yuketang" ? RainSyncStatusText : ChangjiangSyncStatusText;
        var banner = service.Platform.Id == "yuketang" ? RainSyncBanner : ChangjiangSyncBanner;
        service.StatusChanged += (message, running, warning) =>
        {
            if (permitClose) return;
            text.Text = message;
            text.Foreground = warning ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(36, 90, 195));
            banner.Background = new SolidColorBrush(warning ? Color.FromRgb(255, 240, 239) : Color.FromRgb(234, 241, 252));
            rainRunning[service.Platform.Id] = running;
            rainWarnings[service.Platform.Id] = warning;
            if (warning) PlatformSyncExpander.IsExpanded = true;
            UpdateSyncControls();
        };
        service.Completed += _ => RefreshTasks();
    }

    private void UpdateSyncControls()
    {
        var running = chaoxingRunning || rainRunning.Values.Any(value => value);
        SyncButton.IsEnabled = !running;
        StopSyncButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        var active = (chaoxingRunning ? new[] { "学习通" } : Array.Empty<string>())
            .Concat(rainRunning.Where(pair => pair.Value).Select(pair => PlatformCatalog.Name(pair.Key)));
        var warnings = (chaoxingWarning ? new[] { "学习通" } : Array.Empty<string>())
            .Concat(rainWarnings.Where(pair => pair.Value).Select(pair => PlatformCatalog.Name(pair.Key))).ToArray();
        SyncOverviewText.Text = running ? "正在采集 · " + string.Join("、", active)
            : warnings.Length > 0 ? "平台同步需处理 · " + string.Join("、", warnings) : "平台连接与同步";
        SyncOverviewText.Foreground = warnings.Length > 0 ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(98, 114, 138));
    }

    public void AttachMoocSync(MoocSyncService service)
    {
        moocSync = service;
        service.StatusChanged += (message, running, warning) =>
        {
            if (permitClose) return;
            MoocSyncStatusText.Text = message;
            MoocSyncStatusText.Foreground = warning ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(36, 90, 195));
            MoocSyncBanner.Background = new SolidColorBrush(warning ? Color.FromRgb(255, 240, 239) : Color.FromRgb(234, 241, 252));
            rainRunning[MoocPlatform.Id] = running; rainWarnings[MoocPlatform.Id] = warning;
            if (warning) PlatformSyncExpander.IsExpanded = true;
            UpdateSyncControls();
        };
        service.Completed += _ => RefreshTasks();
    }

    public void ShowDashboard()
    {
        Show(); WindowState = WindowState.Normal; Activate(); ShowTasks(1);
    }

    public void CloseForExit() { if (permitClose) return; permitClose = true; Close(); }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (permitClose || reminders is null) return;
        args.Cancel = true; Hide();
    }

    private void StoreChanged()
    {
        if (!permitClose && !Dispatcher.HasShutdownStarted)
        {
            if (Dispatcher.CheckAccess()) RefreshTasks();
            else Dispatcher.BeginInvoke(new Action(RefreshTasks));
        }
    }

    public async void RefreshTasks()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(new Action(RefreshTasks));
            return;
        }
        // WPF installs the UI synchronization context during dispatcher events, after construction.
        if (!ready || permitClose || !IsLoaded) return;
        if (refreshingTasks) { refreshAgain = true; return; }
        refreshingTasks = true;
        try
        {
            do
            {
                refreshAgain = false;
                var snapshot = await Task.Run(() => (Tasks: store.LoadRecordedTasks(), StoredTasks: store.LoadTasks(), Settings: store.LoadSettings()));
                if (permitClose || Dispatcher.HasShutdownStarted) return;
                tasks = snapshot.Tasks;
                var visibleIds = tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
                retainedCourseTasksByPlatform = snapshot.StoredTasks.Where(task => !visibleIds.Contains(task.Id))
                    .GroupBy(task => task.PlatformId).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                retainedCourseTaskCount = retainedCourseTasksByPlatform.Values.Sum();
                var now = DateTimeOffset.UtcNow;
                ActiveCountText.Text = tasks.Count(TaskRules.IsActive).ToString();
                OverdueCountText.Text = tasks.Count(task => TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc < now).ToString();
                SoonCountText.Text = tasks.Count(task => TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc >= now && task.DeadlineUtc <= now.AddHours(24)).ToString();
                CompleteCountText.Text = tasks.Count(task => task.State == TaskState.Completed).ToString();
                AggregationTitleText.Text = $"统一任务清单 · 共 {tasks.Count} 条";
                var sources = new[] { "chaoxing", "yuketang", "changjiang", "mooc" }
                    .Concat(PlatformCatalog.Sites.Where(site => site.Id is not ("chaoxing" or "yuketang" or "changjiang" or "mooc")
                        && tasks.Any(task => task.PlatformId == site.Id)).Select(site => site.Id));
                AggregationSummaryText.Text = string.Join(" · ", sources.Select(platformId =>
                    $"{PlatformCatalog.Name(platformId)} {tasks.Count(task => task.PlatformId == platformId)} 条"))
                    + (retainedCourseTaskCount > 0 ? $" · 未选课程 {retainedCourseTaskCount} 条已隐藏并保留" : "");
                ApplyFilters();
                RuntimeText.Text = reminders is null ? "数据库已加载 · 此窗口未运行提醒服务。" : snapshot.Settings.NotificationsEnabled
                    ? "数据库已加载 · 提醒运行中。关闭窗口后保留托盘；退出程序后停止，重启会检查待发提醒。"
                    : "数据库已加载 · 通知已关闭。任务仍会保存，可在设置中开启提醒。";
            } while (refreshAgain);
        }
        catch
        {
            if (!permitClose) ShowError("DB001", "本地任务库读取失败。请检查目录权限，并先备份 .local/data/tasks.db。");
        }
        finally { refreshingTasks = false; }
    }

    private void FilterChanged(object sender, RoutedEventArgs args) { if (ready) ApplyFilters(); }

    private void ApplyFilters()
    {
        if (!ready) return;
        var selectedId = (TaskTable.SelectedItem as TaskDisplayRow)?.Task.Id;
        var now = DateTimeOffset.UtcNow;
        IEnumerable<TaskItem> filtered = tasks;
        filtered = StateFilter.SelectedIndex switch
        {
            0 => filtered.Where(TaskRules.IsActive), 2 => filtered.Where(task => task.State == TaskState.Pending),
            3 => filtered.Where(task => task.State == TaskState.Completed), 4 => filtered.Where(task => task.State == TaskState.Ignored),
            5 => filtered.Where(task => task.State == TaskState.Returned),
            6 => filtered.Where(task => TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc < now),
            7 => filtered.Where(task => TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc >= now && task.DeadlineUtc <= now.AddHours(24)),
            8 => filtered.Where(task => task.State == TaskState.NeedsReview),
            _ => filtered
        };
        if (PlatformFilter.SelectedIndex > 0) filtered = filtered.Where(task => task.PlatformId == PlatformCatalog.Sites[PlatformFilter.SelectedIndex - 1].Id);
        var search = SearchBox.Text.Trim();
        if (search.Length > 0)
            filtered = filtered.Where(task => new[] { task.Title, task.CourseName, PlatformCatalog.Name(task.PlatformId), task.Notes }
                .Any(text => text.Contains(search, StringComparison.OrdinalIgnoreCase)));
        filtered = SortFilter.SelectedIndex switch
        {
            1 => filtered.OrderBy(task => task.DeadlineUtc ?? DateTimeOffset.MaxValue).ThenBy(task => task.Title),
            2 => filtered.OrderByDescending(task => task.UpdatedAtUtc).ThenBy(task => task.Id),
            _ => filtered.OrderBy(task => TaskRules.IsActive(task) ? 0 : 1).ThenBy(task => TaskRules.Priority(task, now))
                .ThenBy(task => task.DeadlineUtc ?? DateTimeOffset.MaxValue).ThenBy(task => task.Title)
        };
        var rows = filtered.Select(task => new TaskDisplayRow(task, now)).ToArray();
        var selectedPlatform = PlatformFilter.SelectedIndex > 0 ? PlatformCatalog.Sites[PlatformFilter.SelectedIndex - 1] : null;
        var platformTasks = selectedPlatform is null ? tasks : tasks.Where(task => task.PlatformId == selectedPlatform.Id).ToArray();
        var hiddenCompleted = platformTasks.Count(task => task.State == TaskState.Completed)
            - rows.Count(row => row.Task.State == TaskState.Completed);
        var hiddenOtherPlatforms = tasks.Count - platformTasks.Count;
        FilterCountText.Text = $"当前显示 {rows.Length}/{tasks.Count} 条 · {selectedPlatform?.Name ?? "全部平台"} · {StateFilter.SelectedItem}"
            + (search.Length > 0 ? " · 搜索已启用" : "")
            + (hiddenOtherPlatforms > 0 ? $"；有 {hiddenOtherPlatforms} 条其他平台任务被当前筛选隐藏。" : "")
            + (hiddenCompleted > 0 ? $"；有 {hiddenCompleted} 条已完成任务被当前筛选隐藏。" : "")
            + (retainedCourseTaskCount > 0 ? $"；未选课程 {retainedCourseTaskCount} 条已保留，可在“选择记录课程”恢复。" : "");
        TaskTable.ItemsSource = rows;
        TaskTable.SelectedItem = rows.FirstOrDefault(row => row.Task.Id == selectedId);
        if (TaskTable.SelectedItem is null && rows.Length > 0) TaskTable.SelectedIndex = 0;
        EmptyText.Text = tasks.Count == 0 && retainedCourseTaskCount > 0 ? "当前没有记录中的任务。未选课程的历史任务已保留，可在“选择记录课程”重新勾选恢复。"
            : tasks.Count == 0 ? "还没有任务。连接学习通、雨课堂、长江雨课堂或中国大学 MOOC 后，任务会自动同步到这里；也可新增任务。"
            : platformTasks.Count == 0 && selectedPlatform is not null && retainedCourseTasksByPlatform.GetValueOrDefault(selectedPlatform.Id) > 0
                ? selectedPlatform.Name + "未选课程的历史任务已隐藏并保留，可在“选择记录课程”重新勾选恢复。"
            : platformTasks.Count == 0 && selectedPlatform is not null ? selectedPlatform.Name + "尚未采集到任务，请查看该平台同步结果。"
            : hiddenCompleted > 0 ? $"本地已有 {tasks.Count} 条任务，其中 {hiddenCompleted} 条已完成任务被当前筛选隐藏。点击“查看全部任务”可恢复全部平台和状态。"
            : "当前筛选没有任务。点击“查看全部任务”可清除搜索与筛选。";
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void TaskSelected(object sender, SelectionChangedEventArgs args) { if (ready) UpdateSelection(); }

    private void UpdateSelection()
    {
        var task = (TaskTable.SelectedItem as TaskDisplayRow)?.Task;
        GoButton.IsEnabled = task is not null && TaskRules.IsSafeLink(task.Link) && taskOpenCancellation is null;
        GoButton.Content = taskOpenCancellation is not null ? "正在打开…" : task is null ? "做题 / 提交 ↗" : TaskNavigationRules.ActionName(task);
        GoButton.ToolTip = "在系统浏览器打开所选任务；也可点击列表中的任务名称。";
        EditButton.IsEnabled = DeleteButton.IsEnabled = ExtensionButton.IsEnabled = task is not null;
        CompleteButton.IsEnabled = task is not null && task.State != TaskState.Completed && TaskRules.CanComplete(task);
        ReturnButton.IsEnabled = task?.State == TaskState.Completed;
        IgnoreButton.IsEnabled = task is not null && task.State != TaskState.Ignored;
        RestoreButton.IsEnabled = task is not null && task.State != TaskState.Pending;
        CompleteButton.ToolTip = task is not null && !TaskRules.CanComplete(task) ? "采集进度不足 100%，可核对后编辑修正。" : "手动修改后保留你的状态";
        if (task is null)
        {
            SelectedDetails.Text = "选中任务可查看采集时间、平台状态、原文时间和备注。"; return;
        }
        var lines = new List<string>
        {
            task.Title + " · " + TaskRules.Confidence(task),
            TaskRules.IsCollected(task)
                ? "最近采集：" + task.CollectedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "；平台状态：" + (string.IsNullOrWhiteSpace(task.PlatformStatusText) ? "未读取" : task.PlatformStatusText) + "（以原平台为准）"
                : "最近更新：" + task.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            "开放时间：" + (task.BeginUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知")
                + "；进度：" + (task.ProgressPercent.HasValue ? task.ProgressPercent + "%" : "未提供") + "；成绩：" + (task.ScoreText.Length == 0 ? "未提供" : task.ScoreText)
        };
        if (task.DisplayedTimeText.Length > 0) lines.Add("平台时间原文：" + task.DisplayedTimeText);
        lines.Add("截止时间：" + TaskRules.DeadlineName(task));
        if (TaskRules.HasUsableDeadline(task) && !TaskRules.HasTrustedDeadline(task))
            lines.Add("该时间用于排序，带有年份推定或剩余时长估算；可在编辑中核对确认后启用定时提醒。");
        if (task.LinkIssue is not null) lines.Add(task.LinkIssue + "：原平台链接不可用，请同步对应平台或编辑补充。");
        else if (task.Link is null) lines.Add("链接待补充：目前无法直达作业，可在编辑中填写。");
        if (task.Notes.Length > 0) lines.Add("备注：" + task.Notes);
        SelectedDetails.Text = string.Join("\n", lines);
    }

    private void FilterActive(object sender, RoutedEventArgs args) => ShowTasks(0);
    private void FilterOverdue(object sender, RoutedEventArgs args) => ShowTasks(6);
    private void FilterSoon(object sender, RoutedEventArgs args) => ShowTasks(7);
    private void FilterCompleted(object sender, RoutedEventArgs args) => ShowTasks(3);
    private void OpenConnection(object sender, RoutedEventArgs args) => openConnection();
    private void OpenRainConnection(object sender, RoutedEventArgs args) => PlatformConnectionRequested?.Invoke("yuketang");
    private void OpenChangjiangConnection(object sender, RoutedEventArgs args) => PlatformConnectionRequested?.Invoke("changjiang");
    private void OpenMoocConnection(object sender, RoutedEventArgs args) => PlatformConnectionRequested?.Invoke(MoocPlatform.Id);
    private void ViewAllTasks(object sender, RoutedEventArgs args) => ShowTasks(1);
    private void ShowTasks(int stateIndex)
    {
        // Change filters together so the selected task survives intermediate empty views.
        var wasReady = ready;
        ready = false;
        try
        {
            SearchBox.Clear();
            StateFilter.SelectedIndex = stateIndex;
            PlatformFilter.SelectedIndex = 0;
        }
        finally { ready = wasReady; }
        ApplyFilters();
        RefreshTasks();
    }
    private async void SyncNow(object sender, RoutedEventArgs args)
    {
        if (sync is null && rainServices.Count == 0 && moocSync is null) { ShowError("SYNC002", "后台同步尚未就绪，请重新启动程序。"); return; }
        ShowTasks(1);
        var runs = rainServices.Values.Where(service => store.LoadPlatformSettings(service.Platform.Id).Connected)
            .Select(service => service.StartAsync(true)).ToList();
        if (sync is not null) runs.Add(sync.StartAsync(true));
        if (moocSync is not null && store.LoadMoocSettings().Connected) runs.Add(moocSync.StartAsync(true));
        await Task.WhenAll(runs);
        UpdateSyncControls();
    }
    private void StopSync(object sender, RoutedEventArgs args)
    {
        sync?.Cancel();
        moocSync?.Cancel();
        foreach (var service in rainServices.Values) service.Cancel();
    }
    private void ShowSyncDetails(object sender, RoutedEventArgs args)
    {
        var settings = store.LoadSettings();
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = "学习通同步详情", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        stack.Children.Add(new TextBlock { Text = "上次检查：" + (settings.LastSyncAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未检查"), Margin = new Thickness(0, 0, 0, 10) });
        if ((latestSync ?? settings.LastSyncSummary) is { } summary)
        {
            stack.Children.Add(new TextBlock { Text = $"已读取 {summary.CoursesRead}/{summary.SelectedCourses ?? summary.Courses} 门已选课程（发现 {summary.Courses} 门），共 {summary.Tasks} 条任务；新增 {summary.Added}、更新 {summary.Updated}，读取时间原文 {summary.DetailsSaved} 条。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            if (summary.DetailsUnavailable > 0)
                stack.Children.Add(new TextBlock { Text = $"本轮 {summary.DetailsUnavailable} 条详情显示提交提示，平台未提供作答时间；已保留列表信息。",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) });
            if (summary.DetailsRequireAction > 0)
                stack.Children.Add(new TextBlock { Text = $"本轮 {summary.DetailsRequireAction} 条详情转入作答页面，已保留列表信息；可通过“前往作业”自行查看。",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var problem in summary.Problems)
                stack.Children.Add(new TextBlock { Text = problem.Code + "：" + (problem.Course.Length > 0 ? problem.Course + " — " : "") + problem.Message,
                    Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        }
        foreach (var platform in RainPlatform.All)
        {
            var connection = store.LoadPlatformSettings(platform.Id);
            stack.Children.Add(new TextBlock { Text = platform.Name + "同步详情", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 10) });
            stack.Children.Add(new TextBlock { Text = "上次检查：" + (connection.LastSyncAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未检查")
                + "；官方站点：" + connection.Host, TextWrapping = TextWrapping.Wrap });
            if (connection.LastSummary is { } rain)
            {
                stack.Children.Add(new TextBlock { Text = $"已完整读取 {rain.CoursesRead}/{rain.SelectedCourses ?? rain.Courses} 门已选课程（发现 {rain.Courses} 门）；本轮 {rain.Tasks} 条任务，新增 {rain.Added}、更新 {rain.Updated}。"
                    + $"取得截止时间 {rain.TasksWithDeadline} 条、官方入口 {rain.TasksWithLink} 条，状态待核对 {rain.StatusNeedsReview} 条；元数据请求 {rain.Requests} 次。",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
                foreach (var problem in rain.Problems)
                    stack.Children.Add(new TextBlock { Text = problem.Code + "：" + (problem.Course.Length > 0 ? problem.Course + " — " : "") + problem.Message,
                        Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            }
            else stack.Children.Add(new TextBlock { Text = connection.Connected ? "已启用连接，等待官方登录或首次同步。" : "尚未连接。完成官方登录后自动采集。", Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 8) });
        }
        stack.Children.Add(new TextBlock { Text = "已连接平台在启动后及运行期间每 30 分钟同步。手动修改保留；缺失或无法确认的元数据会显示。雨课堂题目进度与提交记录分别标明，截止时间按北京时间读取。其余四个平台仍可手动录入。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 16, 0, 0) });
        var moocSettings = store.LoadMoocSettings();
        stack.Children.Add(new TextBlock { Text = "中国大学 MOOC 同步详情", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 10) });
        stack.Children.Add(new TextBlock { Text = "上次检查：" + (moocSettings.LastSyncAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未检查") });
        if (moocSettings.LastSummary is { } mooc)
        {
            stack.Children.Add(new TextBlock { Text = $"已读取 {mooc.CoursesRead}/{mooc.SelectedCourses ?? mooc.Courses} 门已选课程（发现 {mooc.Courses} 门，含 {mooc.SpocCourses} 门 SPOC）；本轮 {mooc.Tasks} 条任务，新增 {mooc.Added}、更新 {mooc.Updated}。"
                + $"含新版考试 {mooc.NewExamTasks} 条；有截止时间 {mooc.TasksWithDeadline} 条、直达入口 {mooc.TasksWithLink} 条、状态待核对 {mooc.StatusNeedsReview} 条。"
                + (mooc.CourseListCovered ? "课程列表分页已读完。" : "课程分页覆盖不完整，请核对官方课程。"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
            foreach (var problem in mooc.Problems)
                stack.Children.Add(new TextBlock { Text = problem.Code + "：" + (problem.Course.Length > 0 ? problem.Course + " — " : "") + problem.Message,
                    Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        }
        else stack.Children.Add(new TextBlock { Text = moocSettings.Connected ? "已启用连接，等待官方登录或首次同步。" : "尚未连接。完成官方登录后自动采集。", Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 8) });
        new Window { Owner = this, Title = "各平台同步详情", Width = 820, Height = 680, Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.Show();
    }
    private void ExitApplication(object sender, RoutedEventArgs args) => exit();

    private void AddTask(object sender, RoutedEventArgs args)
    {
        var editor = new TaskEditorWindow { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        RunAction(() => { store.Create(editor.Result, DateTimeOffset.UtcNow); ShowSuccess("任务已保存，重启后仍可查看。"); });
    }

    private void EditTask(object sender, RoutedEventArgs args)
    {
        if (TaskTable.SelectedItem is not TaskDisplayRow row) return;
        var editor = new TaskEditorWindow(row.Task) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        RunAction(() => { store.Edit(row.Task.Id, row.Task.Revision, editor.Result, DateTimeOffset.UtcNow); ShowSuccess("任务已更新，提醒计划已重新核对。"); });
    }

    private async void GoTask(object sender, RoutedEventArgs args)
    {
        var row = (sender as FrameworkElement)?.DataContext as TaskDisplayRow ?? TaskTable.SelectedItem as TaskDisplayRow;
        if (row is null || taskOpenCancellation is not null) return;
        TaskTable.SelectedItem = row;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        taskOpenCancellation = cancellation;
        UpdateSelection();
        ShowSuccess("正在确认这条任务的官方做题 / 提交入口…");
        try
        {
            var result = await taskNavigation.OpenAsync(row.Task.Id, cancellation.Token);
            if (!permitClose) ShowSuccess(TaskNavigationRules.OpenMessage(result) + " 浏览器沿用自己的登录状态。");
        }
        catch (OperationCanceledException) { if (!permitClose) ShowError("LINK003", "打开作业已取消或超时，请再点击一次。"); }
        catch (TaskValidationException error) { if (!permitClose) ShowError(error.Code, error.Message); }
        catch { if (!permitClose) ShowError("LINK001", "无法打开作业，请检查系统浏览器或同步后重试。"); }
        finally
        {
            taskOpenCancellation = null;
            if (!permitClose) UpdateSelection();
        }
    }

    private void SetState(TaskState next)
    {
        if (TaskTable.SelectedItem is not TaskDisplayRow row) return;
        RunAction(() => { store.SetState(row.Task.Id, row.Task.Revision, next, DateTimeOffset.UtcNow); ShowSuccess("已标记：" + TaskRules.StateName(next)); });
    }
    private void CompleteTask(object sender, RoutedEventArgs args) => SetState(TaskState.Completed);
    private void ReturnTask(object sender, RoutedEventArgs args) => SetState(TaskState.Returned);
    private void IgnoreTask(object sender, RoutedEventArgs args) => SetState(TaskState.Ignored);
    private void RestoreTask(object sender, RoutedEventArgs args) => SetState(TaskState.Pending);

    private void DeleteTask(object sender, RoutedEventArgs args)
    {
        if (TaskTable.SelectedItem is not TaskDisplayRow row) return;
        if (MessageBox.Show(this, "删除“" + row.Task.Title + "”？删除后无法恢复，相关提醒也会取消。",
            "删除任务", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        RunAction(() => { store.Delete(row.Task.Id, row.Task.Revision); ShowSuccess("任务已删除。"); });
    }

    private void CopyExtension(object sender, RoutedEventArgs args)
    {
        RunAction(() => { Clipboard.SetText(TaskRules.ExtensionText); ShowSuccess("延期申请已复制，可自行修改后发给老师。"); });
    }

    private void RunAction(Action action)
    {
        try { action(); }
        catch (TaskValidationException error) { ShowError(error.Code, error.Message); RefreshTasks(); }
        catch { ShowError("APP001", "操作没有完成。可重新打开任务重试，或查看 README 故障说明。"); }
    }

    public void ShowSuccess(string text) { ResultText.Foreground = new SolidColorBrush(Color.FromRgb(34, 91, 68)); ResultText.Text = text; }
    public void ShowError(string code, string text) { ResultText.Foreground = Brushes.Firebrick; ResultText.Text = code + "：" + text; }

    private void OpenSettings(object sender, RoutedEventArgs args)
    {
        try
        {
            var window = new Window { Owner = this, Title = "设置与平台状态", Width = 820, Height = 720, MinWidth = 660, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var stack = new StackPanel { Margin = new Thickness(24) };
            stack.Children.Add(new TextBlock { Text = "设置与平台", FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
            var autoSync = new CheckBox { Content = "启动时及运行期间自动同步已连接平台（每 30 分钟）", IsChecked = store.LoadSettings().AutomaticSyncEnabled, Margin = new Thickness(0, 0, 0, 12) };
            autoSync.Click += (_, _) => RunAction(() =>
            {
                var settings = store.LoadSettings() with { AutomaticSyncEnabled = autoSync.IsChecked == true };
                store.SaveSettings(settings); AutomaticSyncSettingChanged?.Invoke(settings.AutomaticSyncEnabled);
            });
            stack.Children.Add(autoSync);
            foreach (var platform in RainPlatform.All)
            {
                var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
                var current = store.LoadPlatformSettings(platform.Id);
                var platformSync = new CheckBox { Content = "自动同步" + platform.Name, IsChecked = current.AutomaticSyncEnabled,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                platformSync.Click += (_, _) => RunAction(() =>
                {
                    var updated = store.LoadPlatformSettings(platform.Id) with { AutomaticSyncEnabled = platformSync.IsChecked == true };
                    store.SavePlatformSettings(platform.Id, updated);
                    if (rainServices.TryGetValue(platform.Id, out var service)) service.SettingsChanged(updated.AutomaticSyncEnabled && store.LoadSettings().AutomaticSyncEnabled);
                });
                row.Children.Add(platformSync);
                var connect = new Button { Content = "连接" + platform.Name, Padding = new Thickness(12, 5, 12, 5) };
                connect.Click += (_, _) => PlatformConnectionRequested?.Invoke(platform.Id); row.Children.Add(connect);
                stack.Children.Add(row);
            }
            var moocRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            var moocAuto = new CheckBox { Content = "自动同步中国大学 MOOC", IsChecked = store.LoadMoocSettings().AutomaticSyncEnabled,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            moocAuto.Click += (_, _) => RunAction(() =>
            {
                var updated = store.LoadMoocSettings() with { AutomaticSyncEnabled = moocAuto.IsChecked == true };
                store.SaveMoocSettings(updated); moocSync?.SettingsChanged(updated.AutomaticSyncEnabled && store.LoadSettings().AutomaticSyncEnabled);
            });
            moocRow.Children.Add(moocAuto);
            var moocConnect = new Button { Content = "连接中国大学 MOOC", Padding = new Thickness(12, 5, 12, 5) };
            moocConnect.Click += (_, _) => PlatformConnectionRequested?.Invoke(MoocPlatform.Id); moocRow.Children.Add(moocConnect); stack.Children.Add(moocRow);
            var enabled = new CheckBox { Content = "开启任务提醒（手动任务也会提醒）", IsChecked = store.LoadSettings().NotificationsEnabled, Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(enabled);
            enabled.Click += (_, _) => RunAction(() =>
            {
                var settings = store.LoadSettings() with { NotificationsEnabled = enabled.IsChecked == true };
                store.SaveSettings(settings); NotificationSettingChanged?.Invoke(settings.NotificationsEnabled);
            });
            var test = new Button { Content = "发送测试提醒", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            test.Click += (_, _) =>
            {
                if (reminders?.SendTest() != true) ShowError("NOTIFY001", "请先开启提醒，再检查 Windows 是否允许此程序显示通知。");
            };
            stack.Children.Add(test);
            stack.Children.Add(new TextBlock { Text = "关闭首页后保留托盘并继续提醒。点击“退出程序”会停止提醒；下次启动会补查当前仍适用的待发计划。Windows 是否显示通知，需用测试提醒核对。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 18) });
            var table = new DataGrid { ItemsSource = PlatformCatalog.Sites, IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, Height = 235 };
            foreach (var (label, field, width) in new[] { ("平台", "Name", 140.0), ("官方域名", "Domain", 190.0), ("当前接入范围", "Status", 350.0) })
                table.Columns.Add(new DataGridTextColumn { Header = label, Binding = new System.Windows.Data.Binding(field), Width = width });
            stack.Children.Add(table);
            var courseButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var platform in PlatformCatalog.Sites)
            {
                var courses = new Button { Content = platform.Name + " · 选择记录课程", Tag = platform.Id,
                    Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 7, 12, 7) };
                courses.Click += ChooseRecordingCourses; courseButtons.Children.Add(courses);
            }
            stack.Children.Add(courseButtons);
            stack.Children.Add(new TextBlock { Text = "点击任务名称或“做题 / 提交”，直接在系统浏览器打开对应官方页面；已提交任务进入查看或提交记录。浏览器沿用自己的登录状态。学习通采集会话用于确认入口，登录失效和未读取的课程会在首页提示。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 18) });
            var history = new Button { Content = "查看提醒记录", HorizontalAlignment = HorizontalAlignment.Left };
            history.Click += (_, _) => ShowReminderHistory();
            stack.Children.Add(history);
            stack.Children.Add(new TextBlock { Text = "免责声明", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 7) });
            stack.Children.Add(new TextBlock { Text = TaskRules.Disclaimer, TextWrapping = TextWrapping.Wrap });
            var readme = new Button { Content = "打开使用与故障说明", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
            readme.Click += (_, _) => RunAction(() => Process.Start(new ProcessStartInfo(Path.Combine(projectRoot, "README.md")) { UseShellExecute = true }));
            stack.Children.Add(readme);
            window.Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            window.Show();
        }
        catch { ShowError("DB001", "无法读取设置，请检查任务库。"); }
    }

    private void ShowReminderHistory()
    {
        RunAction(() =>
        {
            var rows = store.LoadReminderHistory().Select(item => new
            {
                Task = tasks.FirstOrDefault(task => task.Id == item.Plan.TaskId)?.Title ?? "任务已删除",
                Kind = ReminderPlanner.KindName(item.Plan.Kind), Due = item.Plan.DueAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                State = item.State == "requested" ? "已请求系统通知" : item.State == "processing" ? "处理中" : "待提醒",
                Error = item.ErrorCode ?? "", Attempts = item.Attempts
            }).ToArray();
            var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, Margin = new Thickness(16) };
            foreach (var (header, property) in new[] { ("任务", "Task"), ("提醒", "Kind"), ("计划时间", "Due"), ("状态", "State"), ("错误码", "Error"), ("尝试", "Attempts") })
                grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            new Window { Owner = this, Title = "提醒记录 · 系统请求结果", Content = grid, Width = 980, Height = 440 }.Show();
        });
    }

    public sealed record TaskDisplayRow(TaskItem Task, DateTimeOffset Now)
    {
        public string Title => Task.Title;
        public bool CanOpen => TaskRules.IsSafeLink(Task.Link);
        public string OpenHint => TaskRules.IsActive(Task) ? "打开该任务的官方做题 / 提交页面" : "打开该任务的官方查看 / 提交记录";
        public string Course => Task.CourseName.Length == 0 ? "课程待补充" : Task.CourseName;
        public string Platform => PlatformCatalog.Name(Task.PlatformId);
        public string Deadline => TaskRules.DeadlineName(Task);
        public string Priority => TaskRules.IsActive(Task) ? "P" + TaskRules.Priority(Task, Now) + " " + (TaskRules.Priority(Task, Now) switch { 0 => "逾期", 1 => "马上到期", 2 => "临期", 3 => "不慌", _ => "待确认" }) : "—";
        public string State => TaskRules.StateName(Task.State);
        public string PlatformState => Task.PlatformStatusText.Length == 0 ? "—" : Task.PlatformStatusText;
    }
}
