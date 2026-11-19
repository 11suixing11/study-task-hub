using AccessProbe;
using Android.Webkit;
using System.Text.Json;

namespace AccessProbe.Mobile;

public static class MobilePlatforms
{
    public static readonly string[] All = ["chaoxing", "yuketang", "changjiang", "mooc", "xuetang", "ucampus", "zhihuishu", "pta", "uai"];
    public static Uri Login(string platform) => platform switch
    {
        "chaoxing" => new("https://passport2.chaoxing.com/login?refer=" + Uri.EscapeDataString("https://i.chaoxing.com/")),
        "yuketang" => RainPlatform.Rain.LoginUri("www.yuketang.cn"),
        "changjiang" => RainPlatform.Changjiang.LoginUri("changjiang.yuketang.cn"),
        "mooc" => MoocPlatform.Login,
        "xuetang" => XuetangPlatform.Login,
        "uai" => UaiPortal.Home,
        _ => MetadataPlatform.Get(platform).Login
    };
    public static bool Allowed(string platform, string address) => platform switch
    {
        "chaoxing" => ProbePolicy.IsAllowedNavigation(address),
        "yuketang" or "changjiang" => RainAddressPolicy.IsNavigation(RainPlatform.Find(platform), address),
        "mooc" => MoocPlatform.IsLoginNavigation(address),
        "xuetang" => XuetangPlatform.IsLoginNavigation(address),
        "uai" => UaiPortal.IsLoginNavigation(address),
        _ => MetadataPlatform.Get(platform).IsLoginNavigation(address)
    };
    public static bool FrameAllowed(string platform, string address) => platform switch
    {
        "xuetang" => XuetangPlatform.IsLoginFrameNavigation(address),
        "ucampus" or "zhihuishu" or "pta" => MetadataPlatform.Get(platform).IsLoginFrameNavigation(address),
        _ => Allowed(platform, address)
    };
    public static IReadOnlyList<string> CookieOrigins(string platform) => MobileCookieSnapshot.Addresses(platform);
    public static MetadataSessionCookie[] ReadBrowserCookies(string platform) => MobileCookieSnapshot.Capture(platform, ReadPairs(platform));
    private static MobileCookieRead[] ReadPairs(string platform) => CookieOrigins(platform)
        .Select(origin => new MobileCookieRead(origin, Android.Webkit.CookieManager.Instance!.GetCookie(origin) ?? "")).ToArray();
    public static async Task<MetadataSessionCookie[]> ReadBrowserCookiesAsync(string platform, WebView target)
    {
        var before = ReadBrowserCookies(platform);
        var reads = ReadPairs(platform); var observed = new List<MobileCookieRead>();
        // Local, resource-free documents inspect DOM visibility without loading or modifying official pages.
        // The browser supplies HttpOnly enforcement; the bridge never guesses visibility from a cookie name.
        using var probe = new WebView(target.Context!);
        probe.Settings.JavaScriptEnabled = true; probe.Settings.AllowFileAccess = false; probe.Settings.AllowContentAccess = false;
        probe.Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        Android.Webkit.CookieManager.Instance!.SetAcceptThirdPartyCookies(probe, true);
        try
        {
            foreach (var read in reads)
            {
                if (read.Header.Length == 0) { observed.Add(read); continue; }
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                probe.SetWebViewClient(new CookieVisibilityClient(loaded));
                probe.LoadDataWithBaseURL(read.Address, "<!doctype html><title>Cookie visibility</title>", "text/html", "UTF-8", null);
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var visible = JsonSerializer.Deserialize<string>(await MainActivity.Evaluate(probe, "document.cookie"));
                observed.Add(read with { VisibleHeader = visible });
            }
            if (MobileSessionRules.CookieFingerprint(platform, before) != MobileSessionRules.CookieFingerprint(platform, ReadBrowserCookies(platform)))
                throw new OperationCanceledException("The official browser session changed during capture.");
            return MobileCookieSnapshot.Capture(platform, observed);
        }
        finally { probe.StopLoading(); probe.Destroy(); }
    }
    public static async Task RestoreBrowserAsync(MobileSession session)
    {
        await WriteCookiesAsync(MobileCookieSnapshot.MissingForRestore(session, ReadBrowserCookies(session.PlatformId)));
    }
    public static async Task ApplyResponseCookiesAsync(string platform, Uri address, IReadOnlyList<MetadataSessionCookie> observed,
        IReadOnlyList<MetadataSessionCookie> changes, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var manager = Android.Webkit.CookieManager.Instance!;
        if (!MobileCookieSnapshot.CanApplyResponse(platform, manager.GetCookie(address.AbsoluteUri) ?? "", observed)) return;
        if (changes.Any(cookie => !MobileSessionRules.CookieValid(platform, cookie)
            || !cookie.AppliesTo(new Uri("https://" + address.IdnHost + cookie.Path))))
            throw new InvalidDataException("Invalid browser response cookie scope.");
        await WriteCookiesAsync(changes);
        cancellation.ThrowIfCancellationRequested();
    }
    private static async Task WriteCookiesAsync(IEnumerable<MetadataSessionCookie> cookies)
    {
        var manager = Android.Webkit.CookieManager.Instance!;
        var writes = new List<Task>();
        foreach (var cookie in cookies)
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
        var writes = new List<Task>(); var removedScopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cookie in cookies)
        {
            var domains = new HashSet<string>(StringComparer.Ordinal) { cookie.Domain.TrimStart('.') };
            if (!cookie.Name.StartsWith("__Host-", StringComparison.Ordinal))
            {
                var domain = cookie.Domain.TrimStart('.');
                while (domain.Contains('.'))
                {
                    if (MobileSessionRules.CookieValid(platform, cookie with { Domain = "." + domain })) domains.Add("." + domain);
                    domain = domain[(domain.IndexOf('.') + 1)..];
                }
            }
            var paths = new HashSet<string>(StringComparer.Ordinal) { cookie.Path };
            // Inferred /api/ may represent an actual /api cookie. Explicit reset clears each possible observed ancestor.
            for (var index = 0; index < cookie.Path.Length; index++)
                if (cookie.Path[index] == '/')
                { paths.Add(cookie.Path[..(index + 1)]); if (index > 0) paths.Add(cookie.Path[..index]); }
            foreach (var domain in domains)
            foreach (var path in paths)
            {
                if (cookie.Name.StartsWith("__Host-", StringComparison.Ordinal) && path != "/") continue;
                if (!removedScopes.Add(cookie.Name + "\n" + domain + "\n" + path)) continue;
                var header = cookie.Name + "=; Path=" + path + (domain.StartsWith('.') ? "; Domain=" + domain : "") + "; Max-Age=0; Secure; HttpOnly";
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                manager.SetCookie("https://" + domain.TrimStart('.') + path, header, new CookieWriteResult(completed));
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
    private sealed class CookieVisibilityClient(TaskCompletionSource completed) : WebViewClient
    {
        public override void OnPageFinished(WebView? view, string? url) => completed.TrySetResult();
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => true;
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
            => new("text/plain", "UTF-8", new MemoryStream([]));
        public override void OnReceivedSslError(WebView? view, SslErrorHandler? handler, Android.Net.Http.SslError? error) => handler?.Cancel();
    }
}
