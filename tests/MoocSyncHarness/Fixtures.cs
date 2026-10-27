using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MoocSyncHarness;

internal sealed class Fixtures
{
    public const string AuthName = "NTESSTUDYSI";
    public string ExpectedToken { get; set; } = "synthetic-seed";
    public string Account { get; set; } = "9001";
    public bool RejectRpc { get; set; }
    public bool UnknownIdentity { get; set; }
    public bool UnknownTerm { get; set; }
    public bool UnknownNewExams { get; set; }
    public bool EmptyCourses { get; set; }
    public bool RenewNext { get; set; }
    public bool LateRenewal { get; set; }
    public bool RedirectHomeAway { get; set; }
    public bool RedirectHomeToLogin { get; set; }
    public bool SlowHomeResource { get; set; }
    public bool SlowHomeScript { get; set; }
    public bool DelayedHomeIdentity { get; set; }
    public int RedirectedHomeRequests { get; private set; }
    public TaskCompletionSource<bool>? ResourceEntered { get; set; }
    public TaskCompletionSource<bool>? ResourceRelease { get; set; }
    public int Requests { get; private set; }
    public int HomeRequests { get; private set; }
    public int BlockedResources { get; private set; }
    public List<string> RequestPaths { get; } = [];
    public TaskCompletionSource<bool>? Entered { get; set; }
    public TaskCompletionSource<bool>? Release { get; set; }
    public TaskCompletionSource<bool>? HomeEntered { get; set; }
    public TaskCompletionSource<bool>? HomeRelease { get; set; }

    public void BlockBrowser(WebView2 browser)
    {
        browser.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) return;
            var core = browser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += async (_, request) =>
            {
                using var deferral = request.GetDeferral();
                var address = new Uri(request.Request.Uri);
                var home = address.Host == MoocPlatform.Host && address.AbsolutePath == "/home.htm";
                var token = CookieValue(request.Request.Headers.Contains("Cookie") ? request.Request.Headers.GetHeader("Cookie") : "");
                var user = token == ExpectedToken && token.Length > 0
                    ? "{id:'" + (UnknownIdentity ? "unknown" : Account) + "'}" : "null";
                if (home)
                {
                    HomeRequests++;
                    if (address.Query.Contains("userId=", StringComparison.Ordinal)) RedirectedHomeRequests++;
                    if (HomeRelease is { Task.IsCompleted: false } pending)
                    { HomeEntered!.TrySetResult(true); await pending.Task; }
                }
                else BlockedResources++;
                if (home && (RedirectHomeAway || RedirectHomeToLogin))
                {
                    request.Response = core.Environment.CreateWebResourceResponse(null, 302, "Local Redirect Fixture",
                        "Location: " + (RedirectHomeToLogin ? "https://www.icourse163.org/" : "https://outside.example/") + "\r\nCache-Control: no-store\r\n");
                    return;
                }
                if (address.AbsolutePath == "/synthetic-slow-resource" && ResourceRelease is { Task.IsCompleted: false } resourcePending)
                { ResourceEntered!.TrySetResult(true); await resourcePending.Task; }
                var html = "<!doctype html><html><meta charset='utf-8'><title>MOOC synthetic official page</title>"
                    + "<script>window.webUser=" + (home && DelayedHomeIdentity ? "null" : user) + ";"
                    + (home ? "document.cookie='Hm_lpvt_fixture='+String(Math.random())+'; Path=/; Secure';document.cookie='Hm_lvt_fixture=1792775943,1792843420; Path=/; Secure';" : "")
                    + (home ? "if(!location.search&&window.webUser){location.search='?userId='+window.webUser.id;}" : "")
                    + "</script>"
                    + (home && (SlowHomeScript && address.Query.Length > 0 || DelayedHomeIdentity)
                        ? "<script src='/synthetic-slow-resource'></script>" : "")
                    + (home && DelayedHomeIdentity ? "<script>window.webUser=" + user
                        + ";if(!location.search&&window.webUser){location.search='?userId='+window.webUser.id;}</script>" : "")
                    + "<body>中国大学 MOOC 官方页面合成验收"
                    + (home && SlowHomeResource && address.Query.Length > 0 ? "<img src='/synthetic-slow-resource'>" : "") + "</body></html>";
                request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)),
                    200, "Local Fixture", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            };
        };
    }

    public static void SetCookie(WebView2 browser, string token)
    {
        var manager = browser.CoreWebView2.CookieManager;
        var cookie = manager.CreateCookie(AuthName, token, ".icourse163.org", "/");
        cookie.IsSecure = cookie.IsHttpOnly = true;
        cookie.Expires = DateTime.MinValue; cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        manager.AddOrUpdateCookie(cookie);
    }

    public void PauseTerm()
    { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void ResetPause() { Entered = Release = null; HomeEntered = HomeRelease = null; LateRenewal = false; }
    public HttpMessageHandler CreateHandler() => new Handler(this);
    private static string CookieValue(string header) => header.Split(';').Select(pair => pair.Trim().Split('=', 2))
        .FirstOrDefault(pair => pair.Length == 2 && pair[0] == AuthName)?[1] ?? "";

    private sealed class Handler(Fixtures fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            fixture.Requests++; var uri = request.RequestUri!; fixture.RequestPaths.Add(uri.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(token);
            var fields = RainAddressPolicy.Query(new Uri("https://www.icourse163.org/?" + body))!;
            var query = RainAddressPolicy.Query(uri)!;
            var auth = CookieValue(request.Headers.TryGetValues("Cookie", out var values) ? string.Join(';', values) : "");
            if (uri.Scheme != "https" || uri.IdnHost != MoocPlatform.Host || request.Method != HttpMethod.Post
                || !MoocPlatform.IsReadRequest(uri.AbsolutePath, fields) || query.Count != 1 || query.GetValueOrDefault("csrfKey") != auth
                || request.Headers.GetValues("edu-script-token").Single() != auth)
                throw new InvalidOperationException("Unexpected metadata fixture request.");
            var valid = auth == fixture.ExpectedToken && auth.Length > 0 && !fixture.RejectRpc;
            if (uri.AbsolutePath == MoocPlatform.LearnedTermRpc && fixture.Release is { Task.IsCompleted: false } pending)
            { fixture.Entered!.TrySetResult(true); await pending.Task; }
            if (!valid)
            {
                var rejection = Json("{\"code\":-2,\"message\":\"not logged in\"}");
                rejection.Headers.TryAddWithoutValidation("Set-Cookie", AuthName + "=; Domain=.icourse163.org; Path=/; Max-Age=0");
                return rejection;
            }
            var json = uri.AbsolutePath == MoocPlatform.CourseRpc ? Catalog(fields["p"], fixture.EmptyCourses)
                : uri.AbsolutePath == MoocPlatform.LearnedTermRpc ? fixture.UnknownTerm ? "{\"code\":0,\"result\":{\"mocTermDto\":{\"id\":" + fields["termId"] + ",\"unexpected\":[]}}}" : LearnedTerm(fields["termId"])
                : fixture.UnknownNewExams ? "{\"code\":0,\"result\":{\"unexpected\":[]}}" : NewExams();
            var response = Json(json);
            if (fixture.RenewNext && uri.AbsolutePath == MoocPlatform.CourseRpc || fixture.LateRenewal && uri.AbsolutePath == MoocPlatform.LearnedTermRpc)
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", AuthName + "=synthetic-renewed; Domain=.icourse163.org; Path=/; Secure; HttpOnly; SameSite=Strict");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "SYNTHETIC_DELETE=; Domain=.icourse163.org; Path=/; Max-Age=0");
                if (!fixture.LateRenewal) { fixture.ExpectedToken = "synthetic-renewed"; fixture.RenewNext = false; }
            }
            return response;
        }
    }

    private static string Catalog(string page, bool empty)
    {
        if (empty) return "{\"code\":0,\"result\":{\"result\":[],\"pagination\":{\"totlePageCount\":0,\"totleCount\":0}}}";
        return "{\"code\":0,\"result\":{\"result\":[{\"id\":1001,\"name\":\"合成 MOOC 课程\",\"mode\":" + (page == "1" ? "0" : "20")
            + ",\"schoolPanel\":{\"shortName\":\"PKU\"},\"termPanel\":{\"id\":" + (page == "1" ? "2001" : "2002")
            + ",\"courseId\":1001}}],\"pagination\":{\"pageIndex\":" + page + ",\"totlePageCount\":2,\"totleCount\":2}}}";
    }
    private static string Term(string term) => """
        {"code":0,"result":{"id":TERM,"chapters":[{"homeworks":[{"name":"同名合成作业","test":{"id":3001,"usedTryCount":1,"deadline":LONG}}],
        "quizs":[{"name":"MOOC 待做测验","test":{"id":3002,"usedTryCount":0,"deadline":SOON}},
        {"name":"MOOC 状态待核对","test":{"id":3003,"deadline":LONG}}],"exam":null}],"exams":[]}}
        """.Replace("TERM", term).Replace("LONG", DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds().ToString())
        .Replace("SOON", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds().ToString());
    private static string LearnedTerm(string term)
    {
        using var document = System.Text.Json.JsonDocument.Parse(Term(term));
        return "{\"code\":0,\"result\":{\"mocTermDto\":" + document.RootElement.GetProperty("result").GetRawText() + ",\"lastLearnUnitId\":null}}";
    }
    private static string NewExams() => """
        {"code":0,"result":[{"id":7001,"name":"MOOC 新版考试","examType":2,"usedTryCount":0,"userScore":0,"totalScore":100,"deadline":DUE}]}
        """.Replace("DUE", DateTimeOffset.UtcNow.AddHours(7).ToUnixTimeMilliseconds().ToString());
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
