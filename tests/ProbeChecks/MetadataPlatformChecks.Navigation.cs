using AccessProbe;

internal static partial class MetadataPlatformChecks
{
    private static void LoginNavigation()
    {
        var current = new Uri("https://login.zhihuishu.com/?origin=zhs&service=" + Uri.EscapeDataString(Zhs.Home.AbsoluteUri));
        Check(Zhs.Login == current, "current login center keeps the student return address");
        var student = "https://onlineweb.zhihuishu.com/onlinestuh5?synthetic=a%2Bb%26c";
        var sso = "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect=" + Uri.EscapeDataString(student) + "&ticket=SYNTHETIC";
        var check = "https://cloudapi.polymas.com/oauth/login/checkLogin?url=" + Uri.EscapeDataString(current.AbsoluteUri);
        foreach (var allowed in new[] { sso, check, current.AbsoluteUri, "https://passport.zhihuishu.com/login?service=" + Uri.EscapeDataString(sso),
            "https://onlinestuh5.zhihuishu.com/", "https://open.weixin.qq.com/connect/qrconnect?synthetic=1" })
            Check(Zhs.IsLoginNavigation(allowed), "official login and SSO navigation are allowed");
        foreach (var rejected in new[]
        {
            sso.Replace("cloudapi.polymas.com", "cloudapi.polymas.com.evil.example", StringComparison.Ordinal),
            sso.Replace("cloudapi.polymas.com", "other.polymas.com", StringComparison.Ordinal),
            sso.Replace("ssoLogin", "logout", StringComparison.Ordinal),
            "https://cloudapi.polymas.com/oauth/login/ssoLogin",
            "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect=" + Uri.EscapeDataString("https://evil.example/"),
            "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect=" + Uri.EscapeDataString("http://onlineweb.zhihuishu.com/onlinestuh5"),
            "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect=" + Uri.EscapeDataString("https://onlineweb.zhihuishu.com.evil.example/"),
            "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect=" + Uri.EscapeDataString("https://user@onlineweb.zhihuishu.com/"),
            sso + "&redirect=" + Uri.EscapeDataString("https://evil.example/"),
            sso + "#unverified", sso.Replace("https://cloudapi", "http://cloudapi", StringComparison.Ordinal)
        }) Check(!Zhs.IsLoginNavigation(rejected), "unverified SSO destinations are rejected");
        Check(!Zhs.IsCookieDomain("cloudapi.polymas.com") && !Zhs.IsMetadataDocument(sso), "SSO is not a task-data or cookie-archive origin");
        Check(!MetadataPlatform.Ucampus.IsLoginNavigation(sso) && !XuetangPlatform.IsLoginNavigation(sso), "SSO stays scoped to Zhihuishu");
        Check(MetadataPlatform.Ucampus.IsLoginNavigation("https://sso.unipus.cn/sso/login?service="
            + Uri.EscapeDataString(MetadataPlatform.Ucampus.Login.AbsoluteUri)), "legacy Ucampus official SSO return is allowed");
    }
}
