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
    private readonly ComboBox platforms = new() { MinWidth = 210, DisplayMemberPath = "Name" };
    private readonly TextBox search = new() { MinHeight = 34, Padding = new Thickness(9, 6, 9, 6) };
    private readonly CheckBox recordAll = new() { Content = "记录全部课程（自动包含以后新增的课程）", Margin = new Thickness(0, 12, 0, 10) };
    private readonly CheckBox hideHistory = new() { Content = "隐藏未选课程的历史任务（保留数据，重新勾选可恢复）", Margin = new Thickness(0, 12, 0, 10) };
    private readonly TextBlock catalogStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly TextBlock selectedCount = new() { Margin = new Thickness(0, 9, 0, 9), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly ListBox list = new() { BorderBrush = new SolidColorBrush(Color.FromRgb(218, 225, 235)), Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button refresh = Button("刷新课程列表");
    private readonly Button login = Button("连接平台");
    private readonly Button save = Button("保存并应用");
    private readonly Dictionary<string, Draft> drafts = new(StringComparer.Ordinal);
    private Draft? current;
    private bool loading, busy, closing, catalogRefreshing;
    private string PlatformId => (platforms.SelectedItem as PlatformSite)?.Id ?? "chaoxing";

    public CourseSelectionWindow(TaskStore store, string platformId, Func<string, Task> refreshCatalog,
        Func<string, Task> applySelection, Action<string> connect)
    {
        this.store = store; this.refreshCatalog = refreshCatalog; this.applySelection = applySelection; this.connect = connect;
        Title = "选择记录课程"; Width = 860; Height = 760; MinWidth = 760; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(243, 246, 251));
        NameScope.SetNameScope(this, new NameScope());
        var root = new DockPanel { Margin = new Thickness(24) }; var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "选择记录课程", FontSize = 25, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "勾选需要记录的课程。保存后只采集已选课程，未选课程停止采集和提醒。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 7, 0, 16) });
        var toolbar = new WrapPanel(); toolbar.Children.Add(platforms); toolbar.Children.Add(refresh); toolbar.Children.Add(login); header.Children.Add(toolbar);
        header.Children.Add(recordAll);
        search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "搜索课程"); search.ToolTip = "搜索课程名称、班级或开课期";
        header.Children.Add(search);
        var bulk = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; var all = Button("全选当前列表"); var none = Button("全不选当前列表"); bulk.Children.Add(all); bulk.Children.Add(none);
        header.Children.Add(bulk); header.Children.Add(selectedCount); header.Children.Add(catalogStatus); header.Margin = new Thickness(0, 0, 0, 12);
        root.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); footer.Children.Add(hideHistory); footer.Children.Add(result);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("取消"); cancel.Click += (_, _) => Close(); actions.Children.Add(cancel); actions.Children.Add(save); footer.Children.Add(actions); root.Children.Add(footer);
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
        list.ItemTemplate = template; root.Children.Add(list); Content = root;
        RegisterName("RecordingPlatformSelector", platforms); RegisterName("RecordingCourseSearch", search); RegisterName("RecordingAllCourses", recordAll);
        RegisterName("RecordingHideHistory", hideHistory); RegisterName("RecordingCourseList", list); RegisterName("RecordingSaveButton", save);
        RegisterName("RecordingRefreshButton", refresh); RegisterName("RecordingSelectAllButton", all); RegisterName("RecordingSelectNoneButton", none);
        RegisterName("RecordingResult", result); RegisterName("RecordingCatalogStatus", catalogStatus);
        RegisterName("RecordingCancelButton", cancel); RegisterName("RecordingConnectionButton", login);
        platforms.ItemsSource = PlatformCatalog.Sites;
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

    private static Button Button(string text) => new() { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0) };

    private async Task LoadPlatformAsync()
    {
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
            list.ItemsSource = current.Choices; list.IsEnabled = !busy;
            var automatic = CourseRecordingRules.SupportsAutomatic(PlatformId);
            refresh.Content = automatic ? "刷新官方课程列表" : "刷新本地课程"; login.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
            login.Content = "连接" + PlatformCatalog.Name(PlatformId);
            catalogStatus.Text = !automatic ? "此平台当前支持手动录入，课程来自本地任务；自动课程发现尚未接入。"
                : current.Settings.CatalogCheckedAtUtc is { } checkedAt ? (current.Settings.CatalogComplete ? "官方课程列表已完整读取" : "官方课程列表尚未完整读取，已有课程与选择保留")
                    + " · " + checkedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "尚未读取官方课程列表。请完成平台登录后刷新；没有任务的课程也会列出。";
            if (current.Choices.Count == 0) catalogStatus.Text += "\n当前没有可选课程。";
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
        UpdateFilter(); ShowResult("课程选择尚未保存，点击“保存并应用”生效。", false);
    }

    private void CourseSelectionChanged()
    {
        if (!loading)
        {
            if (current?.RecordAll == true && current.Choices.Any(choice => !choice.Selected)) recordAll.IsChecked = false;
            UpdateFilter(); ShowResult("课程选择尚未保存，点击“保存并应用”生效。", false);
        }
    }

    private void SelectVisible(bool selected)
    {
        if (current is null || busy) return;
        recordAll.IsChecked = false; current.RecordAll = false;
        foreach (var choice in current.Choices.Where(MatchesSearch)) choice.Selected = selected;
        UpdateFilter(); ShowResult("课程选择尚未保存，点击“保存并应用”生效。", false);
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
        selectedCount.Text = $"共 {current.Choices.Count} 门课程 · 已选 {(current.RecordAll ? current.Choices.Count : current.Choices.Count(choice => choice.Selected))} 门"
            + (current.RecordAll ? " · 自动记录新增课程" : " · 新增课程需要手动勾选");
    }

    private void SetBusy(bool value)
    {
        busy = value; platforms.IsEnabled = refresh.IsEnabled = save.IsEnabled = recordAll.IsEnabled = hideHistory.IsEnabled = !value;
        list.IsEnabled = !value;
    }

    private async Task RefreshAsync()
    {
        if (busy || closing) return; var platformId = PlatformId;
        SetBusy(true); ShowResult(CourseRecordingRules.SupportsAutomatic(platformId) ? "正在读取官方课程列表…" : "正在刷新本地课程…", false);
        var before = current?.Settings.CatalogCheckedAtUtc;
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
                : settings.CatalogCheckedAtUtc != before && settings.CatalogComplete ? "官方课程列表已刷新。勾选后点击“保存并应用”。"
                : "官方课程列表未完整更新，请在连接窗口核对登录，或查看首页同步详情；已有课程选择保留。",
                CourseRecordingRules.SupportsAutomatic(platformId) && (settings.CatalogCheckedAtUtc == before || !settings.CatalogComplete));
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
                draft.Settings = await Task.Run(() => store.SaveCourseRecording(platformId, draft.RecordAll,
                    draft.Choices.Where(choice => choice.Selected).Select(choice => choice.Key).ToArray(), draft.HideHistory,
                    draft.Settings.Revision, DateTimeOffset.UtcNow));
                saved.Add(platformId);
                _ = applySelection(platformId);
            }
            if (!closing) Close();
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
            if (closing || busy || current is null || catalogRefreshing) return;
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
        public bool Dirty => RecordAll != Settings.RecordAllCourses || HideHistory != Settings.HideUnselectedTasks
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
                Choices.Add(new(course, RecordAll || selected.GetValueOrDefault(course.Key, CourseRecordingRules.IsSelected(settings, course.Key)), changed));
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
}
