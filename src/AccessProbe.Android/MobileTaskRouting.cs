using AccessProbe;

namespace AccessProbe.Mobile;

public sealed record MobileOfficialApp(string Package, string Name);
public sealed record MobileTaskRoute(string Outcome, string Message);

public static class MobileTaskRouting
{
    // Packages confirmed by the official download or the existing official QR integration.
    // WeChat is a QR scanner, not a native task destination for Rain Classroom or Xuetang.
    public static MobileOfficialApp? App(string platform) => platform switch
    {
        "chaoxing" => new("com.chaoxing.mobile", "学习通"),
        "ucampus" => new("cn.unipus.ucampus.student", "U 校园"),
        "zhihuishu" => new("com.able.wisdomtree", "知到"),
        "mooc" => new("com.netease.edu.ucmooc", "中国大学 MOOC"),
        "uai" => new("cn.unipus.cloud", "U 校园 AI 版"),
        _ => null
    };

    public static string Home(string platform) => platform switch
    {
        "chaoxing" => "https://i.chaoxing.com/",
        "yuketang" => "https://www.yuketang.cn/web",
        "changjiang" => "https://changjiang.yuketang.cn/web",
        "mooc" => MoocPlatform.Home.AbsoluteUri,
        "xuetang" => XuetangPlatform.Home.AbsoluteUri,
        "ucampus" => MetadataPlatform.Ucampus.Home.AbsoluteUri,
        "zhihuishu" => MetadataPlatform.Zhihuishu.Home.AbsoluteUri,
        "pta" => MetadataPlatform.Pta.Home.AbsoluteUri,
        "uai" => "https://uai.unipus.cn/",
        _ => throw new TaskValidationException("LINK003", "该平台的官方入口尚未确认。")
    };

    public static bool IsOfficialWeb(string platform, string? address)
    {
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        var host = uri.IdnHost;
        bool Domain(string root) => host == root || host.EndsWith("." + root, StringComparison.Ordinal);
        return platform switch
        {
            "chaoxing" => Domain("chaoxing.com") || Domain("xuexitong.com"),
            "yuketang" => host is "www.yuketang.cn" or "pro.yuketang.cn" or "sso.yuketang.cn",
            "changjiang" => host is "changjiang.yuketang.cn" or "sso.yuketang.cn",
            "mooc" => Domain("icourse163.org") || host == "reg.163.com",
            "xuetang" => Domain("xuetangx.com") || host == "sso.yuketang.cn",
            "ucampus" => host is "u.unipus.cn" or "sso.unipus.cn" || MetadataPlatform.Ucampus.IsLoginNavigation(address!),
            "zhihuishu" => Domain("zhihuishu.com") || host == "cloudapi.polymas.com",
            "pta" => host is "pintia.cn" or "passport.pintia.cn",
            "uai" => host is "uai.unipus.cn" or "sso.unipus.cn",
            _ => false
        };
    }

    public static MobileTaskRoute Open(string platform, string address, string courseName, bool preferApp,
        Func<MobileOfficialApp, string, string?> openApp, Func<string, bool> openWeb)
    {
        if (!TaskRules.IsSafeLink(address)) throw new TaskValidationException("LINK001", "作业链接不可用，请同步后重试。");
        if (preferApp && IsOfficialWeb(platform, address) && App(platform) is { } app)
        {
            var opened = openApp(app, address);
            if (opened == "task") return new("app_task", "已将官方任务入口交给" + app.Name + "，请按官方页面继续。");
            if (opened == "home") return new("app_home", "已打开" + app.Name + "。请进入“" + (courseName.Length > 0 ? courseName : "对应课程")
                + "”查找任务；也可返回这里点击“打开任务网页”。");
        }
        if (openWeb(address)) return new("web", "已打开任务网页，请以页面中的提交状态为准。");
        return new("unavailable", "任务入口未能打开。可点击“打开官方课程入口”继续核对。");
    }
}
