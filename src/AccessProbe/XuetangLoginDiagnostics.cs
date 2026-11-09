using System.IO;
using System.Text.Json;

namespace AccessProbe;

public enum XuetangLoginStage { Preparation, CacheRefresh, MainNavigation, FrameNavigation, Popup, Resource, Certificate, Renderer }
public enum XuetangLoginResult { Started, Ready, Blocked, LoadingTimeout, NetworkFailed, Failed, ScriptUnavailable, StyleUnavailable, BlankPage }
public sealed record XuetangLoginEvent(DateTimeOffset AtUtc, XuetangLoginStage Stage, XuetangLoginResult Result, string Destination, int? Status);
public sealed record XuetangLoginReport(int Schema, int Attempts, int BlockedNavigations, int FailedResources,
    IReadOnlyList<XuetangLoginEvent> Events, XuetangLoginEvent? PageFailure);

public sealed class XuetangLoginDiagnostics(string path)
{
    private readonly object gate = new();
    private readonly List<XuetangLoginEvent> events = [];
    private XuetangLoginEvent? pageFailure;
    private int attempts, blocked, failedResources;
    public string Path { get; } = path;
    public string PersistenceOutcome { get; private set; } = "not_saved";

    public XuetangLoginReport Snapshot()
    { lock (gate) return new(1, attempts, blocked, failedResources, events.ToArray(), pageFailure); }

    public void Record(XuetangLoginStage stage, XuetangLoginResult result, string? address = null, int? status = null)
    {
        lock (gate)
        {
            if (stage == XuetangLoginStage.Preparation && result == XuetangLoginResult.Started) { attempts++; pageFailure = null; }
            if (result == XuetangLoginResult.Blocked) blocked++;
            if (stage == XuetangLoginStage.Resource && result is XuetangLoginResult.ScriptUnavailable or XuetangLoginResult.StyleUnavailable)
                failedResources++;
            var item = new XuetangLoginEvent(DateTimeOffset.UtcNow, stage, result, Classify(address), status is >= 100 and <= 599 ? status : null);
            events.Add(item);
            if (result is not (XuetangLoginResult.Started or XuetangLoginResult.Ready)
                && (pageFailure is null || stage != XuetangLoginStage.Popup || pageFailure.Stage == XuetangLoginStage.Popup)) pageFailure = item;
            if (events.Count > 24) events.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path + ".tmp", JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions
                { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
                File.Move(Path + ".tmp", Path, true); PersistenceOutcome = "saved";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { PersistenceOutcome = "unavailable"; }
        }
    }

    public string Describe()
    {
        var report = Snapshot(); var last = report.PageFailure ?? report.Events.LastOrDefault();
        if (last is null) return "尚未记录学堂在线登录页面诊断。";
        var text = last.Result switch
        {
            XuetangLoginResult.Blocked => "登录跳转尚未适配",
            XuetangLoginResult.ScriptUnavailable => "官方登录脚本未能加载，请重新加载登录页",
            XuetangLoginResult.StyleUnavailable => "官方登录样式未能加载，请重新加载登录页",
            XuetangLoginResult.BlankPage => "官方页面没有显示内容，请重新加载登录页",
            XuetangLoginResult.LoadingTimeout => "页面加载超时",
            XuetangLoginResult.NetworkFailed => "官方页面请求失败",
            XuetangLoginResult.Failed => "登录环境或页面运行异常",
            XuetangLoginResult.Ready => "官方页面已加载",
            _ => "正在准备或加载官方页面"
        };
        return $"最近登录诊断：{text}。已拦截跳转 {report.BlockedNavigations} 次、失败资源 {report.FailedResources} 项。";
    }

    private static string Classify(string? address)
    {
        if (address is null) return "none";
        if (address == "about:blank") return "blank";
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return "unsupported";
        if (XuetangPlatform.LoginResourceKind(address) != "none") return "application_resource";
        if (XuetangPlatform.IsCookieDomain(uri.IdnHost)) return "official_page";
        if (XuetangPlatform.IsLoginNavigation(address)) return "wechat";
        if (!XuetangPlatform.IsLoginFrameNavigation(address)) return "unsupported";
        return uri.IdnHost == "newassets.hcaptcha.com" ? "hcaptcha_frame" : "tencent_frame";
    }
}
