using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Web.WebView2.Core;

namespace AccessProbe;

public partial class MainWindow
{
    private const int MaximumReadFrames = 12;
    private readonly HashSet<CoreWebView2Frame> workFrames = [];
    private string lastWorkReadOutcome = "not_attempted";
    private int workReadTaskCount;
    private int workReadPageCount;
    private int workReadSkippedFrameCount;
    private int workReadInvalidLinkCount;
    private int workReadMissingTitleCount;
    private int workReadUnrecognizedPageCount;
    private bool workReadRowLimitReached;

    private void TrackWorkFrame(CoreWebView2Frame frame)
    {
        if (!workFrames.Add(frame)) return;
        var state = new FrameNavigationState();
        frameNavigationStates.Add(frame, state);
        frame.FrameCreated += (_, created) => TrackWorkFrame(created.Frame);
        frame.NavigationStarting += (_, navigation) => { if (!closing) FrameNavigationStarting(frame, navigation); };
        frame.NavigationCompleted += (_, navigation) =>
        {
            if (navigation.NavigationId == state.NavigationId && navigation.IsSuccess) state.Upgrade.Reset();
        };
        frame.Destroyed += (_, _) =>
        {
            workFrames.Remove(frame);
            frameNavigationStates.Remove(frame);
            if (!closing) InvalidateCollection();
        };
    }

    private void ResetWorkReadDiagnostics()
    {
        lastWorkReadOutcome = "not_attempted";
        workReadTaskCount = workReadPageCount = workReadSkippedFrameCount = workReadInvalidLinkCount = 0;
        workReadMissingTitleCount = workReadUnrecognizedPageCount = 0;
        workReadRowLimitReached = false;
    }

    private async void ReadCurrentWorkPage(object sender, RoutedEventArgs args)
    {
        if (!initialized || clearing || closing || collectionCancellation is not null) return;
        InvalidateCollection(clearBrowserProbe: false, clearNoticeProbe: false);
        var readRevision = collectionRevision;
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        collectionCancellation = cancellation;
        CollectButton.IsEnabled = BrowserProbeButton.IsEnabled = ReadWorkButton.IsEnabled = false;
        StatusText.Text = "正在读取已加载作业页；不会自动翻页或请求其他课程。";
        try
        {
            var snapshots = new List<WorkPageSnapshot>();
            var skippedFrames = Math.Max(0, workFrames.Count - MaximumReadFrames);
            snapshots.Add(WorkPageSnapshotReader.Parse(await Browser.CoreWebView2
                .ExecuteScriptAsync(WorkPageSnapshotReader.Script).WaitAsync(cancellation.Token)));
            foreach (var frame in workFrames.ToArray().Take(MaximumReadFrames))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    if (frame.IsDestroyed() != 0) { skippedFrames++; continue; }
                    snapshots.Add(WorkPageSnapshotReader.Parse(await frame
                        .ExecuteScriptAsync(WorkPageSnapshotReader.Script).WaitAsync(cancellation.Token)));
                }
                catch (OperationCanceledException) { throw; }
                catch { skippedFrames++; }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (closing || clearing || collectionRevision != readRevision) return;

            var lists = snapshots.Where(snapshot => snapshot.List?.Outcome == "parsed_partial").ToArray();
            var details = snapshots.Where(snapshot => snapshot.Detail?.Outcome == "parsed_time_window").ToArray();
            workReadPageCount = lists.Length + details.Length;
            workReadSkippedFrameCount = skippedFrames;
            workReadRowLimitReached = lists.Any(snapshot => snapshot.RowLimitReached);
            workReadMissingTitleCount = lists.Sum(snapshot => snapshot.List!.MissingTitles);
            workReadUnrecognizedPageCount = snapshots.Count(snapshot =>
                snapshot.Outcome is "response_too_large" or "ambiguous_detail" or "page_loading" or "snapshot_unrecognized"
                || snapshot.List is { Outcome: not "parsed_partial" }
                || snapshot.Detail is { Outcome: not "parsed_time_window" });
            var readAt = DateTimeOffset.UtcNow;
            if (lists.Length > 0)
            {
                var tasks = lists.SelectMany(snapshot => snapshot.List!.Tasks).ToArray();
                workReadTaskCount = tasks.Length;
                workReadInvalidLinkCount = lists.Sum(snapshot => snapshot.List!.InvalidLinks);
                lastWorkReadOutcome = "parsed_partial";
                StatusText.Text = $"已读取 {tasks.Length} 条作业（{lists.Length} 个已加载页面）。{WorkReadCoverageText()}";
                ShowWorkList(tasks, readAt);
            }
            else if (details.Length > 0)
            {
                lastWorkReadOutcome = "parsed_time_window";
                StatusText.Text = $"已读取当前作业的作答时间原文；完整年份或平台时区仍需确认。{WorkReadCoverageText()}";
                ShowWorkDetails(details, readAt);
            }
            else
            {
                lastWorkReadOutcome = snapshots.Any(snapshot => snapshot.Outcome == "response_too_large")
                    ? "response_too_large" : "not_found";
                StatusText.Text = lastWorkReadOutcome == "response_too_large"
                    ? "作业信息超过读取上限，未采用结果。"
                    : "没有找到可识别的已加载作业列表或详情。请在本窗口登录并进入课程“作业”页，再读取；这不表示没有作业。";
            }
        }
        catch (OperationCanceledException)
        {
            // Only a timeout of the current read may change its status. Navigation owns newer UI state.
            if (!closing && !clearing && collectionRevision == readRevision)
            {
                lastWorkReadOutcome = "timed_out";
                StatusText.Text = "本次作业读取超时。请等待页面加载后重试，未采用本次结果。";
            }
        }
        catch
        {
            if (!closing && !clearing && collectionRevision == readRevision)
            {
                lastWorkReadOutcome = "read_error";
                StatusText.Text = "作业页读取未完成。请等待页面加载后重试；未记录页面地址、账号或异常正文。";
            }
        }
        finally
        {
            if (ReferenceEquals(collectionCancellation, cancellation)) collectionCancellation = null;
            cancellation.Dispose();
            if (!closing && !clearing)
                CollectButton.IsEnabled = BrowserProbeButton.IsEnabled = ReadWorkButton.IsEnabled = initialized && collectionCancellation is null;
        }
    }

    private string WorkReadCoverageText()
    {
        var limitations = new List<string>();
        if (workReadRowLimitReached) limitations.Add("单页超过 500 条，只读取前 500 条");
        if (workReadSkippedFrameCount > 0) limitations.Add($"{workReadSkippedFrameCount} 个内嵌页面未读取");
        if (workReadMissingTitleCount > 0) limitations.Add($"{workReadMissingTitleCount} 个条目缺少名称，未加入清单");
        if (workReadUnrecognizedPageCount > 0) limitations.Add($"{workReadUnrecognizedPageCount} 个页面的作业结构未能解析");
        if (workReadInvalidLinkCount > 0) limitations.Add($"{workReadInvalidLinkCount} 条作业的链接未解析");
        return (limitations.Count > 0 ? string.Join("；", limitations) + "。" : string.Empty)
            + "仅覆盖已加载页面，未自动翻页，未确认课程任务完整性。";
    }

    private void ShowWorkList(IReadOnlyList<WorkListTask> tasks, DateTimeOffset readAt)
    {
        var panel = new DockPanel { Margin = new Thickness(16) };
        var window = new Window { Owner = this, Title = "当前作业列表 · 页面读取", Width = 1050, Height = 520, MinWidth = 760, MinHeight = 360, Content = panel };
        var resultClosed = false;
        var saving = false;
        window.Closed += (_, _) => resultClosed = true;
        var summary = new TextBlock
        {
            Text = $"读取时间：{readAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}。共 {tasks.Count} 条作业，状态按原平台文字展示。{WorkReadCoverageText()}点击“保存到任务清单”可保留到本地；重复导入保留你的手动状态与修改。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);
        var actions = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var row = new WrapPanel();
        var import = new Button { Content = "保存到任务清单", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 6) };
        row.Children.Add(import);
        var embedded = new Button { Content = "在验证窗口打开选中作业", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 6) };
        var external = new Button { Content = "在系统浏览器打开", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(embedded);
        row.Children.Add(external);
        var courseRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        courseRow.Children.Add(new TextBlock { Text = "课程名称（可选）：", VerticalAlignment = VerticalAlignment.Center });
        var course = new TextBox { Width = 260, Padding = new Thickness(6, 4, 6, 4) };
        System.Windows.Automation.AutomationProperties.SetName(course, "导入课程名称");
        courseRow.Children.Add(course);
        actions.Children.Add(courseRow);
        actions.Children.Add(row);
        import.Click += async (_, _) =>
        {
            if (saving || resultClosed || closing) return;
            saving = true;
            import.IsEnabled = course.IsEnabled = false;
            import.Content = "正在保存…";
            summary.Text = $"正在保存 {tasks.Count} 条作业，完成后可在任务清单查看。";
            var courseName = course.Text;
            try
            {
                var result = await Task.Run(() => taskStore.ImportWorks(tasks, courseName, readAt));
                if (resultClosed || closing || Dispatcher.HasShutdownStarted) return;
                summary.Text = $"已保存：新增 {result.Added} 条，更新 {result.Updated} 条，重复行 {result.DuplicateRows} 条。"
                    + (result.Unresolved > 0 ? $" {result.Unresolved} 条缺少可用任务标识，每次保存会作为独立任务，链接请手动补充。" : "")
                    + " 已完成、已提交和待批阅会同步为完成；手动状态修改保留。仅覆盖本次已加载页面。";
                StatusText.Text = summary.Text;
                if (showDashboard is not null) showDashboard();
            }
            catch (TaskValidationException error)
            {
                if (!resultClosed && !closing) summary.Text = error.Code + "：" + error.Message;
            }
            catch
            {
                if (!resultClosed && !closing) summary.Text = "DB001：保存未完成，请检查本地任务库和目录写入权限。";
            }
            finally
            {
                saving = false;
                if (!resultClosed && !closing)
                {
                    import.IsEnabled = course.IsEnabled = true;
                    import.Content = "保存到任务清单";
                }
            }
        };
        actions.Children.Add(new TextBlock
        {
            Text = "系统浏览器使用自己的登录状态，可能需要重新登录。保存后状态随平台同步；推定或估算的截止日期会标明，可在编辑中核对后开启提醒。",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray
        });
        DockPanel.SetDock(actions, Dock.Top);
        panel.Children.Add(actions);
        var table = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
            ClipboardCopyMode = DataGridClipboardCopyMode.None, SelectionMode = DataGridSelectionMode.Single,
            ItemsSource = tasks.Select(task => new WorkListDisplayRow(task)).ToArray()
        };
        foreach (var (header, property, weight) in new[]
        {
            ("作业名称", "Title", 2.0), ("平台显示状态", "StatusText", 1.0),
            ("页面时间原文", "TimeText", 1.5), ("链接", "LinkText", 1.0)
        })
            table.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = new DataGridLength(weight, DataGridLengthUnitType.Star) });
        void SelectionChanged()
        {
            var canOpen = table.SelectedItem is WorkListDisplayRow { Task.Location: not null };
            embedded.IsEnabled = external.IsEnabled = canOpen;
        }
        table.SelectionChanged += (_, _) => SelectionChanged();
        embedded.Click += (_, _) => OpenObservedWork((table.SelectedItem as WorkListDisplayRow)?.Task, false);
        external.Click += (_, _) => OpenObservedWork((table.SelectedItem as WorkListDisplayRow)?.Task, true);
        if (tasks.Count > 0) table.SelectedIndex = 0;
        SelectionChanged();
        panel.Children.Add(table);
        resultsWindow = window;
        window.Show();
    }

    private void OpenObservedWork(WorkListTask? task, bool external)
    {
        if (!initialized || closing || clearing || task?.Location is not { } location) return;
        var verified = WorkListParser.ParseTaskLocation(location.PageUri.OriginalString);
        if (verified is null) { StatusText.Text = "当前作业链接未解析，无法打开。请返回课程作业页重新读取。"; return; }
        try
        {
            if (external)
                Process.Start(new ProcessStartInfo(verified.PageUri.OriginalString) { UseShellExecute = true });
            else
                Browser.CoreWebView2.Navigate(verified.PageUri.OriginalString);
        }
        catch { StatusText.Text = "无法打开作业链接，请确认系统浏览器可用或重新读取当前作业页。"; }
    }

    private void ShowWorkDetails(IReadOnlyList<WorkPageSnapshot> snapshots, DateTimeOffset readAt)
    {
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock { Text = $"读取时间：{readAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}", Margin = new Thickness(0, 0, 0, 12) });
        foreach (var snapshot in snapshots)
        {
            var detail = snapshot.Detail!;
            stack.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(detail.Title) ? "当前作业" : detail.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock { Text = $"作答开始：{detail.AnswerWindow!.StartText}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            stack.Children.Add(new TextBlock { Text = $"作答结束：{detail.AnswerWindow.EndText}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock
            {
                Text = detail.AnswerWindow.Resolution == "year_unconfirmed"
                    ? "保存后按北京时间及推定年份显示截止日期并排序；可在编辑中核对日期后开启提醒。"
                    : "保存后按北京时间生成截止日期；你已有的手动修改继续保留。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            });
            var save = new Button { Content = "保存时间原文到这条任务", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
            var resultText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
            save.Click += (_, _) =>
            {
                try
                {
                    if (snapshot.DetailLocation is null) throw new TaskValidationException("LINK003", "当前详情地址无法关联任务，请从已保存的作业列表打开后再读取。");
                    var task = taskStore.SaveDetail(snapshot.DetailLocation, detail, readAt);
                    resultText.Text = "时间原文已保存；已有的手动截止时间和完成状态均保留。";
                    if (showDashboard is not null) showDashboard();
                }
                catch (TaskValidationException error) { resultText.Text = error.Code + "：" + error.Message; }
                catch { resultText.Text = "DB001：保存未完成，请检查本地任务库。"; }
            };
            stack.Children.Add(save); stack.Children.Add(resultText);
        }
        resultsWindow = new Window
        {
            Owner = this, Title = "当前作业 · 详情读取", Width = 760, Height = 460, MinWidth = 520, MinHeight = 330,
            Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        resultsWindow.Show();
    }

    private sealed record WorkListDisplayRow(WorkListTask Task)
    {
        public string Title => Task.Title;
        public string StatusText => string.IsNullOrWhiteSpace(Task.PlatformStatusText) ? "未知" : Task.PlatformStatusText;
        public string TimeText => string.IsNullOrWhiteSpace(Task.DisplayedTimeText) ? "未显示" : Task.DisplayedTimeText;
        public string LinkText => Task.Location is null ? "链接未解析" : "可打开";
    }
}
