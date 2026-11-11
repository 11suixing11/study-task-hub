using AccessProbe;
using Android.Webkit;

namespace AccessProbe.Mobile;

public static class MobilePlatforms
{
    public static readonly string[] All = ["chaoxing", "yuketang", "changjiang", "mooc", "xuetang", "ucampus", "zhihuishu", "pta"];
    public static Uri Login(string platform) => platform switch
    {
        "chaoxing" => new("https://passport2.chaoxing.com/login?refer=" + Uri.EscapeDataString("https://i.chaoxing.com/")),
        "yuketang" => RainPlatform.Rain.LoginUri("www.yuketang.cn"),
        "changjiang" => RainPlatform.Changjiang.LoginUri("changjiang.yuketang.cn"),
        "mooc" => MoocPlatform.Login,
        "xuetang" => XuetangPlatform.Home,
        _ => MetadataPlatform.Get(platform).Login
    };
    public static bool Allowed(string platform, string address) => platform switch
    {
        "chaoxing" => ProbePolicy.IsAllowedNavigation(address),
        "yuketang" or "changjiang" => RainAddressPolicy.IsNavigation(RainPlatform.Find(platform), address),
        "mooc" => MoocPlatform.IsLoginNavigation(address),
        "xuetang" => XuetangPlatform.IsLoginNavigation(address),
        _ => MetadataPlatform.Get(platform).IsLoginNavigation(address)
    };
    public static bool FrameAllowed(string platform, string address) => platform switch
    {
        "xuetang" => XuetangPlatform.IsLoginFrameNavigation(address),
        "ucampus" or "zhihuishu" or "pta" => MetadataPlatform.Get(platform).IsLoginFrameNavigation(address),
        _ => Allowed(platform, address)
    };
    public static IReadOnlyList<string> CookieOrigins(string platform) => platform switch
    {
        "chaoxing" => ["https://chaoxing.com/", "https://i.chaoxing.com/", "https://i.mooc.chaoxing.com/", "https://passport2.chaoxing.com/", "https://mooc1.chaoxing.com/", "https://mooc2.chaoxing.com/", "https://mooc2-ans.chaoxing.com/", "https://mooc2-ans.chaoxing.com/mooc2-ans/", "https://i.xuexitong.com/", "https://mooc1-api.chaoxing.com/"],
        "yuketang" => ["https://www.yuketang.cn/", "https://yuketang.cn/", "https://pro.yuketang.cn/"],
        "changjiang" => ["https://changjiang.yuketang.cn/", "https://yuketang.cn/"],
        "mooc" => ["https://www.icourse163.org/", "https://icourse163.org/"],
        "xuetang" => ["https://www.xuetangx.com/", "https://xuetangx.com/"],
        "ucampus" => ["https://u.unipus.cn/", "https://sso.unipus.cn/sso/", "https://unipus.cn/"],
        "pta" => ["https://pintia.cn/", "https://passport.pintia.cn/"],
        _ => ["https://zhihuishu.com/", "https://login.zhihuishu.com/", "https://onlinestuh5.zhihuishu.com/", "https://onlineweb.zhihuishu.com/", "https://mycourse.zhihuishu.com/", "https://studentexam-api.zhihuishu.com/", "https://taurusexam-api.zhihuishu.com/", "https://aiplus.zhihuishu.com/", "https://onlineservice-api.zhihuishu.com/", "https://studyservice-api.zhihuishu.com/", "https://hike.zhihuishu.com/", "https://hikeservice.zhihuishu.com/"]
    };
    public static MetadataSessionCookie[] ReadBrowserCookies(string platform)
    {
        var values = new Dictionary<string, MetadataSessionCookie>(StringComparer.Ordinal);
        var manager = Android.Webkit.CookieManager.Instance!;
        foreach (var origin in CookieOrigins(platform))
        {
            var address = new Uri(origin);
            foreach (var pair in (manager.GetCookie(origin) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('='); if (separator < 1) continue;
                var name = pair[..separator].Trim(); var value = pair[(separator + 1)..].Trim();
                var path = address.AbsolutePath is "/sso/" or "/mooc2-ans/" ? address.AbsolutePath.TrimEnd('/') : "/";
                // WebView exposes request-cookie pairs, not Domain/Expires attributes. Keep host-only cookies;
                // server validation still decides expiry, and HTTP Set-Cookie retains exact subsequent scope.
                var cookie = new MetadataSessionCookie(name, value, address.IdnHost, path, true, true, null);
                if (MobileSessionRules.CookieValid(platform, cookie)) values.TryAdd(cookie.Key, cookie);
            }
        }
        return values.Values.Take(512).ToArray();
    }
    public static async Task RestoreBrowserAsync(MobileSession session)
    {
        var manager = Android.Webkit.CookieManager.Instance!;
        var writes = new List<Task>();
        foreach (var cookie in session.Cookies.Where(cookie => !cookie.IsExpired(DateTimeOffset.UtcNow)))
        {
            var origin = "https://" + cookie.Domain.TrimStart('.') + cookie.Path;
            var header = cookie.Name + "=" + cookie.Value + "; Path=" + cookie.Path + (cookie.Domain.StartsWith('.') ? "; Domain=" + cookie.Domain : "")
                + (cookie.Secure ? "; Secure" : "") + (cookie.HttpOnly ? "; HttpOnly" : "")
                + (cookie.ExpiresUtc is { } expiry ? "; Expires=" + expiry.ToUniversalTime().ToString("r", System.Globalization.CultureInfo.InvariantCulture) : "")
                + (cookie.SameSite is not null ? "; SameSite=" + cookie.SameSite : "");
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.SetCookie(origin, header, new CookieWriteResult(completed));
            writes.Add(completed.Task);
        }
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(10)); manager.Flush();
    }
    public static async Task ClearBrowserAsync(string platform, MobileSession? saved)
    {
        var manager = Android.Webkit.CookieManager.Instance!;
        var cookies = ReadBrowserCookies(platform).Concat(saved?.Cookies ?? []).DistinctBy(cookie => cookie.Key).ToArray();
        var writes = new List<Task>();
        foreach (var cookie in cookies)
        {
            var domains = cookie.Name.StartsWith("__Host-", StringComparison.Ordinal) ? new[] { cookie.Domain.TrimStart('.') }
                : new[] { cookie.Domain, "." + cookie.Domain.TrimStart('.') }.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var domain in domains)
            {
                var header = cookie.Name + "=; Path=" + cookie.Path + (domain.StartsWith('.') ? "; Domain=" + domain : "") + "; Max-Age=0; Secure; HttpOnly";
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                manager.SetCookie("https://" + domain.TrimStart('.') + cookie.Path, header, new CookieWriteResult(completed));
                writes.Add(completed.Task);
            }
        }
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(10)); manager.Flush();
    }
    private sealed class CookieWriteResult(TaskCompletionSource<bool> completed) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value)
        {
            if (value?.ToString() == "true") completed.TrySetResult(true);
            else completed.TrySetException(new InvalidDataException("The browser did not accept the Cookie update."));
        }
    }
}
