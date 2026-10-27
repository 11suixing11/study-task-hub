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
        Width = 690; Height = 820; MinHeight = 600; MinWidth = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 252));
        var root = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = "保存任务", IsDefault = true, Padding = new Thickness(20, 8, 20, 8) };
        save.Click += Save;
        actions.Children.Add(cancel); actions.Children.Add(save);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = Title, FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock
        {
            Text = "时间使用本机时区 " + TimeZoneInfo.Local.DisplayName + "。填写完整年份，保存时转为 UTC。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 12)
        });
        void Field(string label, Control control, string? help = null)
        {
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 4), FontWeight = FontWeights.Medium });
            control.Padding = new Thickness(8, 6, 8, 6); control.MinHeight = 34;
            stack.Children.Add(control);
            if (help is not null) stack.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
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
        Field("截止时间类型", deadlineBox); Field("开放时间（可选）", beginBox, "格式：2026-10-25 19:00");
        Field("截止时间", endBox, "格式：2026-10-25 23:59。未知时间不会安排定时提醒。");
        if (task is not null && TaskRules.HasUsableDeadline(task) && !TaskRules.HasTrustedDeadline(task))
        {
            stack.Children.Add(new TextBlock { Text = TaskRules.Confidence(task), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0) });
            stack.Children.Add(confirmDeadlineBox);
        }
        Field("直达作业链接（可选）", linkBox, "HTTPS 原平台页面地址；采集链接在本机加密保存。");
        if (preserveCollectedLink)
        {
            var replaceLink = new Button
            {
                Content = "更换链接", HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 6, 0, 0)
            };
            replaceLink.Click += (_, _) =>
            {
                preserveCollectedLink = false;
                linkBox.IsReadOnly = false; linkBox.Clear(); linkBox.Focus();
                replaceLink.IsEnabled = false;
                errorText.Text = "请粘贴新的完整 HTTPS 作业地址。保存后保留你的链接修改；取消可保留原链接。";
            };
            stack.Children.Add(replaceLink);
        }
        Field("进度百分比（可选）", progressBox, "填 0–100；成绩 100 分不代表进度 100%。");
        Field("成绩 / 分数（可选）", scoreBox);
        Field("优先级", priorityBox, "未知截止时间为 P4；明确无截止时间为 P3；已逾期为 P0；自动判断：2 小时内 P1，24 小时内 P2。");
        Field("备注", notesBox);
        if (task is not null && !string.IsNullOrWhiteSpace(task.DisplayedTimeText))
            stack.Children.Add(new TextBlock { Text = "平台时间原文：" + task.DisplayedTimeText, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        stack.Children.Add(errorText);
        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
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
