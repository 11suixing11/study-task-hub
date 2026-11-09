using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccessProbe;

public sealed class TaskEditorWindow : Window
{
    private readonly TaskItem? original;
    private readonly TextBox titleBox = new();
    private readonly ComboBox platformBox = new();
    private readonly TextBox courseBox = new();
    private readonly ComboBox deadlineBox = new();
    private readonly TextBox beginBox = new();
    private readonly TextBox endBox = new();
    private readonly CheckBox confirmDeadlineBox = new() { Content = "确认以上截止时间，开启定时提醒", Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox linkBox = new();
    private bool preserveCollectedLink;
    private readonly TextBox progressBox = new();
    private readonly TextBox scoreBox = new();
    private readonly ComboBox priorityBox = new();
    private readonly TextBox notesBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80 };
    private readonly TextBlock errorText = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    public TaskDraft? Result { get; private set; }

    public TaskEditorWindow(TaskItem? task = null)
    {
        original = task;
        Title = task is null ? "新增任务" : "编辑任务";
        Icon = ProductIcons.AppImage;
        Width = 690; Height = 820; MinHeight = 600; MinWidth = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AccessProbe;component/FluentTheme.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        NameScope.SetNameScope(this, new NameScope());
        var root = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Wpf.Ui.Controls.Button { Content = "取消", IsCancel = true, Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(0, 0, 10, 0) };
        var save = new Wpf.Ui.Controls.Button { Content = "保存任务", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, IsDefault = true, Padding = new Thickness(20, 8, 20, 8) };
        ProductVisuals.SetButtonIcon(save, "save");
        save.Click += Save;
        actions.Children.Add(cancel); actions.Children.Add(save);
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = actions };
        footer.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var heading = ProductVisuals.Heading(Title, task is null ? "记录学习安排，统一管理截止时间与备注。" : "更新任务信息，保留平台采集依据与个人修改。", "document");
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var stack = new StackPanel();
        var basicFields = new StackPanel(); var timeFields = new StackPanel(); var extraFields = new StackPanel();
        var fieldGroup = basicFields;
        void Field(string label, Control control, string? help = null)
        {
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            fieldGroup.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4), FontWeight = FontWeights.Medium });
            control.MinHeight = 34; control.Margin = new Thickness(0);
            fieldGroup.Children.Add(control);
            if (help is not null)
            {
                var helper = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
                helper.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); fieldGroup.Children.Add(helper);
            }
        }
        platformBox.ItemsSource = PlatformCatalog.Sites;
        platformBox.DisplayMemberPath = "Name"; platformBox.SelectedValuePath = "Id";
        platformBox.SelectedValue = task?.PlatformId ?? "chaoxing";
        platformBox.IsEnabled = task is null || !TaskRules.IsCollected(task);
        deadlineBox.ItemsSource = new[] { "截止时间未知", "填写截止时间", "明确无截止时间" };
        deadlineBox.SelectedIndex = task?.DeadlineKind switch { DeadlineKind.Known => 1, DeadlineKind.None => 2, _ => 0 };
        priorityBox.ItemsSource = new[] { "自动判断", "P1 马上到期", "P2 临期", "P3 不慌", "P4 待确认" };
        priorityBox.SelectedIndex = task?.PriorityOverride ?? 0;
        titleBox.Text = task?.Title ?? ""; courseBox.Text = task?.CourseName ?? "";
        beginBox.Text = task?.BeginUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
        endBox.Text = task?.DeadlineUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
        // Account-bound collected URLs are not shown as ordinary editable text.
        preserveCollectedLink = task is not null && TaskRules.IsCollected(task) && task.Link is not null;
        linkBox.Text = preserveCollectedLink ? "已保存原平台链接" : task?.Link ?? "";
        linkBox.IsReadOnly = preserveCollectedLink;
        progressBox.Text = task?.ProgressPercent?.ToString(CultureInfo.InvariantCulture) ?? "";
        scoreBox.Text = task?.ScoreText ?? ""; notesBox.Text = task?.Notes ?? "";
        Field("任务名称", titleBox); Field("来源平台", platformBox); Field("所属课程", courseBox);
        fieldGroup = timeFields;
        var timeNote = new TextBlock { Text = "时间按本机时区显示（" + TimeZoneInfo.Local.StandardName + "），请填写完整年份。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        timeNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); timeFields.Children.Add(timeNote);
        Field("截止时间类型", deadlineBox); Field("开放时间（可选）", beginBox, "格式：2026-10-25 19:00");
        Field("截止时间", endBox, "格式：2026-10-25 23:59。未知时间不会安排定时提醒。");
        if (task is not null && TaskRules.HasUsableDeadline(task) && !TaskRules.HasTrustedDeadline(task))
        {
            timeFields.Children.Add(new TextBlock { Text = TaskRules.Confidence(task), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0) });
            timeFields.Children.Add(confirmDeadlineBox);
        }
        fieldGroup = extraFields;
        Field("直达作业链接（可选）", linkBox, "HTTPS 原平台页面地址；采集链接在本机加密保存。");
        if (preserveCollectedLink)
        {
            var replaceLink = new Wpf.Ui.Controls.Button
            {
                Content = "更换链接", HorizontalAlignment = HorizontalAlignment.Left,
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 6, 0, 0)
            };
            ProductVisuals.SetButtonIcon(replaceLink, "open");
            replaceLink.Click += (_, _) =>
            {
                preserveCollectedLink = false;
                linkBox.IsReadOnly = false; linkBox.Clear(); linkBox.Focus();
                replaceLink.IsEnabled = false;
                errorText.Text = "请粘贴新的完整 HTTPS 作业地址。保存后保留你的链接修改；取消可保留原链接。";
            };
            extraFields.Children.Add(replaceLink);
        }
        Field("进度百分比（可选）", progressBox, "填 0–100；成绩 100 分不代表进度 100%。");
        Field("成绩 / 分数（可选）", scoreBox);
        Field("优先级", priorityBox, "未知截止时间为 P4；明确无截止时间为 P3；已逾期为 P0；自动判断：2 小时内 P1，24 小时内 P2。");
        Field("备注", notesBox);
        if (task is not null && !string.IsNullOrWhiteSpace(task.DisplayedTimeText))
            timeFields.Children.Add(new TextBlock { Text = "平台时间原文：" + task.DisplayedTimeText, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
        stack.Children.Add(ProductVisuals.Section("任务信息", "document", basicFields));
        stack.Children.Add(ProductVisuals.Section("时间安排", "soon", timeFields));
        stack.Children.Add(ProductVisuals.Section("补充信息", "edit", extraFields));
        errorText.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(errorText, Dock.Bottom); root.Children.Add(errorText);
        var scroller = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroller);
        RegisterName("TaskEditorTitle", titleBox); RegisterName("TaskEditorPlatform", platformBox);
        RegisterName("TaskEditorSave", save); RegisterName("TaskEditorCancel", cancel); RegisterName("TaskEditorScroll", scroller);
        RegisterName("TaskEditorError", errorText); RegisterName("TaskEditorSections", stack);
        SizeChanged += (_, _) => root.Margin = new Thickness(ActualHeight < 700 ? 16 : 24);
        Content = root;
        deadlineBox.SelectionChanged += (_, _) => endBox.IsEnabled = deadlineBox.SelectedIndex == 1;
        endBox.IsEnabled = deadlineBox.SelectedIndex == 1;
        Loaded += (_, _) => titleBox.Focus();
    }

    private void Save(object sender, RoutedEventArgs args)
    {
        try
        {
            decimal? progress = null;
            if (!string.IsNullOrWhiteSpace(progressBox.Text))
            {
                if (!decimal.TryParse(progressBox.Text.Trim().TrimEnd('%', '％'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
                    throw new TaskValidationException("TASK003", "进度请输入 0–100 的数字。");
                progress = parsed;
            }
            var kind = deadlineBox.SelectedIndex switch { 1 => DeadlineKind.Known, 2 => DeadlineKind.None, _ => DeadlineKind.Unknown };
            var link = preserveCollectedLink ? original!.Link
                : string.IsNullOrWhiteSpace(linkBox.Text) ? null : linkBox.Text.Trim();
            var draft = new TaskDraft(titleBox.Text, platformBox.SelectedValue as string ?? "", courseBox.Text,
                kind, TaskRules.ParseEditedLocalTime(beginBox.Text, original?.BeginUtc),
                kind == DeadlineKind.Known ? TaskRules.ParseEditedLocalTime(endBox.Text, original?.DeadlineUtc) : null,
                link, progress, scoreBox.Text, notesBox.Text, priorityBox.SelectedIndex == 0 ? null : priorityBox.SelectedIndex)
                { ConfirmDeadline = confirmDeadlineBox.IsChecked == true && kind == DeadlineKind.Known };
            TaskRules.Validate(draft);
            Result = draft; DialogResult = true;
        }
        catch (TaskValidationException error) { errorText.Text = error.Code + "：" + error.Message; }
    }
}
