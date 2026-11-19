using AccessProbe;
using System.Text.Json;

namespace AccessProbe.Mobile;

// Categories and booleans only. URLs, console messages, cookie values and account IDs are never persisted.
public sealed record MobileLoginEvent(DateTimeOffset AtUtc, string Stage, string Outcome, string Destination, int? Status);
public sealed record MobileLoginReport(int Schema, string Platform, IReadOnlyList<MobileLoginEvent> Events,
    int Cookies, bool SessionPresent, bool AccountConfirmed, bool CatalogCovered, string[] Codes);

public sealed class MobileLoginDiagnostics(string platform, string path)
{
    private readonly object gate = new();
    private readonly List<MobileLoginEvent> events = [];
    private int cookies;
    private bool session, account, catalog;
    private string[] codes = [];
    public MobileLoginReport Snapshot()
    { lock (gate) return new(1, platform, events.ToArray(), cookies, session, account, catalog, codes.ToArray()); }

    public void Record(string stage, string outcome, string? address = null, int? status = null)
    {
        if (stage is not ("preparation" or "main" or "frame" or "popup" or "resource" or "certificate" or "renderer" or "session")) return;
        if (outcome is not ("started" or "ready" or "blocked" or "network_failed" or "http_failed" or "script_failed" or "style_failed"
            or "blank" or "failed" or "login_required" or "account_unknown" or "account_ready" or "catalog_ready" or "cleared")) return;
        lock (gate)
        {
            events.Add(new(DateTimeOffset.UtcNow, stage, outcome, Destination(platform, address), status is >= 100 and <= 599 ? status : null));
            if (events.Count > 24) events.RemoveAt(0);
            Save();
        }
    }

    public void Observe(IReadOnlyList<MetadataSessionCookie> values)
    {
        lock (gate)
        {
            cookies = values.Count;
            session = values.Any(cookie => cookie.Name is "sessionid" or "xtsessionid" or "CASTGC" or "DTS_SESSIONID" or "TGC");
            Save();
        }
    }

    public void Complete(MobileSyncResult result)
    {
        lock (gate)
        {
            account = result.AccountVerified; catalog = result.CatalogCovered;
            codes = result.Codes.Where(code => code.Length is > 0 and <= 16 && code.All(char.IsAsciiLetterOrDigit)).Distinct().Take(8).ToArray();
            Record("session", result.LoginRequired ? "login_required" : !account ? "account_unknown" : catalog ? "catalog_ready" : "account_ready");
        }
    }

    public static string ResourceKind(string platform, string address)
    {
        if (platform == "xuetang") return XuetangPlatform.LoginResourceKind(address);
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return "none";
        var application = platform == "zhihuishu" && uri.IdnHost == "login.zhihuishu.com"
            && (uri.AbsolutePath.StartsWith("/assets/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/styles/", StringComparison.Ordinal)
                || uri.AbsolutePath == "/js/pre-login.js");
        if (!application) return "none";
        return uri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "script"
            : uri.AbsolutePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "style" : "none";
    }

    public static string Destination(string platform, string? address)
    {
        if (address is null) return "none";
        if (address == "about:blank") return "blank";
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return "unsupported";
        if (ResourceKind(platform, address) != "none") return "application_resource";
        if (platform == "xuetang") return XuetangPlatform.IsOfficialNavigation(address) ? "official_page"
            : XuetangPlatform.IsLoginNavigation(address) ? "wechat" : XuetangPlatform.IsLoginFrameNavigation(address) ? "verifier" : "unsupported";
        if (platform is "zhihuishu" or "ucampus") return MetadataPlatform.Get(platform).IsLoginFrameNavigation(address) ? uri.IdnHost switch
        {
            "login.zhihuishu.com" => "login_center", "passport.zhihuishu.com" => "passport", "user.zhihuishu.com" => "cas",
            "onlineweb.zhihuishu.com" or "onlinestuh5.zhihuishu.com" => "student_home", "cloudapi.polymas.com" => "sso_return",
            "u.unipus.cn" => "student_home", "sso.unipus.cn" => "sso", "open.weixin.qq.com" or "open.weixin.qq.com.cn" => "wechat",
            _ => "official_other"
        } : "unsupported";
        return "other";
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Snapshot())); File.Move(path + ".tmp", path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
