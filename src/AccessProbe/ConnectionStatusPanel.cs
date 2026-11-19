using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccessProbe;

// This stays beside the official browser; navigation hints cannot overwrite verification.
public sealed class ConnectionStatusPanel : Border
{
    private readonly TextBlock title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock steps = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private readonly TextBlock explanation = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private readonly TextBlock next = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock pageProblem = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick,
        Margin = new Thickness(0, 7, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Wpf.Ui.Controls.Button check = new() { Content = "检查接入", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Padding = new Thickness(14, 7, 14, 7) };
    private readonly Wpf.Ui.Controls.Button choose = new() { Content = "确认课程并同步", Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Func<Task> checkConnection;
    private readonly Func<Task> retryCatalog;
    private readonly Func<Task> retryTasks;
    private readonly Func<bool, Task> recoverLogin;
    private readonly Action? returnHome;
    private bool checking;
    private bool handlingAction;
    private bool receivedResult;
    private bool syncRunning;
    private bool previousResult;
    private bool runReported;
    private ConnectionFeedback? pageFailure;
    private ConnectionReport? current;
    private WindowsConnectionAction primaryAction = WindowsConnectionAction.Check;
    public bool HasResultForRun => runReported;

    public ConnectionStatusPanel(Func<Task> checkConnection, Action chooseCourses, ConnectionReport? previous = null,
        Func<Task>? retryCatalog = null, Func<Task>? retryTasks = null, Func<bool, Task>? recoverLogin = null, Action? returnHome = null)
    {
        this.checkConnection = checkConnection;
        this.retryCatalog = retryCatalog ?? checkConnection;
        this.retryTasks = retryTasks ?? checkConnection;
        this.recoverLogin = recoverLogin ?? (_ => checkConnection());
        this.returnHome = returnHome;
        CornerRadius = new CornerRadius(8); Padding = new Thickness(12); Margin = new Thickness(0, 0, 0, 12);
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        var body = new StackPanel(); body.Children.Add(title); body.Children.Add(steps); body.Children.Add(explanation); body.Children.Add(next); body.Children.Add(pageProblem);
        var actions = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) }; actions.Children.Add(check); actions.Children.Add(choose); body.Children.Add(actions);
        var details = new Expander { Header = "接入检查详情", Margin = new Thickness(0, 6, 0, 0), Content = detail }; body.Children.Add(details);
        Child = body;
        System.Windows.Automation.AutomationProperties.SetName(this, "平台接入状态");
        check.Click += async (_, _) => await RunPrimaryActionAsync(chooseCourses);
        choose.Click += (_, _) => chooseCourses();
        if (previous is not null) Show(previous, true); else Show(ConnectionFeedback.Waiting());
    }

    public void Show(ConnectionReport report, bool previous = false)
    {
        receivedResult = true;
        current = report; previousResult = previous; runReported |= !previous;
        var result = WindowsConnectionFeedback.For(report);
        primaryAction = result.Primary;
        Show(result.Feedback);
        pageProblem.Visibility = pageFailure is null ? Visibility.Collapsed : Visibility.Visible;
        title.Text = (previous ? "上次检查 · " : "") + result.Feedback.Title;
        detail.Text = "检查时间：" + report.CheckedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss")
            + "\n登录：" + (report.AccountVerified && !report.LoginRequired ? "官网已确认" : "尚未确认")
            + "\n课程：" + report.Courses + " 门；所选：" + report.SelectedCourses + " 门；任务：" + report.Tasks + " 条"
            + (report.ProblemCodes.Count > 0 ? "\n诊断编号：" + string.Join("、", report.ProblemCodes) : "")
            + (report.ProblemOutcomes.Count > 0 ? "\n结果类别：" + string.Join("、", report.ProblemOutcomes) : "");
        UpdateActions(result);
    }

    public void Show(ConnectionFeedback feedback)
    {
        title.Text = feedback.Title; steps.Text = feedback.Steps; explanation.Text = feedback.Explanation; next.Text = feedback.NextStep;
        title.Foreground = feedback.Warning ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(34, 91, 68));
        choose.Visibility = feedback.CanChooseCourses && primaryAction != WindowsConnectionAction.ChooseCourses
            ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetRunning(bool running)
    {
        if (running && !syncRunning) runReported = false;
        syncRunning = running;
        check.IsEnabled = choose.IsEnabled = !running && !checking;
        if (running && pageFailure is null) Show(ConnectionFeedback.Checking());
        else if (!running) UpdateActions();
    }

    public void ShowFailure(string message)
    {
        receivedResult = true;
        primaryAction = WindowsConnectionAction.Check;
        Show(ConnectionFeedback.Interrupted(false, current?.AccountVerified == true && !current.LoginRequired));
        detail.Text = message;
        UpdateActions();
    }

    public void ShowPageFailure(string outcome, string message)
    {
        receivedResult = true; pageFailure = ConnectionFeedback.PageFailed(outcome);
        primaryAction = WindowsConnectionAction.ReopenLogin;
        pageProblem.Text = pageFailure.Explanation + "\n" + pageFailure.NextStep;
        pageProblem.Visibility = Visibility.Visible;
        if (current is null) { Show(pageFailure); pageProblem.Visibility = Visibility.Collapsed; detail.Text = message; }
        else Show(current, previousResult);
        UpdateActions(WindowsConnectionFeedback.PageFailed(outcome));
    }

    public void ClearPageFailure()
    {
        pageFailure = null; pageProblem.Text = ""; pageProblem.Visibility = Visibility.Collapsed;
        if (current is null && !checking && !syncRunning) Show(ConnectionFeedback.Waiting());
        if (current is not null) Show(current, previousResult);
        else UpdateActions();
    }

    public void Reset()
    {
        current = null; previousResult = runReported = false; ClearPageFailure();
        detail.Text = "尚未完成本次检查。"; primaryAction = WindowsConnectionAction.Check;
        Show(ConnectionFeedback.Waiting()); UpdateActions();
    }

    private void UpdateActions(WindowsConnectionFeedback? result = null)
    {
        if (pageFailure is not null)
        {
            primaryAction = WindowsConnectionAction.ReopenLogin;
            check.Content = "重新打开登录页";
            choose.Visibility = Visibility.Collapsed;
        }
        else if (result is not null)
        {
            primaryAction = result.Primary;
            check.Content = result.ActionText;
            choose.Visibility = result.OfferCourseChoice ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            check.Content = primaryAction switch
            {
                WindowsConnectionAction.RefreshLogin => "刷新登录页",
                WindowsConnectionAction.ChooseCourses => "确认课程并同步",
                WindowsConnectionAction.RetryCatalog => "重试读取课程",
                WindowsConnectionAction.RetryTasks => "重试同步",
                WindowsConnectionAction.ReturnHome => "返回任务首页",
                _ => "检查接入"
            };
            choose.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RunPrimaryActionAsync(Action chooseCourses)
    {
        if (checking || syncRunning || handlingAction) return;
        switch (primaryAction)
        {
            case WindowsConnectionAction.ChooseCourses:
                chooseCourses();
                return;
            case WindowsConnectionAction.ReturnHome:
                returnHome?.Invoke();
                return;
        }
        if (primaryAction is WindowsConnectionAction.Check or WindowsConnectionAction.RetryCatalog)
        {
            await CheckAsync(primaryAction == WindowsConnectionAction.Check ? checkConnection : retryCatalog);
            return;
        }
        handlingAction = true;
        check.IsEnabled = choose.IsEnabled = false;
        try
        {
            if (primaryAction == WindowsConnectionAction.RetryTasks) await retryTasks();
            else await recoverLogin(primaryAction == WindowsConnectionAction.ReopenLogin);
        }
        catch { ShowFailure("本次接入操作未完成；已有课程和任务保留，请等待页面就绪后重试。"); }
        finally
        {
            handlingAction = false;
            check.IsEnabled = choose.IsEnabled = !syncRunning && !checking;
            UpdateActions();
        }
    }

    private async Task CheckAsync(Func<Task>? operation = null)
    {
        if (checking || syncRunning) return;
        checking = true; receivedResult = false; check.IsEnabled = choose.IsEnabled = false; Show(ConnectionFeedback.Checking());
        try { await (operation ?? checkConnection)(); }
        catch (OperationCanceledException) { Show(ConnectionFeedback.Interrupted(true, current?.AccountVerified == true && !current.LoginRequired)); receivedResult = true; }
        catch { ShowFailure("本次接入检查未完成，请等待页面加载后重试。"); }
        finally
        {
            checking = false; check.IsEnabled = choose.IsEnabled = !syncRunning;
            if (!receivedResult)
            {
                primaryAction = WindowsConnectionAction.Check;
                Show(ConnectionFeedback.Interrupted(false, current?.AccountVerified == true && !current.LoginRequired));
            }
            if (current is not null) UpdateActions(WindowsConnectionFeedback.For(current)); else UpdateActions();
        }
    }
}
