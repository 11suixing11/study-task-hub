using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XuetangTestData;

namespace XuetangSyncHarness;

internal sealed class Fixtures
{
    public const string AuthName = "sessionid";
    public string ExpectedToken { get; set; } = "synthetic-seed";
    public string Account { get; set; } = "91001";
    public bool RejectLogin { get; set; }
    public bool UnknownChapter { get; set; }
    public bool UnknownCatalog { get; set; }
    public bool UnknownProfile { get; set; }
    public bool ResetUnavailable { get; set; }
    public bool RenewNext { get; set; }
    public bool LateRenewal { get; set; }
    public int Requests { get; private set; }
    public int BrowserRequests { get; private set; }
    public bool FailLoginScript { get; set; } = true;
    public bool FailLoginStyle { get; set; }
    public bool EmptyLoginDocument { get; set; }
    public int LoginDocumentRequests { get; private set; }
    public int LoginScriptRequests { get; private set; }
    public int HealthyLoginScriptRequests { get; private set; }
    private const string LoginScript = "https://fe-static-yuketang.yuketang.cn/fe/static/xt/1.6.999/xtassets/synthetic-login.js";
    private const string LoginStyle = "https://fe-static-yuketang.yuketang.cn/fe/static/xt/1.6.999/xtassets/synthetic-login.css";
    public List<string> Paths { get; } = [];
    public TaskCompletionSource<bool>? Entered { get; private set; }
    public TaskCompletionSource<bool>? Release { get; private set; }

    public void BlockBrowser(WebView2 browser)
    {
        browser.CoreWebView2InitializationCompleted += (_, result) =>
        {
            if (!result.IsSuccess) return;
            var core = browser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                BrowserRequests++;
                if (request.Request.Uri == LoginScript)
                {
                    LoginScriptRequests++;
                    if (!FailLoginScript) HealthyLoginScriptRequests++;
                    request.Response = Response(core, FailLoginScript ? "<html>synthetic failed script</html>"
                        : "document.documentElement.setAttribute('data-synthetic-script','ready');", FailLoginScript ? "text/html" : "application/javascript");
                    return;
                }
                if (request.Request.Uri == LoginStyle)
                {
                    request.Response = Response(core, FailLoginStyle ? "<html>synthetic unavailable style</html>"
                        : "body{font-family:system-ui;margin:24px}button{padding:12px}form{margin-top:12px}", FailLoginStyle ? "text/html" : "text/css");
                    return;
                }
                if (request.Request.Uri == XuetangPlatform.Login.AbsoluteUri)
                {
                    LoginDocumentRequests++;
                    var document = EmptyLoginDocument ? "<!doctype html><meta charset='utf-8'><div id='app'></div>"
                        : "<!doctype html><meta charset='utf-8'><title>学堂在线本地登录验收</title><link rel='stylesheet' href='" + LoginStyle
                            + "'><script defer src='" + LoginScript + "'></script><div class='login_con'><button id='synthetic-open-login' class='h_login_login txt'"
                            + " onclick=\"document.getElementById('synthetic-login-form').hidden=false\">登录</button></div>"
                            + "<form id='synthetic-login-form' hidden><input aria-label='合成登录'><button type='button'>合成确认</button></form>";
                    request.Response = Response(core, document, "text/html"); return;
                }
                if (!XuetangPlatform.IsLoginNavigation(request.Request.Uri) && XuetangPlatform.IsLoginFrameNavigation(request.Request.Uri))
                {
                    request.Response = Response(core, "<!doctype html><meta charset='utf-8'><p>合成验证页</p>"
                        + "<script>parent.postMessage('synthetic-captcha-ready','https://www.xuetangx.com')</script>", "text/html");
                    return;
                }
                var html = "<!doctype html><meta charset='utf-8'><title>学堂在线合成登录验收</title><p>本地合成官方登录页，不连接任何账号。</p>";
                request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            };
        };
    }
    private static CoreWebView2WebResourceResponse Response(CoreWebView2 core, string content, string type) => core.Environment.CreateWebResourceResponse(
        new MemoryStream(Encoding.UTF8.GetBytes(content)), 200, "OK", "Content-Type: " + type + "; charset=utf-8\r\nCache-Control: no-store\r\n");
    public static void SetCookie(WebView2 browser, string token)
    {
        var manager = browser.CoreWebView2.CookieManager;
        var cookie = manager.CreateCookie(AuthName, token, ".xuetangx.com", "/");
        cookie.IsSecure = true; cookie.IsHttpOnly = true; cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        cookie.Expires = DateTime.MinValue; manager.AddOrUpdateCookie(cookie);
        var csrf = manager.CreateCookie("csrftoken", "synthetic-csrf", ".xuetangx.com", "/");
        csrf.IsSecure = true; csrf.Expires = DateTime.MinValue; manager.AddOrUpdateCookie(csrf);
    }
    public void PauseChapter()
    { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void ResetPause() { Entered = Release = null; LateRenewal = false; }
    public HttpMessageHandler CreateHandler() => new Handler(this);
    private static string Auth(HttpRequestMessage request) => (request.Headers.TryGetValues("Cookie", out var values) ? string.Join(';', values) : "")
        .Split(';').Select(part => part.Trim().Split('=', 2)).FirstOrDefault(part => part.Length == 2 && part[0] == AuthName)?[1] ?? "";

    private sealed class Handler(Fixtures fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.Requests++; fixture.Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method != HttpMethod.Get || request.Content is not null || !XuetangPlatform.IsReadAddress(request.RequestUri)
                || request.Headers.GetValues("xtbz").Single() != "xt" || request.Headers.GetValues("x-client").Single() != "web")
                throw new InvalidOperationException("Unexpected official metadata fixture request.");
            var valid = Auth(request) == fixture.ExpectedToken && fixture.ExpectedToken.Length > 0 && !fixture.RejectLogin;
            if (!valid)
            {
                var rejected = Metadata.Json(Metadata.Login(false));
                if (request.RequestUri.AbsolutePath != XuetangPlatform.LoginCheck) rejected = Metadata.Json("{\"success\":false,\"error_code\":500300}");
                rejected.Headers.TryAddWithoutValidation("Set-Cookie", "sessionid=; Domain=.xuetangx.com; Path=/; Max-Age=0");
                return rejected;
            }
            if (request.RequestUri.AbsolutePath == XuetangPlatform.Chapter && fixture.Release is { Task.IsCompleted: false } pending)
            { fixture.Entered!.TrySetResult(true); await pending.Task; }
            var response = Metadata.Respond(request.RequestUri).Json;
            if (request.RequestUri.AbsolutePath == XuetangPlatform.Profile)
                response = fixture.UnknownProfile ? Metadata.Envelope(new { nickname = "synthetic" }) : Metadata.Profile(fixture.Account);
            if (request.RequestUri.AbsolutePath == XuetangPlatform.Chapter && fixture.UnknownChapter) response = Metadata.Envelope(new { unexpected = new object[0] });
            if (request.RequestUri.AbsolutePath == XuetangPlatform.Courses && fixture.UnknownCatalog) response = Metadata.Envelope(new { unexpected = new object[0] });
            if (request.RequestUri.AbsolutePath == XuetangPlatform.ResetDeadlines && fixture.ResetUnavailable) return Metadata.Json("{}", HttpStatusCode.ServiceUnavailable);
            var result = Metadata.Json(response);
            if (fixture.RenewNext || fixture.LateRenewal && request.RequestUri.AbsolutePath == XuetangPlatform.Chapter)
            {
                fixture.RenewNext = false; fixture.ExpectedToken = "synthetic-renewed";
                result.Headers.TryAddWithoutValidation("Set-Cookie", "sessionid=synthetic-renewed; Domain=.xuetangx.com; Path=/; Secure; HttpOnly; SameSite=Strict");
                result.Headers.TryAddWithoutValidation("Set-Cookie", "SYNTHETIC_DELETE=; Domain=.xuetangx.com; Path=/; Max-Age=0");
            }
            return result;
        }
    }
}
