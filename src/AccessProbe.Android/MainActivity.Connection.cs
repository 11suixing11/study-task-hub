using AccessProbe;
using Android.Views;
using Android.Widget;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private TextView? connectionTitle, connectionSteps, connectionExplanation, connectionNext, connectionDetails, connectionPageProblem;
    private Button? connectionCheck, connectionContinue;
    private ConnectionReport? loginReport;
    private DateTimeOffset lastLoginCheck;
    private bool connectionDetailsExpanded;
    private string? loginPageFailure;

    private void BuildConnectionCard(string platform)
    {
        var originPage = page; var expectedRevision = viewRevision;
        bool CurrentView() => alive && loginPlatform == platform && ReferenceEquals(page, originPage) && viewRevision == expectedRevision;
        var card = Card(page);
        connectionTitle = Text(card, "", 18, true);
        connectionTitle.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        connectionSteps = Text(card, "", 12, false, Muted);
        connectionExplanation = Text(card, "", 13);
        connectionNext = Text(card, "", 13, false, Accent);
        connectionPageProblem = Text(card, "", 13);
        connectionPageProblem.SetTextColor(Android.Graphics.Color.Rgb(168, 49, 49));
        connectionPageProblem.Visibility = ViewStates.Gone;
        connectionCheck = Button(card, ConnectionFeedback.CheckLabel, async () => { if (CurrentView()) await CaptureLogin(platform); });
        connectionContinue = Button(card, platform == "uai" ? "返回平台查看概要" : ConnectionFeedback.ChooseLabel,
            () => { if (CurrentView()) ContinueConnectedPlatform(platform); }, false);
        connectionContinue.Visibility = ViewStates.Gone;
        connectionDetails = Text(card, "", 12, false, Muted);
        connectionDetailsExpanded = false;
        var toggle = Button(card, "接入检查详情", () =>
        {
            if (!CurrentView()) return;
            connectionDetailsExpanded = !connectionDetailsExpanded;
            connectionDetails.Visibility = connectionDetailsExpanded ? ViewStates.Visible : ViewStates.Gone;
        }, false);
        toggle.ContentDescription = "展开或收起接入检查详情";
        loginReport = null; loginPageFailure = null; lastLoginCheck = default;
        ShowLoginFeedback(ConnectionFeedback.Waiting());
        connectionDetails.Text = "尚未完成本次检查。";
        connectionDetails.Visibility = ViewStates.Gone;
    }

    private void ShowLoginFeedback(ConnectionFeedback feedback)
    {
        if (!alive || loginPlatform is null || connectionTitle is null) return;
        connectionTitle.Text = feedback.Title;
        connectionTitle.SetTextColor(feedback.Warning ? Android.Graphics.Color.Rgb(168, 49, 49) : Android.Graphics.Color.Rgb(34, 91, 68));
        connectionSteps!.Text = feedback.Steps; connectionExplanation!.Text = feedback.Explanation; connectionNext!.Text = feedback.NextStep;
        if (connectionContinue is not null)
            connectionContinue.Visibility = feedback.CanChooseCourses || loginPlatform == "uai" && loginReport?.AccountVerified == true ? ViewStates.Visible : ViewStates.Gone;
    }

    private void ShowConnectionReport(ConnectionReport report)
    {
        if (!alive || loginPlatform != report.PlatformId || connectionDetails is null || connectionCheck is null) return;
        loginReport = report;
        var feedback = ConnectionFeedback.From(report);
        ShowLoginFeedback(feedback);
        if (connectionPageProblem is not null) connectionPageProblem.Visibility = loginPageFailure is null ? ViewStates.Gone : ViewStates.Visible;
        status.Text = "";
        connectionDetails!.Text = "检查时间：" + report.CheckedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss")
            + "\n登录：" + (report.AccountVerified && !report.LoginRequired ? "官网已确认" : "尚未确认")
            + "\n课程：" + report.Courses + " 门；所选：" + report.SelectedCourses + " 门；任务：" + report.Tasks + " 条"
            + (report.ProblemCodes.Count > 0 ? "\n诊断编号：" + string.Join("、", report.ProblemCodes) : "")
            + (report.ProblemOutcomes.Count > 0 ? "\n结果类别：" + string.Join("、", report.ProblemOutcomes) : "");
        connectionDetails.Visibility = connectionDetailsExpanded ? ViewStates.Visible : ViewStates.Gone;
        connectionCheck!.Text = report.AccountVerified && !report.LoginRequired && !report.CatalogCovered ? "重试读取课程" : ConnectionFeedback.CheckLabel;
        if (report.CatalogCovered && report.Courses == 0 && report.AccountVerified)
        { connectionContinue!.Text = "返回平台页"; connectionContinue.Visibility = ViewStates.Visible; }
        else if (connectionContinue is not null) connectionContinue.Text = report.PlatformId == "uai" ? "返回平台查看概要" : ConnectionFeedback.ChooseLabel;
    }

    private void SetConnectionBusy(bool running, bool showProgress = true)
    {
        if (connectionCheck is not null) connectionCheck.Enabled = !running;
        if (connectionContinue is not null) connectionContinue.Enabled = !running;
        if (running && showProgress) ShowLoginFeedback(ConnectionFeedback.Checking());
    }

    private void ContinueConnectedPlatform(string platform)
    {
        if (loginPlatform != platform || busy || checkingLogin || loginReport?.PlatformId != platform
            || loginReport.AccountVerified != true || loginReport.LoginRequired) return;
        var report = loginReport;
        CloseLogin(platform);
        status.Text = ConnectionFeedback.ForPlatformManagement(report, platform == "uai" ? "校验官网概要" : "刷新课程").Text;
    }

    private void ShowWaitingForLogin(string? explanation = null)
    {
        var feedback = ConnectionFeedback.Waiting();
        if (explanation is not null) feedback = feedback with { Explanation = explanation };
        ShowLoginFeedback(feedback);
    }

    private void ShowLoginPageFailure(string outcome)
    {
        if (!alive || loginPlatform is null || connectionPageProblem is null) return;
        loginPageFailure = outcome;
        var feedback = ConnectionFeedback.PageFailed(outcome);
        if (loginReport is null) { ShowLoginFeedback(feedback); connectionPageProblem.Visibility = ViewStates.Gone; }
        else { ShowConnectionReport(loginReport); connectionPageProblem.Text = feedback.Explanation + "\n" + feedback.NextStep; connectionPageProblem.Visibility = ViewStates.Visible; }
    }

    private void ResetLoginFeedback()
    {
        loginReport = null; loginPageFailure = null;
        if (connectionPageProblem is not null) { connectionPageProblem.Text = ""; connectionPageProblem.Visibility = ViewStates.Gone; }
        if (connectionDetails is not null) connectionDetails.Text = "尚未完成本次检查。";
        if (connectionCheck is not null) connectionCheck.Text = ConnectionFeedback.CheckLabel;
        ShowWaitingForLogin();
    }
}
