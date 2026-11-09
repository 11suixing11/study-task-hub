using System.Net.Http;
using System.Text.Json;

namespace AccessProbe;

public sealed partial class MetadataHttpReader
{
    private static readonly Uri ZhihuishuCasCheck = new("https://user.zhihuishu.com/zhsuser/login/isLogin");
    private static readonly string ZhihuishuSsoReturn = "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect="
        + Uri.EscapeDataString(MetadataPlatform.Zhihuishu.Home.AbsoluteUri);
    private static readonly Uri ZhihuishuPassport = new("https://passport.zhihuishu.com/login?service=" + Uri.EscapeDataString(ZhihuishuSsoReturn));
    private async Task<MetadataResponse> CheckZhihuishuSessionAsync(Uri profileAddress, CancellationToken cancellation)
    {
        var before = await readCookies(platform.Home, cancellation); var expected = MetadataPlatformParser.ZhihuishuAccount(before);
        var profile = await SendAsync(profileAddress, HttpMethod.Get, null, true, "profile", cancellation);
        if (profile.Outcome is not ("login_required" or "course_credentials_unavailable") || expected is null) return profile;
        try { return await RenewZhihuishuSessionAsync(profileAddress, before, expected, profile, cancellation); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new("session_check_unavailable", profile.StatusCode); }
        catch (HttpRequestException) { return new("session_check_unavailable", profile.StatusCode); }
    }
    private async Task<MetadataResponse> RenewZhihuishuSessionAsync(Uri profileAddress, IReadOnlyList<MetadataSessionCookie> before,
        string expected, MetadataResponse profile, CancellationToken cancellation)
    {
        // The optional student-page JWT can expire before the CAS cookies. Recheck the official profile with CAS alone.
        if (readToken is not null && await readToken(cancellation) is { Length: > 0 })
        {
            var withoutToken = await SendAsync(profileAddress, HttpMethod.Get, null, false, "profile", cancellation);
            if (withoutToken.Outcome == "ok")
            { suppressZhihuishuStudentToken = true; return withoutToken.AccountId == expected ? withoutToken : new("account_mismatch", withoutToken.StatusCode); }
            if (withoutToken.Outcome is not ("login_required" or "course_credentials_unavailable")) return withoutToken;
            profile = withoutToken;
        }
        if (!before.Any(cookie => cookie.Name == "CASTGC" && MetadataSessionStore.IsValid(platform, cookie)
            && cookie.AppliesTo(ZhihuishuCasCheck) && !cookie.IsExpired(DateTimeOffset.UtcNow) && cookie.Value.Length > 0)) return profile;
        // This read is the exact account-session check used by the official pre-login script.
        var cas = await SendAsync(ZhihuishuCasCheck, HttpMethod.Get, null, false, "zhihuishu-cas-check", cancellation);
        if (cas.Outcome != "cas_ready") return cas.Outcome == "login_required" ? profile : new("session_check_unavailable", cas.StatusCode);
        var target = ZhihuishuPassport; var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var hop = 0; hop < 8; hop++)
        {
            if (!IsZhihuishuRenewalAddress(target) || !visited.Add(target.AbsoluteUri)) return new("session_check_unavailable", profile.StatusCode);
            var response = await SendAsync(target, HttpMethod.Get, null, false, "zhihuishu-session-renewal", cancellation);
            if (response.Outcome == "session_document")
            {
                var restored = await SendAsync(profileAddress, HttpMethod.Get, null, false, "profile", cancellation);
                if (restored.Outcome == "ok")
                { suppressZhihuishuStudentToken = true; return restored.AccountId == expected ? restored : new("account_mismatch", restored.StatusCode); }
                if (restored.Outcome == "account_mismatch") return restored;
                return restored.Outcome is "login_required" or "course_credentials_unavailable"
                    ? new("course_session_expired", restored.StatusCode) : new("session_check_unavailable", restored.StatusCode);
            }
            if (response.Outcome != "session_redirect" || response.SessionRedirect is not { } redirect)
                return new("session_check_unavailable", response.StatusCode);
            target = redirect;
        }
        return new("session_check_unavailable", profile.StatusCode);
    }
    private static bool IsZhihuishuRenewalAddress(Uri address)
    {
        if (!TaskRules.IsSafeLink(address.AbsoluteUri) || address.Fragment.Length > 0 || RainAddressPolicy.Query(address) is not { } fields) return false;
        if (address.AbsoluteUri == ZhihuishuPassport.AbsoluteUri) return true;
        var ticket = fields.GetValueOrDefault("ticket");
        if (ticket is not null && (ticket.Length is 0 or > 4096 || ticket.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))) return false;
        if (address.IdnHost == "cloudapi.polymas.com" && address.AbsolutePath is "/oauth/login/ssoLogin" or "/oauth/login/checkLogin")
        {
            var field = address.AbsolutePath.EndsWith("ssoLogin", StringComparison.Ordinal) ? "redirect" : "url";
            return fields.GetValueOrDefault(field) == MetadataPlatform.Zhihuishu.Home.AbsoluteUri && fields.Keys.All(key => key == field || key == "ticket");
        }
        return address.IdnHost == MetadataPlatform.Zhihuishu.Host && address.AbsolutePath == MetadataPlatform.Zhihuishu.HomePath && fields.Keys.All(key => key == "ticket");
    }
    private static string ZhihuishuCasOutcome(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || MetadataPlatformParser.Text(root, "status") != "200"
                || root.EnumerateObject().Count(field => field.Name == "rt") != 1) return "session_check_unavailable";
            return MetadataPlatformParser.Property(root, "rt").ValueKind switch
            { JsonValueKind.True => "cas_ready", JsonValueKind.False => "login_required", _ => "session_check_unavailable" };
        }
        catch (JsonException) { return "session_check_unavailable"; }
    }
}
