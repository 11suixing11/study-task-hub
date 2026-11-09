using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace AccessProbe;

public sealed class CourseSelectionWindow : Window
{
    private readonly TaskStore store;
    private readonly Func<string, Task> refreshCatalog;
    private readonly Func<string, Task> applySelection;
    private readonly Action<string> connect;
    private readonly bool fixedPlatform;
    private readonly string initialPlatformId;
    private readonly Action<string, bool>? setAutomatic;
    private readonly Action<string>? cancelSync;
    private readonly TextBlock sessionStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly CheckBox automatic = new() { Content = "每 30 分钟同步已选课程", Margin = new Thickness(0, 6, 0, 8) };
    private readonly Button stop = Button("停止本轮采集");
    private readonly Expander coursesSection = new() { Header = "课程与采集范围", IsExpanded = true, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly ComboBox platforms = new() { MinWidth = 210, DisplayMemberPath = "Name" };
    private readonly ProductIcon platformArtwork = new() { Tile = true, Width = 38, Height = 38,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock platformHeading = new() { FontSize = 22, FontWeight = FontWeights.SemiBold };
    private readonly Wpf.Ui.Controls.TextBox search = new()
    {
        PlaceholderText = "搜索课程名称、班级或开课期…",
        Icon = new ProductIcon { Key = "search" },
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly CheckBox recordAll = new() { Visibility = Visibility.Collapsed };
    private readonly CheckBox hideHistory = new() { Content = "隐藏未选课程的历史任务（保留数据，重新勾选可恢复）", Margin = new Thickness(0, 8, 0, 6) };
    private readonly TextBlock catalogStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly TextBlock selectedCount = new() { Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly ListBox list = new() { BorderBrush = new SolidColorBrush(Color.FromRgb(218, 225, 235)), Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button refresh = Button("刷新课程列表");
    private readonly Button login = Button("连接平台");
    private readonly Button save = Button("保存选择并采集");
    private readonly Dictionary<string, Draft> drafts = new(StringComparer.Ordinal);
    private Draft? current;
    private bool loading, busy, closing, catalogRefreshing, syncRunning;
    private double expandedHeight = 800;
    private readonly TextBlock syncDetails = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), Foreground = Brushes.DimGray };
    private string PlatformId => (platforms.SelectedItem as PlatformSite)?.Id ?? "chaoxing";

    public CourseSelectionWindow(TaskStore store, string platformId, Func<string, Task> refreshCatalog,
        Func<string, Task> applySelection, Action<string> connect, bool fixedPlatform = false,
        Action<string, bool>? setAutomatic = null, Action<string>? cancelSync = null)
    {
        this.store = store; this.refreshCatalog = refreshCatalog; this.applySelection = applySelection; this.connect = connect;
        this.fixedPlatform = fixedPlatform; initialPlatformId = platformId; this.setAutomatic = setAutomatic; this.cancelSync = cancelSync;
        Title = PlatformCatalog.Name(platformId) + " · 平台管理"; Width = 880; Height = 800; MinWidth = 760; MinHeight = 620;
        Icon = ProductIcons.AppImage;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AccessProbe;component/FluentTheme.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "CanvasBrush"); UseLayoutRounding = true;
        NameScope.SetNameScope(this, new NameScope());
        var root = new DockPanel { Margin = new Thickness(24) }; var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        var titleRow = new DockPanel();
        var collapse = Button("收起课程区"); collapse.SetResourceReference(StyleProperty, "TextButton"); DockPanel.SetDock(collapse, Dock.Right); titleRow.Children.Add(collapse);
        var brandRow = new Grid(); brandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        brandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        platformArtwork.Key = platformId; brandRow.Children.Add(platformArtwork);
        platformHeading.Text = fixedPlatform ? PlatformCatalog.Name(platformId) : "选择记录课程";
        var headingLabels = new StackPanel(); headingLabels.Children.Add(platformHeading);
        var subtitle = new TextBlock { Text = "登录后先读取全部课程，再按你的选择采集任务。", FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); headingLabels.Children.Add(subtitle);
        Grid.SetColumn(headingLabels, 1); brandRow.Children.Add(headingLabels);
        titleRow.Children.Add(brandRow); header.Children.Add(titleRow);
        var connectionArea = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 10, 0, 8), BorderThickness = new Thickness(1) };
        connectionArea.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        connectionArea.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var connectionBody = new StackPanel(); connectionArea.Child = connectionBody;
        sessionStatus.FontWeight = FontWeights.SemiBold;
        var sessionScroller = new ScrollViewer { Content = sessionStatus, MaxHeight = 48, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        connectionBody.Children.Add(sessionScroller);
        var synchronize = Button("同步已选课程"); synchronize.Click += async (_, _) => await SaveAsync();
        ProductVisuals.SetButtonIcon(login, "connect"); ProductVisuals.SetButtonIcon(refresh, "sync");
        ProductVisuals.SetButtonIcon(synchronize, "sync"); ProductVisuals.SetButtonIcon(stop, "stop");
        ProductVisuals.SetButtonIcon(save, "save"); ProductVisuals.SetButtonIcon(collapse, "sidebar");
        var toolbar = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) }; toolbar.Children.Add(platforms); toolbar.Children.Add(login); toolbar.Children.Add(refresh); toolbar.Children.Add(synchronize); toolbar.Children.Add(stop); connectionBody.Children.Add(toolbar);
        login.SetResourceReference(StyleProperty, "PrimaryButton"); stop.SetResourceReference(StyleProperty, "TextButton");
        platforms.Visibility = fixedPlatform ? Visibility.Collapsed : Visibility.Visible;
        stop.Visibility = cancelSync is null ? Visibility.Collapsed : Visibility.Visible;
        automatic.Visibility = setAutomatic is null ? Visibility.Collapsed : Visibility.Visible;
        connectionBody.Children.Add(automatic); header.Children.Add(connectionArea);
        var courseHeader = new StackPanel(); courseHeader.Children.Add(recordAll);
        search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "搜索课程"); search.ToolTip = "搜索课程名称、班级或开课期";
        courseHeader.Children.Add(search);
        var bulk = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; var all = Button("全选当前列表"); var none = Button("全不选当前列表"); bulk.Children.Add(all); bulk.Children.Add(none);
        ProductVisuals.SetButtonIcon(all, "check-all"); ProductVisuals.SetButtonIcon(none, "clear-selection");
        all.SetResourceReference(StyleProperty, "TextButton"); none.SetResourceReference(StyleProperty, "TextButton");
        selectedCount.FontSize = 12; selectedCount.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); catalogStatus.FontSize = 12; catalogStatus.Margin = new Thickness(0, 4, 0, 10);
        var catalogScroller = new ScrollViewer { Content = catalogStatus, MaxHeight = 58, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        courseHeader.Children.Add(bulk); courseHeader.Children.Add(selectedCount); courseHeader.Children.Add(catalogScroller); header.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); footer.Children.Add(hideHistory);
        var resultScroller = new ScrollViewer { Content = result, MaxHeight = 52, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        footer.Children.Add(resultScroller);
        var details = new Expander { Header = "平台同步详情", Content = new ScrollViewer { Content = syncDetails, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        footer.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = Button("关闭"); cancel.Click += (_, _) => Close(); actions.Children.Add(cancel); actions.Children.Add(save);
        save.SetResourceReference(StyleProperty, "PrimaryButton"); footer.Children.Add(actions); root.Children.Add(footer);
        var template = new DataTemplate(typeof(CourseChoice));
        var check = new FrameworkElementFactory(typeof(CheckBox)); check.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 8, 9, 8));
        check.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center); check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(CourseChoice.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var labels = new FrameworkElementFactory(typeof(StackPanel)); var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(CourseChoice.Name))); name.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); labels.AppendChild(name);
        var detail = new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(CourseChoice.Detail)));
        detail.SetValue(TextBlock.ForegroundProperty, Brushes.DimGray); detail.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        detail.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0)); labels.AppendChild(detail); check.AppendChild(labels); template.VisualTree = check;
        list.ItemTemplate = template; list.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush"); list.BorderThickness = new Thickness(0);
        var courseRowStyle = new Style(typeof(ListBoxItem));
        courseRowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        courseRowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        courseRowStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("RowHoverBrush"))); courseRowStyle.Triggers.Add(hover);
        list.ItemContainerStyle = courseRowStyle;
        // Reuse course containers when catalog refreshes reset the filtered view.
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var courseBody = new DockPanel(); DockPanel.SetDock(courseHeader, Dock.Top); courseBody.Children.Add(courseHeader); courseBody.Children.Add(list);
        var courseFrame = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Padding = new Thickness(8), Child = courseBody };
        courseFrame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        courseFrame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        coursesSection.Content = courseFrame; root.Children.Add(coursesSection); Content = root;
        void FitCompactManager()
        {
            var compact = ActualHeight < 720;
            root.Margin = new Thickness(compact ? 12 : 24);
            platformHeading.FontSize = compact ? 20 : 22;
            subtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            platformArtwork.Width = platformArtwork.Height = compact ? 32 : 38;
            courseFrame.Padding = new Thickness(compact ? 4 : 8);
            connectionArea.Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(14, 10, 14, 10);
            sessionStatus.Margin = compact ? new Thickness(0, 2, 0, 2) : new Thickness(0, 4, 0, 6);
            automatic.Margin = compact ? new Thickness(0, 2, 0, 4) : new Thickness(0, 6, 0, 8);
            hideHistory.Margin = compact ? new Thickness(0, 4, 0, 2) : new Thickness(0, 8, 0, 6);
            search.Margin = new Thickness(0, compact ? 6 : 10, 0, 0);
            catalogStatus.Margin = new Thickness(0, 2, 0, compact ? 2 : 8);
            catalogScroller.MaxHeight = compact ? 32 : 58;
            sessionScroller.MaxHeight = compact ? 36 : 48;
            resultScroller.MaxHeight = compact ? 36 : 52;
            selectedCount.Margin = new Thickness(0, 2, 0, 2);
            actions.Margin = new Thickness(0, compact ? 8 : 12, 0, 0);
            foreach (var button in new[] { all, none })
            {
                button.MinHeight = compact ? 28 : 36;
                button.Padding = compact ? new Thickness(8, 3, 8, 3) : new Thickness(12, 7, 12, 7);
                button.Margin = new Thickness(0, 0, 8, compact ? 0 : 4);
            }
        }
        SizeChanged += (_, _) => FitCompactManager();
        Loaded += (_, _) => FitCompactManager();
        collapse.Click += (_, _) => coursesSection.IsExpanded = !coursesSection.IsExpanded;
        coursesSection.Collapsed += (_, _) => { expandedHeight = Height; MinHeight = 460; Height = 480; collapse.Content = "展开课程区"; };
        coursesSection.Expanded += (_, _) => { MinHeight = 620; Height = Math.Max(expandedHeight, MinHeight); collapse.Content = "收起课程区"; };
        stop.Click += (_, _) => cancelSync?.Invoke(PlatformId);
        automatic.Click += (_, _) => setAutomatic?.Invoke(PlatformId, automatic.IsChecked == true);
        RegisterName("RecordingPlatformArtwork", platformArtwork); RegisterName("RecordingPlatformHeading", platformHeading);
        RegisterName("RecordingPlatformSelector", platforms); RegisterName("RecordingCourseSearch", search); RegisterName("RecordingAllCourses", recordAll);
        RegisterName("RecordingHideHistory", hideHistory); RegisterName("RecordingCourseList", list); RegisterName("RecordingSaveButton", save);
        RegisterName("RecordingRefreshButton", refresh); RegisterName("RecordingSelectAllButton", all); RegisterName("RecordingSelectNoneButton", none);
        RegisterName("RecordingResult", result); RegisterName("RecordingCatalogStatus", catalogStatus);
        RegisterName("RecordingCancelButton", cancel); RegisterName("RecordingConnectionButton", login);
        RegisterName("PlatformSessionStatus", sessionStatus); RegisterName("PlatformAutomaticSync", automatic);
        RegisterName("PlatformStopButton", stop); RegisterName("PlatformCoursesExpander", coursesSection); RegisterName("PlatformCollapseButton", collapse);
        RegisterName("PlatformSyncButton", synchronize); RegisterName("PlatformDetailsExpander", details); RegisterName("PlatformSyncDetails", syncDetails);
        platforms.ItemsSource = fixedPlatform ? PlatformCatalog.Sites.Where(site => site.Id == platformId).ToArray() : PlatformCatalog.Sites;
        platforms.SelectionChanged += async (_, _) => { if (!loading && !closing) await LoadPlatformAsync(); };
        search.TextChanged += (_, _) => UpdateFilter();
        recordAll.Checked += (_, _) => ChangeAllMode(); recordAll.Unchecked += (_, _) => ChangeAllMode();
        hideHistory.Checked += (_, _) => { if (!loading && current is not null) current.HideHistory = true; };
        hideHistory.Unchecked += (_, _) => { if (!loading && current is not null) current.HideHistory = false; };
        all.Click += (_, _) => SelectVisible(true); none.Click += (_, _) => SelectVisible(false);
        refresh.Click += async (_, _) => await RefreshAsync(); login.Click += (_, _) => connect(PlatformId);
        save.Click += async (_, _) => await SaveAsync();
        Closed += (_, _) => closing = true;
        store.Changed += CatalogChanged;
        Closed += (_, _) => store.Changed -= CatalogChanged;
        loading = true; platforms.SelectedItem = PlatformCatalog.Sites.FirstOrDefault(site => site.Id == platformId) ?? PlatformCatalog.Sites[0]; loading = false;
        Loaded += async (_, _) => await LoadPlatformAsync();
    }

    private static Button Button(string text) => new Wpf.Ui.Controls.Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 4) };

    private async Task LoadPlatformAsync()
    {
        platformArtwork.Key = PlatformId;
        platformHeading.Text = fixedPlatform ? PlatformCatalog.Name(PlatformId) : "选择记录课程";
        var platformId = PlatformId;
        if (!drafts.TryGetValue(platformId, out var draft))
        {
            try
            {
                var settings = await Task.Run(() => store.LoadCourseRecordingOverview(platformId));
                if (closing || PlatformId != platformId) return;
                drafts[platformId] = draft = new(settings, CourseSelectionChanged);
            }
            catch (TaskValidationException error) { ShowResult(error.Code + "：" + error.Message, true); return; }
            catch { ShowResult("COURSE001：课程列表未能加载，请检查本地任务库。", true); return; }
        }
        current = draft; Render();
        if (CourseRecordingRules.SupportsAutomatic(platformId) && draft.Settings.CatalogCheckedAtUtc is null) await RefreshAsync();
    }

    private void Render()
    {
        if (current is null || closing) return;
        loading = true;
        try
        {
            recordAll.IsChecked = current.RecordAll; hideHistory.IsChecked = current.HideHistory;
            UpdateSessionStatus();
            list.ItemsSource = current.Choices; list.IsEnabled = !busy;
            var automatic = CourseRecordingRules.SupportsAutomatic(PlatformId);
            refresh.Content = automatic ? "刷新课程列表" : "刷新本地课程"; login.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
            catalogStatus.Text = !automatic ? PlatformId == "fif"
                    ? "FiF 暂不适配；保留手动录入与历史任务，课程来自本地任务。"
                    : "此平台当前支持手动录入，课程来自本地任务；自动课程发现尚未接入。"
                : current.Settings.LastCatalogRefresh is not null ? CourseRecordingRules.CatalogRefreshText(current.Settings)
                : current.Settings.CatalogCheckedAtUtc is { } checkedAt ? (current.Settings.CatalogComplete ? "官方课程列表已完整读取" : "官方课程列表尚未完整读取，已有课程与选择保留")
                    + " · " + checkedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "尚未读取官方课程列表。请完成平台登录后刷新；没有任务的课程也会列出。";
            if (current.Choices.Count == 0) catalogStatus.Text += "\n当前没有可选课程。";
            if (!current.Settings.SelectionConfirmed) catalogStatus.Text += "\n请确认本次课程选择，确认前只读取课程目录。";
            UpdateFilter();
        }
        finally { loading = false; }
    }

    private void ChangeAllMode()
    {
        if (loading || current is null) return;
        loading = true;
        try
        {
            current.RecordAll = recordAll.IsChecked == true;
            if (current.RecordAll) foreach (var choice in current.Choices) choice.Selected = true;
        }
        finally { loading = false; }
        UpdateFilter(); ShowResult("课程选择尚未保存，点击“保存选择并采集”生效。", false);
    }

    private void CourseSelectionChanged()
    {
        if (!loading)
        {
            if (current?.RecordAll == true && current.Choices.Any(choice => !choice.Selected)) recordAll.IsChecked = false;
            UpdateFilter(); ShowResult("课程选择尚未保存，点击“保存选择并采集”生效。", false);
        }
    }

    private void SelectVisible(bool selected)
    {
        if (current is null || busy) return;
        recordAll.IsChecked = false; current.RecordAll = false;
        foreach (var choice in current.Choices.Where(MatchesSearch)) choice.Selected = selected;
        UpdateFilter(); ShowResult("课程选择尚未保存，点击“保存选择并采集”生效。", false);
    }

    private bool MatchesSearch(CourseChoice choice)
    {
        var value = search.Text.Trim();
        return value.Length == 0 || choice.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
            || choice.Detail.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateFilter()
    {
        if (current is null) return;
        CollectionViewSource.GetDefaultView(current.Choices).Filter = item => item is CourseChoice choice && MatchesSearch(choice);
        selectedCount.Text = $"共 {current.Choices.Count} 门课程 · 已选 {current.Choices.Count(choice => choice.Selected)} 门 · 新增课程需要手动勾选";
    }

    private void SetBusy(bool value)
    {
        busy = value; platforms.IsEnabled = refresh.IsEnabled = save.IsEnabled = recordAll.IsEnabled = hideHistory.IsEnabled = !value;
        list.IsEnabled = !value;
        stop.IsEnabled = value || syncRunning;
    }

    private async Task RefreshAsync()
    {
        if (busy || closing) return; var platformId = PlatformId;
        SetBusy(true); ShowResult(CourseRecordingRules.SupportsAutomatic(platformId) ? "正在读取官方课程列表…" : "正在刷新本地课程…", false);
        var before = current?.Settings.LastCatalogRefresh?.CheckedAtUtc;
        try
        {
            if (CourseRecordingRules.SupportsAutomatic(platformId)) await refreshCatalog(platformId);
            var settings = await Task.Run(() => store.LoadCourseRecordingOverview(platformId));
            if (closing) return;
            var prior = current;
            if (prior is not null && prior.Settings.Revision == settings.Revision) prior.Refresh(settings);
            else drafts[platformId] = current = new(settings, CourseSelectionChanged);
            Render();
            ShowResult(!CourseRecordingRules.SupportsAutomatic(platformId) ? "本地课程已刷新。"
                : settings.LastCatalogRefresh is { Outcome: "completed" } completed && completed.CheckedAtUtc != before
                    ? "官方课程列表已刷新。勾选后点击“保存选择并采集”。"
                : CourseRecordingRules.CatalogRefreshText(settings) + "。请核对连接窗口或首页同步详情。",
                CourseRecordingRules.SupportsAutomatic(platformId)
                    && (settings.LastCatalogRefresh?.CheckedAtUtc == before || settings.LastCatalogRefresh?.Outcome != "completed"));
        }
        catch (TaskValidationException error) { if (!closing) ShowResult(error.Code + "：" + error.Message, true); }
        catch { if (!closing) ShowResult("COURSE001：课程刷新未完成，已有课程选择保留，请稍后重试。", true); }
        finally { if (!closing) SetBusy(false); }
    }

    private async Task SaveAsync()
    {
        if (busy || closing) return; SetBusy(true); var saved = new List<string>();
        try
        {
            foreach (var (platformId, draft) in drafts.Where(pair => pair.Value.Dirty).ToArray())
            {
                if (CourseRecordingRules.SupportsAutomatic(platformId) && draft.Settings.CatalogCheckedAtUtc is null)
                    throw new TaskValidationException("COURSE001", "请先登录并读取官方课程列表，再保存选择。");
                draft.Settings = await Task.Run(() => store.SaveCourseRecording(platformId, false,
                    draft.Choices.Where(choice => choice.Selected).Select(choice => choice.Key).ToArray(), draft.HideHistory,
                    draft.Settings.Revision, DateTimeOffset.UtcNow));
                saved.Add(platformId);
                draft.RecordAll = false;
                await applySelection(platformId);
            }
            if (saved.Count == 0) await applySelection(PlatformId);
            if (!closing) { Render(); ShowResult("课程选择已保存，后续只同步已选课程。", false); }
        }
        catch (TaskValidationException error) { if (!closing) ShowResult(error.Code + "：" + error.Message
            + (saved.Count > 0 ? " 已保存：" + string.Join("、", saved.Select(PlatformCatalog.Name)) + "。" : ""), true); }
        catch { if (!closing) ShowResult("COURSE001：保存课程选择未完成，请稍后重试。", true); }
        finally { if (!closing) SetBusy(false); }
    }

    private void CatalogChanged()
    {
        if (closing || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (closing || current is null || catalogRefreshing) return;
            UpdateSessionStatus();
            if (busy) return;
            catalogRefreshing = true;
            var platformId = PlatformId;
            try
            {
                var settings = await Task.Run(() => store.LoadCourseRecordingOverview(platformId));
                if (closing || busy || PlatformId != platformId) return;
                if (current.Settings.Revision == settings.Revision) { current.Refresh(settings); Render(); }
            }
            catch { }
            finally { catalogRefreshing = false; }
        }));
    }

    private void ShowResult(string message, bool warning) { result.Text = message; result.Foreground = warning ? Brushes.Firebrick : Brushes.DimGray; }

    private sealed class Draft
    {
        private readonly Action changed;
        public CourseRecordingSettings Settings { get; set; }
        public List<CourseChoice> Choices { get; } = [];
        public bool RecordAll { get; set; }
        public bool HideHistory { get; set; }
        public bool Dirty => !Settings.SelectionConfirmed || RecordAll != Settings.RecordAllCourses || HideHistory != Settings.HideUnselectedTasks
            || !RecordAll && !Choices.Where(choice => choice.Selected).Select(choice => choice.Key).Order(StringComparer.Ordinal)
                .SequenceEqual(Settings.SelectedCourseKeys.Order(StringComparer.Ordinal));
        public Draft(CourseRecordingSettings settings, Action changed)
        {
            Settings = settings; RecordAll = settings.RecordAllCourses; HideHistory = settings.HideUnselectedTasks; this.changed = changed; Refresh(settings);
        }
        public void Refresh(CourseRecordingSettings settings)
        {
            var selected = Choices.ToDictionary(choice => choice.Key, choice => choice.Selected, StringComparer.Ordinal);
            Settings = settings; Choices.Clear();
            foreach (var course in settings.Courses)
                Choices.Add(new(course, selected.GetValueOrDefault(course.Key, RecordAll || settings.SelectedCourseKeys.Contains(course.Key, StringComparer.Ordinal)), changed));
        }
    }

    public sealed class CourseChoice : INotifyPropertyChanged
    {
        private bool selected; private readonly Action changed;
        public string Key { get; }
        public string Name { get; }
        public string Detail { get; }
        public bool Selected
        {
            get => selected;
            set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public CourseChoice(RecordingCourse course, bool selected, Action changed)
        { Key = course.Key; Name = course.Name; Detail = course.Detail + (course.Available ? "" : " · 历史记录保留"); this.selected = selected; this.changed = changed; }
    }

    public void ShowSyncStatus(string message, bool running, bool warning)
    {
        if (closing) return;
        syncRunning = running;
        ShowResult(message, warning); stop.IsEnabled = running || busy; UpdateSessionStatus();
    }

    private void UpdateSessionStatus()
    {
        var id = fixedPlatform ? initialPlatformId : PlatformId;
        if (!CourseRecordingRules.SupportsAutomatic(id)) { sessionStatus.Text = "此平台保留手动记录。"; return; }
        var info = store.LoadPlatformSessionInfo(id);
        sessionStatus.Text = info.Description + (info.SavedAtUtc is { } saved ? " · 保存于 " + saved.ToLocalTime().ToString("MM-dd HH:mm") : "");
        sessionStatus.Foreground = info.State == "expired" ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(25, 118, 210));
        login.Content = info.State == "expired" ? "重新登录并更新" : info.CanReuse ? "打开当前登录" : "官方登录";
        var recording = store.LoadCourseRecording(id);
        var (checkedAt, outcome) = id switch
        {
            "chaoxing" => (store.LoadSettings().LastSyncAtUtc, store.LoadSettings().LastSyncOutcome),
            "mooc" => (store.LoadMoocSettings().LastSyncAtUtc, store.LoadMoocSettings().LastSyncOutcome),
            "xuetang" => (store.LoadXuetangSettings().LastSyncAtUtc, store.LoadXuetangSettings().LastSyncOutcome),
            "yuketang" or "changjiang" => (store.LoadPlatformSettings(id).LastSyncAtUtc, store.LoadPlatformSettings(id).LastSyncOutcome),
            _ => (store.LoadMetadataSettings(id).LastSyncAtUtc, store.LoadMetadataSettings(id).LastSyncOutcome)
        };
        syncDetails.Text = "课程目录：" + CourseRecordingRules.CatalogRefreshText(recording)
            + "\n任务同步：" + (outcome switch { "completed" => "完成", "partial" => "部分完成", "login_required" => "登录已过期，请重新登录", _ => "尚未完成" })
            + (checkedAt is { } time ? " · " + time.ToLocalTime().ToString("MM-dd HH:mm") : "")
            + "\n本地任务：" + store.LoadTasks().Count(task => task.PlatformId == id) + " 条；未选课程历史会保留。";
        if (setAutomatic is not null) automatic.IsChecked = id switch
        {
            "chaoxing" => store.LoadSettings().ChaoxingAutomaticSyncEnabled,
            "mooc" => store.LoadMoocSettings().AutomaticSyncEnabled,
            "xuetang" => store.LoadXuetangSettings().AutomaticSyncEnabled,
            "yuketang" or "changjiang" => store.LoadPlatformSettings(id).AutomaticSyncEnabled,
            _ => store.LoadMetadataSettings(id).AutomaticSyncEnabled
        };
    }
}
