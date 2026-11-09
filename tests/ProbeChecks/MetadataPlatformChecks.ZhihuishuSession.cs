using System.Net;
using System.Text.Json;
using AccessProbe;
using PlatformMetadataTestData;

internal static partial class MetadataPlatformChecks
{
    private static async Task ZhihuishuSessionRenewalAsync()
    {
        foreach (var mode in new[] { "renewed", "cas-expired", "cas-network", "cas-timeout", "renewal-network", "login-form", "foreign-redirect", "loop", "other-account", "course-still-expired" })
        {
            var cookies = new List<MetadataSessionCookie> { Fixtures.Cookie(), Fixtures.Cookie() with { Name = "CASTGC", Value = "synthetic-cas-session" } };
            var paths = new List<string>(); var profiles = 0;
            var service = "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect=" + Uri.EscapeDataString(Zhs.Home.AbsoluteUri);
            using var reader = new MetadataHttpReader(Zhs,
                (uri, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(cookies.Where(cookie => cookie.AppliesTo(uri)).ToArray()),
                new Handler((request, _) =>
                {
                    var uri = request.RequestUri!; paths.Add(uri.IdnHost + uri.AbsolutePath);
                    Check(request.Method == HttpMethod.Get && request.Content is null, "CAS renewal never posts login credentials or submits a form");
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("") };
                    if (uri.AbsolutePath.EndsWith("getLoginUserInfo", StringComparison.Ordinal))
                    {
                        profiles++;
                        response.Content = new StringContent(profiles == 1 || mode == "course-still-expired" ? "{\"code\":401,\"message\":\"未登录\"}"
                            : Fixtures.Profile(mode == "other-account" ? "other-account" : Fixtures.Account));
                    }
                    else if (uri.IdnHost == "user.zhihuishu.com")
                    {
                        if (mode == "cas-network") throw new HttpRequestException("Synthetic CAS check unavailable");
                        if (mode == "cas-timeout") throw new TaskCanceledException("Synthetic CAS check timeout");
                        response.Content = new StringContent(JsonSerializer.Serialize(new { status = 200, rt = mode != "cas-expired" }));
                    }
                    else if (uri.IdnHost == "passport.zhihuishu.com")
                    {
                        if (mode == "renewal-network") throw new HttpRequestException("Synthetic official renewal unavailable");
                        if (mode == "login-form") response.Content = new StringContent("<!doctype html><form>合成官方登录表单</form>");
                        else
                        {
                            response.StatusCode = HttpStatusCode.Found;
                            response.Headers.Location = new Uri(mode == "foreign-redirect" ? "https://evil.invalid/?ticket=synthetic-private" : service + "&ticket=ST-synthetic-ticket");
                            response.Headers.TryAddWithoutValidation("Set-Cookie", "CASTGC=synthetic-cas-renewed; Domain=.zhihuishu.com; Path=/; Secure; HttpOnly");
                        }
                    }
                    else if (uri.IdnHost == "cloudapi.polymas.com")
                    {
                        Check(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"), "SSO bridge never receives CAS or course cookie credentials");
                        response.StatusCode = HttpStatusCode.Found; response.Headers.Location = new Uri(mode == "loop"
                            ? "https://passport.zhihuishu.com/login?service=" + Uri.EscapeDataString(service) : Zhs.Home.AbsoluteUri);
                    }
                    else if (uri.IdnHost == Zhs.Host) response.Content = new StringContent("<!doctype html><title>合成学生首页</title>");
                    else throw new Exception("Unexpected CAS renewal host");
                    return Task.FromResult(response);
                }), (_, _, renewed, _) =>
                { foreach (var cookie in renewed) { cookies.RemoveAll(value => value.Key == cookie.Key); cookies.Add(cookie); } return Task.CompletedTask; });
            var result = await reader.ReadAsync(new("profile"), default);
            var expected = mode switch { "renewed" => "ok", "cas-expired" => "login_required", "other-account" => "account_mismatch",
                "course-still-expired" => "course_session_expired", _ => "session_check_unavailable" };
            Check(result.Outcome == expected && paths.Count <= 6 && paths.All(path => !path.StartsWith("evil", StringComparison.Ordinal)),
                "same-account CAS recovery preserves bounded unknown and real-expiry classifications: " + mode);
            Check(result.AdditionalRequests == paths.Count - 1, "session renewal HTTP hops count toward sync request limit");
        }
        var profileCalls = 0; var otherCalls = 0;
        using var fallback = new MetadataHttpReader(Zhs, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([Fixtures.Cookie()]),
            new Handler((request, _) =>
            {
                string json;
                if (request.RequestUri!.AbsolutePath.EndsWith("getLoginUserInfo", StringComparison.Ordinal))
                { profileCalls++; json = request.Headers.Contains("Authorization") ? "{\"code\":401,\"message\":\"Missing JWT\"}" : Fixtures.Profile(); }
                else { otherCalls++; Check(!request.Headers.Contains("Authorization"), "expired optional student token stays suppressed for following catalog reads"); json = "{\"status\":200,\"rt\":{\"data\":[]}}"; }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(json) });
            }), readToken: _ => Task.FromResult<string?>("synthetic-expired-jwt"), testMetadataKey: Fixtures.Key);
        Check((await fallback.ReadAsync(new("profile"), default)).Outcome == "ok" && profileCalls == 2, "expired student JWT falls back to same-account CAS cookies without login");
        Check((await fallback.ReadAsync(new("courses"), default)).Outcome == "ok" && otherCalls == 1, "CAS fallback continues ordinary metadata collection");
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "session-revalidation-" + Guid.NewGuid().ToString("N")); var store = new TaskStore(Path.Combine(root, "tasks.db"));
        foreach (var platform in new[] { Zhs, MetadataPlatform.Ucampus })
        {
            var archive = new MetadataSessionStore(platform, Path.Combine(root, platform.Id + ".bin"), store);
            var account = platform.Id == "ucampus" ? UcampusFixtures.Cookie() : Fixtures.Cookie();
            archive.Save([account, account with { Name = "synthetic-login", Value = "synthetic-preserved" }], DateTimeOffset.UtcNow);
            archive.Invalidate(DateTimeOffset.UtcNow);
            Check(archive.Load(DateTimeOffset.UtcNow).Count == 0 && store.LoadPlatformSessionInfo(platform.Id).State == "expired"
                && archive.LoadForRevalidation(DateTimeOffset.UtcNow).Count == 2, "expired session stays unverified while encrypted current credentials survive for official revalidation");
            archive.Clear(); Check(archive.LoadForRevalidation(DateTimeOffset.UtcNow).Count == 0, "explicit reconnect permanently clears revalidation credentials");
        }
    }
}
