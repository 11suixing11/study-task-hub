namespace AccessProbe.Mobile;

public static class LoginNavigationMessage
{
    public static string ForBlocked(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme is not ("https" or "http" or "about")
        ? "官网尝试打开其他应用。请留在本页完成登录，或点击“继续应用内官网确认”核对会话。"
        : "官网登录页面跳转未完成。请点击“继续应用内官网确认”或“重新打开官方登录”重试。";
}
