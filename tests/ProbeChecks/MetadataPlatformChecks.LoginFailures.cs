using System.Net;
using System.Text.Json;
using AccessProbe;
using PlatformMetadataTestData;

internal static partial class MetadataPlatformChecks
{
    private static async Task LoginFailureChecksAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "metadata-login-failures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var diagnostic = new MetadataLoginDiagnostics(Zhs, Path.Combine(directory, "login.json"));
        foreach (var (status, body, expected) in new (HttpStatusCode, string, string)[]
        {
            (HttpStatusCode.Forbidden, "{\"code\":403,\"message\":\"Access denied\"}", "course_access_denied"),
            (HttpStatusCode.Forbidden, "<html>Access denied</html>", "course_access_denied"),
            (HttpStatusCode.Unauthorized, "{\"code\":401,\"message\":\"Missing JWT\"}", "course_credentials_unavailable"),
            (HttpStatusCode.OK, "{\"code\":-1,\"message\":\"Missing JWT\"}", "course_credentials_unavailable"),
            (HttpStatusCode.OK, "{\"code\":-3,\"message\":\"Unknown service failure\"}", "unrecognized_response"),
            (HttpStatusCode.OK, "{\"code\":-1,\"rt\":{\"loginstatus\":false}}", "login_required"),
            (HttpStatusCode.Forbidden, "{\"code\":403,\"message\":\"未登录\"}", "login_required"),
            (HttpStatusCode.Unauthorized, "{}", "login_required")
        })
        {
            using var reader = new MetadataHttpReader(Zhs, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([Fixtures.Cookie()]),
                new Handler((request, _) => Task.FromResult(new HttpResponseMessage(status)
                    { Content = new StringContent(body), RequestMessage = request })));
            reader.ReadCompleted += diagnostic.RecordRequest;
            var result = await reader.ReadAsync(new("profile"), default);
            Check(result.Outcome == expected && result.StatusCode == (int)status, "login expiry distinguished from course token and permission failures");
            Check(diagnostic.Snapshot().Requests.Last() is { Operation: "profile" } value && value.Outcome == expected && value.Status == (int)status,
                "failure diagnostics preserve safe operation and HTTP status");
        }
        foreach (var (status, expected) in new[] { (HttpStatusCode.Forbidden, "course_access_denied"), (HttpStatusCode.ServiceUnavailable, "http_error") })
        {
            using var legacy = new MetadataHttpReader(MetadataPlatform.Ucampus,
                (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([UcampusFixtures.Cookie()]),
                new Handler((request, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}"), RequestMessage = request })));
            Check((await legacy.ReadAsync(new("profile"), default)).Outcome == expected, "legacy HTTP service failure preserves saved login");
        }
        foreach (var (body, expected) in new[]
        {
            ("{\"status\":200,\"code\":-1,\"message\":\"未登录\"}", "login_required"),
            ("{\"status\":200,\"code\":-3,\"message\":\"Missing JWT\"}", "course_credentials_unavailable"),
            ("{\"status\":200,\"code\":-1,\"rt\":{\"loginstatus\":false}}", "login_required"),
            ("{\"status\":200,\"code\":-1}", "course_access_denied")
        })
            Check(MetadataPlatformParser.EnvelopeOutcome(Zhs, "shared-homework", body) == expected,
                "explicit login and credential failures take precedence over shared-homework permission envelope");
        using (var ajax = new MetadataHttpReader(MetadataPlatform.Ucampus,
            (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([UcampusFixtures.Cookie()]),
            new Handler((request, _) =>
            {
                var query = RainAddressPolicy.Query(request.RequestUri!)!;
                var matches = request.RequestUri!.AbsolutePath == "/user/data/getToken" && query.Count == 1 && long.TryParse(query.GetValueOrDefault("_"), out var timestamp)
                    && request.Headers.TryGetValues("X-Requested-With", out var header) && header.Single() == "XMLHttpRequest"
                    && request.Headers.CacheControl?.NoCache == true && request.Headers.Pragma.Any(value => value.Name == "no-cache");
                return Task.FromResult(new HttpResponseMessage(matches ? HttpStatusCode.OK : HttpStatusCode.Found)
                    { Content = new StringContent(matches ? UcampusFixtures.Profile() : "") , RequestMessage = request });
            })))
            Check((await ajax.ReadAsync(new("profile"), default)).Outcome == "ok", "legacy account works when server requires official AJAX and no-cache request");

        var service = "https://u.unipus.cn/user/comm/login?school_id=";
        var sso = "https://sso.unipus.cn/sso/login?service=" + Uri.EscapeDataString(service);
        foreach (var (location, expected) in new[]
        {
            ("/t?p=" + sso, "login_required"),
            ("https://u.unipus.cn/t?p=" + Uri.EscapeDataString(sso), "login_required"),
            ("/t?p=https://sso.unipus.cn/sso/login?service=" + Uri.EscapeDataString("https://u.unipus.cn/user/data/getToken"), "login_required"),
            ("/t?p=" + Uri.EscapeDataString("https://unrelated.invalid/sso/login?service=" + Uri.EscapeDataString(service)), "unsupported_redirect"),
            ("/t?p=" + Uri.EscapeDataString("https://sso.unipus.cn/sso/login?service=https%3A%2F%2Funrelated.invalid%2F"), "unsupported_redirect"),
            ("/t?p=" + Uri.EscapeDataString("http://sso.unipus.cn/sso/login?service=" + Uri.EscapeDataString(service)), "unsupported_redirect"),
            ("/t?p=" + Uri.EscapeDataString(sso) + "&p=" + Uri.EscapeDataString(sso), "unsupported_redirect"),
            ("/t?p=" + Uri.EscapeDataString(sso) + "#unsupported", "unsupported_redirect"),
            ("/t?p=unrecognized", "unsupported_redirect"),
            ("/t?p=https://sso.unipus.cn/sso/login?service=" + Uri.EscapeDataString("https://u.unipus.cn/unrecognized"), "unsupported_redirect"),
            ("/t", "unsupported_redirect")
        })
        {
            var requests = 0;
            using var redirected = new MetadataHttpReader(MetadataPlatform.Ucampus,
                (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([UcampusFixtures.Cookie()]),
                new Handler((request, _) =>
                {
                    requests++;
                    var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request, Content = new StringContent("") };
                    response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return Task.FromResult(response);
                }));
            Check((await redirected.ReadAsync(new("profile"), default)).Outcome == expected && requests == 1,
                "recognized official SSO bridge requests login without following redirects or accepting unknown targets");
        }
        using (var courses = new MetadataHttpReader(MetadataPlatform.Ucampus,
            (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([UcampusFixtures.Cookie()]),
            new Handler((request, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request, Content = new StringContent("") };
                response.Headers.Location = new Uri("/t?p=https://sso.unipus.cn/sso/login?service="
                    + Uri.EscapeDataString("https://u.unipus.cn/user/student/index"), UriKind.Relative);
                return Task.FromResult(response);
            })))
            Check((await courses.ReadAsync(new("courses"), default)).Outcome == "login_required", "SSO bridge can return to requested official course document");

        diagnostic.RecordRequest(new("courses") { Course = new("private-course", "私有课程", "flip", "private") },
            new("network_error", null, "private-response"));
        diagnostic.RecordRequest(new("profile"), new("private-session-token", 700, "private-response"));
        diagnostic.RecordSessionChange("private-account-secret");
        var saved = File.ReadAllText(diagnostic.Path);
        Check(!saved.Contains("private", StringComparison.Ordinal) && !saved.Contains(Fixtures.Account, StringComparison.Ordinal)
            && !saved.Contains("CASLOGC", StringComparison.Ordinal), "request diagnostics exclude account credentials course titles and response bodies");
        Check(diagnostic.Snapshot().SessionChangeReason == "generation_changed", "arbitrary exception text cannot enter login diagnostics");
        Check(MetadataFailureText.Describe("course_access_denied", 403).Contains("HTTP 403", StringComparison.Ordinal)
            && MetadataFailureText.Describe("course_access_denied").Contains("当前登录保留", StringComparison.Ordinal), "failure message identifies stage and retained login");
    }
}
