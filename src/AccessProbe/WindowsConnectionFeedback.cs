namespace AccessProbe;

internal enum WindowsConnectionAction { Check, ReopenLogin, RefreshLogin, RetryCatalog, ChooseCourses, RetryTasks, ReturnHome }

internal sealed record WindowsConnectionFeedback(ConnectionFeedback Feedback, WindowsConnectionAction Primary, bool OfferCourseChoice)
{
    public string ActionText => Primary switch
    {
        WindowsConnectionAction.ReopenLogin => "重新打开登录页",
        WindowsConnectionAction.RefreshLogin => "刷新登录页",
        WindowsConnectionAction.Check => "检查接入",
        WindowsConnectionAction.RetryCatalog => "重试读取课程",
        WindowsConnectionAction.ChooseCourses => "确认课程并同步",
        WindowsConnectionAction.RetryTasks => "重试同步",
        WindowsConnectionAction.ReturnHome => "返回任务首页",
        _ => "检查接入"
    };

    public static WindowsConnectionFeedback For(ConnectionReport report)
    {
        var feedback = ConnectionFeedback.From(report);
        var primary = !report.SessionAvailable ? WindowsConnectionAction.ReopenLogin
            : report.LoginRequired ? WindowsConnectionAction.RefreshLogin
            : !report.AccountVerified || !report.SessionSaved ? WindowsConnectionAction.Check
            : report.PlatformId == "uai" ? WindowsConnectionAction.ReturnHome
            : !report.CatalogCovered || report.Courses == 0 ? WindowsConnectionAction.RetryCatalog
            : !report.SelectionConfirmed || report.SelectedCourses == 0 || report.CatalogOnly ? WindowsConnectionAction.ChooseCourses
            : report.SupportedTaskListsCovered && !report.TaskListsCovered && report.PlatformId == "zhihuishu"
                && !feedback.Warning ? WindowsConnectionAction.ReturnHome
            : !report.TaskListsCovered ? WindowsConnectionAction.RetryTasks
            : WindowsConnectionAction.ReturnHome;
        return new(feedback, primary, primary != WindowsConnectionAction.ChooseCourses && feedback.CanChooseCourses);
    }

    public static WindowsConnectionFeedback PageFailed(string outcome) => new(
        ConnectionFeedback.PageFailed(outcome), WindowsConnectionAction.ReopenLogin, false);
}
