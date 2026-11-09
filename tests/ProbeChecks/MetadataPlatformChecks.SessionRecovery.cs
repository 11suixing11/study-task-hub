using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessProbe;
using PlatformMetadataTestData;

internal static partial class MetadataPlatformChecks
{
    private static void SessionAuxiliaryCookies()
    {
        foreach (var platform in new[] { MetadataPlatform.Ucampus, Zhs })
        {
            var account = platform.Id == "ucampus" ? UcampusFixtures.Cookie() : Fixtures.Cookie();
            var auth = account with { Name = "SYNTHETIC_SESSION", Value = "synthetic-auth", HttpOnly = true };
            var auxiliary = account with { Name = platform.Id == "ucampus" ? "hostUrl" : "acw_tc", Value = "synthetic-route-old" };
            MetadataSessionCookie[] saved = [account, auth, auxiliary];
            var current = auxiliary with { Value = "synthetic-route-renewed" };
            Check(MetadataSessionStore.MissingForRestore(platform, saved, [current]).Count == 2,
                "persistent auxiliary cookie does not prevent restoring session cookies");
            Check(MetadataSessionStore.MissingForRestore(platform, saved, [account, current]).Count == 1,
                "same-account partial profile recovers missing authentication");
            Check(MetadataSessionStore.CookieBinding(platform, saved) == MetadataSessionStore.CookieBinding(platform, [account, auth, current]),
                "routing renewal does not detach saved student token");
            Check(MetadataSessionStore.CookieBinding(platform, saved) == MetadataSessionStore.CookieBinding(platform, [account, auth]),
                "auxiliary expiry leaves authentication binding stable");
            Check(MetadataSessionStore.MissingForRestore(platform, saved, [account with { Value = "synthetic-other-account" }, current]).Count == 0,
                "new account prevents recovery of old authentication");
            Check(MetadataSessionStore.MissingForRestore(platform, saved, [auth with { Value = "synthetic-new-login" }, current]).Count == 0,
                "conflicting authentication remains isolated");
            Check(MetadataSessionStore.MissingForRestore(platform, saved, [account with { Name = "unknown-auth", Value = "synthetic-new-login" }]).Count == 0,
                "unrecognized authentication does not mix generations");
        }
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "metadata-binding-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var archive = new MetadataSessionStore(Zhs, Path.Combine(directory, "session.bin"));
        var now = Fixtures.Now; var authCookie = Fixtures.Cookie();
        var route = authCookie with { Name = "acw_tc", Value = "synthetic-route", ExpiresUtc = now.AddSeconds(1) };
        MetadataSessionCookie[] cookies = [authCookie, route];
        var plain = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, SavedAtUtc = now, Cookies = cookies,
            Token = "synthetic-legacy-token", TokenBinding = MetadataSessionStore.CookieBinding(Zhs, cookies, legacy: true) });
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("LearningTaskAggregator.zhihuishu-session.v1"));
        try { File.WriteAllBytes(Path.Combine(directory, "session.bin"), ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var restored = archive.Load(now.AddSeconds(2));
        Check(restored.Count == 1 && archive.Token == "synthetic-legacy-token" && archive.TokenBinding == MetadataSessionStore.CookieBinding(Zhs, restored),
            "legacy protected binding migrates after auxiliary expiry");
        archive.Save(restored, now.AddSeconds(2), archive.Token, archive.TokenBinding, "https://onlineweb.zhihuishu.com");
        Check(archive.Load(now.AddSeconds(3)).Count == 1 && archive.TokenSource == "https://onlineweb.zhihuishu.com",
            "student token source persists across archive migration");
    }

    private static async Task CourseSessionRecheckAsync()
    {
        foreach (var platform in new[] { MetadataPlatform.Ucampus, Zhs })
        foreach (var mode in new[] { "expired-course", "renewed", "expired-account", "unknown-profile", "other-account" })
        {
            var profileCalls = 0; var courseCalls = 0;
            var accountCookie = platform.Id == "ucampus" ? UcampusFixtures.Cookie() : Fixtures.Cookie();
            using var reader = new MetadataHttpReader(platform, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([accountCookie]),
                new Handler((request, _) =>
                {
                    var profileRead = request.RequestUri!.AbsolutePath.EndsWith("getToken", StringComparison.Ordinal)
                        || request.RequestUri.AbsolutePath.EndsWith("getLoginUserInfo", StringComparison.Ordinal);
                    string body;
                    if (profileRead)
                    {
                        profileCalls++;
                        body = profileCalls > 1 && mode == "expired-account" ? "{\"code\":401,\"message\":\"未登录\"}"
                            : profileCalls > 1 && mode == "unknown-profile" ? "{}"
                            : platform.Id == "ucampus" ? UcampusFixtures.Profile(profileCalls > 1 && mode == "other-account" ? "other-account" : UcampusFixtures.Account)
                            : Fixtures.Profile(profileCalls > 1 && mode == "other-account" ? "other-account" : Fixtures.Account);
                    }
                    else
                    {
                        courseCalls++;
                        body = mode == "renewed" && courseCalls > 1 ? "{\"status\":200,\"rt\":{\"data\":[]}}"
                            : "{\"code\":401,\"status\":401,\"message\":\"未登录\"}";
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) });
                }));
            Check((await reader.ReadAsync(new("profile"), default)).Outcome == "ok", "same account verified before course service check");
            var result = await reader.ReadAsync(new(platform.Id == "ucampus" ? "courses" : "reminders"), default);
            var expected = mode switch { "renewed" => platform.Id == "ucampus" ? "unrecognized_response" : "ok", "expired-account" => "login_required",
                "unknown-profile" => "session_check_unavailable", "other-account" => "account_mismatch", _ => "course_session_expired" };
            Check(result.Outcome == expected && profileCalls == 2 && courseCalls == (mode is "renewed" or "expired-course" ? 2 : 1),
                "course expiry rechecks account and retries once without confusing real expiry");
        }
        Check(MetadataPlatformParser.UcampusEnvelope("ucampus-homework", "{\"code\":403,\"msg\":\"权限不足\"}") == "course_access_denied",
            "Unipus course permission rejection is not account expiry");
        await UcampusSsoRenewalAsync();
        await ZhihuishuSessionRenewalAsync();
    }

    private static async Task UcampusSsoRenewalAsync()
    {
        foreach (var mode in new[] { "renewed", "login-form", "loop", "foreign-redirect", "other-account", "network" })
        {
            var cookies = new List<MetadataSessionCookie> { UcampusFixtures.Cookie(),
                new("TGC", "synthetic-sso-session", "sso.unipus.cn", "/sso", true, true, null) };
            var paths = new List<string>(); var profiles = 0; var loginReads = 0;
            using var reader = new MetadataHttpReader(MetadataPlatform.Ucampus,
                (uri, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(cookies.Where(cookie => cookie.AppliesTo(uri)).ToArray()),
                new Handler((request, _) =>
                {
                    var uri = request.RequestUri!; paths.Add(uri.IdnHost + uri.AbsolutePath);
                    var result = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request, Content = new StringContent("") };
                    if (uri.AbsolutePath == "/user/data/getToken")
                    {
                        profiles++;
                        if (profiles == 1) result.Headers.Location = new Uri("/t?p=" + Uri.EscapeDataString("https://sso.unipus.cn/sso/login?service="
                            + Uri.EscapeDataString("https://u.unipus.cn/user/data/getToken")), UriKind.Relative);
                        else { result.StatusCode = HttpStatusCode.OK; result.Content = new StringContent(UcampusFixtures.Profile(mode == "other-account" ? "other-account" : UcampusFixtures.Account)); }
                    }
                    else if (uri.AbsolutePath == "/user/comm/login")
                    {
                        loginReads++;
                        result.Headers.Location = loginReads == 1
                            ? new Uri("/t?p=" + Uri.EscapeDataString("https://sso.unipus.cn/sso/login?service=" + Uri.EscapeDataString(MetadataPlatform.Ucampus.Login.AbsoluteUri)), UriKind.Relative)
                            : new Uri(mode == "loop" ? MetadataPlatform.Ucampus.Login.AbsoluteUri : "https://u.unipus.cn/user/student?school_id=");
                        result.Headers.TryAddWithoutValidation("Set-Cookie", "DTS_SESSIONID=synthetic-renewed; Path=/; Secure; HttpOnly");
                    }
                    else if (uri.IdnHost == "sso.unipus.cn")
                    {
                        Check(request.Headers.GetValues("Cookie").Single().Contains("TGC=synthetic-sso-session", StringComparison.Ordinal), "official SSO receives only its scoped session");
                        if (mode == "network") throw new HttpRequestException("Synthetic unavailable SSO");
                        if (mode == "login-form") { result.StatusCode = HttpStatusCode.OK; result.Content = new StringContent("<html><form action='/sso/login'>官方登录</form></html>"); }
                        else result.Headers.Location = new Uri(mode == "foreign-redirect" ? "https://evil.invalid/?ticket=synthetic-private"
                            : MetadataPlatform.Ucampus.Login.AbsoluteUri + "&ticket=ST-synthetic-session");
                    }
                    else if (uri.AbsolutePath == "/user/student")
                    { result.StatusCode = HttpStatusCode.OK; result.Content = new StringContent(UcampusFixtures.Catalog()); }
                    else throw new Exception("Unexpected SSO renewal read");
                    return Task.FromResult(result);
                }), (_, _, renewed, _) =>
                {
                    foreach (var cookie in renewed) { cookies.RemoveAll(value => value.Key == cookie.Key); cookies.Add(cookie); }
                    return Task.CompletedTask;
                });
            var result = await reader.ReadAsync(new("profile"), default);
            var expected = mode switch { "renewed" => "ok", "login-form" => "login_required", "other-account" => "account_mismatch",
                "network" => "network_error", _ => "session_check_unavailable" };
            Check(result.Outcome == expected && paths.Count <= 6 && paths.All(path => path.StartsWith("u.unipus.cn/", StringComparison.Ordinal)
                || path == "sso.unipus.cn/sso/login"), "official SSO silently renews same account and bounds rejected hops");
            Check(mode is "renewed" or "other-account" ? profiles == 2 && cookies.Any(cookie => cookie.Name == "DTS_SESSIONID") : profiles == 1,
                "SSO renewal verifies account before reusing task session");
        }
    }
}
