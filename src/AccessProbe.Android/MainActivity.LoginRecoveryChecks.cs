using AccessProbe;
using Android.Webkit;
using System.Text;
using System.Text.Json;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private async Task RunLoginRecoveryWebChecks(string mode)
    {
        var directory = System.IO.Path.Combine(MobileData.Root(this), "synthetic-login-recovery-test");
        Directory.CreateDirectory(directory); var results = new List<string>(); var networkRequests = 0;
        void Check(bool value, string label)
        { if (!value) throw new InvalidDataException("WebView recovery check failed: " + label); results.Add(label); }
        void Report(string outcome, string? error = null) => File.WriteAllText(System.IO.Path.Combine(directory, "webview-" + mode + ".json"),
            JsonSerializer.Serialize(new { outcome, checks = results.Count, results, error, officialNetworkAccessed = false,
                productionDataAccessed = false, interceptedRequests = networkRequests }));
        try
        {
            var protector = new AndroidDataProtector("LearningTaskAggregator.SyntheticLoginRecovery.v1");
            store = new(System.IO.Path.Combine(directory, "tasks.db"), protector); vault = new(store, protector);
            store.SaveSettings(new(false, false));
            sync = new(store, vault, _ => new RecoveryWebHandler(), writeBrowserCookies: WriteBrowserResponseCookies);
            CreateLoginPage("xuetang");
            var manager = Android.Webkit.CookieManager.Instance!;
            async Task Cookie(string origin, string header)
            {
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                manager.SetCookie(origin, header, new CookieWriteResult(completed));
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            async Task Load(string address)
            {
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                web!.SetWebViewClient(new OfflineRecoveryClient(() => networkRequests++, () => completed.TrySetResult(), LoginPageStarted));
                web.LoadUrl(address); await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            async Task WaitFor(Func<bool> condition)
            {
                for (var attempt = 0; attempt < 100; attempt++)
                { if (condition()) return; await Task.Delay(100); }
                throw new TimeoutException("Synthetic official page did not finish.");
            }
            if (mode == "restart")
            {
                await Load(XuetangPlatform.Login.AbsoluteUri);
                var restored = await MobilePlatforms.ReadBrowserCookiesAsync("xuetang", web!);
                Check(restored.Single(cookie => cookie.Name == "csrftoken").Value == "synthetic-csrf" && !restored.Single(cookie => cookie.Name == "csrftoken").HttpOnly,
                    "persistent visible cookie keeps attributes across process restart");
                Check(restored.Single(cookie => cookie.Name == "sessionid").Value == "synthetic-session" && restored.Single(cookie => cookie.Name == "sessionid").HttpOnly,
                    "persistent HttpOnly API cookie survives process restart");
                Check(vault.Load("xuetang")?.AccountId == "91001", "encrypted verified login survives process restart");
                Report("passed"); Finish(); return;
            }
            if (mode != "webview") throw new InvalidDataException("Unknown synthetic recovery mode.");
            var removed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.RemoveAllCookies(new ScriptResult(_ => removed.TrySetResult(true)));
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var expires = DateTimeOffset.UtcNow.AddDays(2).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
            await Cookie("https://www.xuetangx.com/", "csrftoken=synthetic-csrf; Path=/; Secure; Expires=" + expires);
            await Cookie("https://www.xuetangx.com/api/", "sessionid=synthetic-session; Path=/api; Secure; HttpOnly; Expires=" + expires);
            await Cookie("https://u.unipus.cn/user/", "ucamUserId=synthetic-recovery-account; Path=/user; Secure");
            await Cookie("https://u.unipus.cn/user/", "DTS_SESSIONID=synthetic-old; Path=/user; Secure; HttpOnly");
            await Cookie("https://sso.unipus.cn/sso/login", "TGC=synthetic-tgc; Path=/sso; Secure; HttpOnly");
            await Cookie("https://onlineweb.zhihuishu.com/", "CASLOGC=" + Uri.EscapeDataString("{\"uuid\":\"synthetic-recovery-account\"}") + "; Domain=.zhihuishu.com; Path=/; Secure");
            await Cookie("https://onlineweb.zhihuishu.com/", "jt-cas=synthetic-agent; Domain=.zhihuishu.com; Path=/; Secure");
            await Cookie("https://user.zhihuishu.com/zhsuser/login/", "CASTGC=synthetic-cas; Path=/zhsuser/login; Secure; HttpOnly");
            await Cookie("https://hiexam.zhihuishu.com/atHomeworkExam/", "getByNative=synthetic-exam; Path=/atHomeworkExam; Secure");
            await Cookie("https://foreign.invalid/", "sessionid=synthetic-foreign; Path=/; Secure"); manager.Flush();
            await Load(XuetangPlatform.Login.AbsoluteUri);
            var xt = await MobilePlatforms.ReadBrowserCookiesAsync("xuetang", web!);
            Check(!xt.Single(cookie => cookie.Name == "csrftoken").HttpOnly, "real WebView distinguishes visible CSRF from HttpOnly");
            Check(xt.Single(cookie => cookie.Name == "sessionid").HttpOnly && xt.Single(cookie => cookie.Name == "sessionid").Path == "/api/",
                "real WebView API-only HttpOnly cookie is captured");
            Check(xt.All(cookie => cookie.Value != "synthetic-foreign"), "foreign browser cookie stays outside platform snapshot");
            var snapshot = new MobileSession(Guid.NewGuid().ToString("N"), "xuetang", DateTimeOffset.UtcNow, xt, InferredScopeKeys: xt.Select(cookie => cookie.Key).ToArray());
            await MobilePlatforms.RestoreBrowserAsync(snapshot with { Cookies = xt.Select(cookie => cookie with { HttpOnly = true }).ToArray() });
            Check(JsonSerializer.Deserialize<string>(await Evaluate(web!, "document.cookie"))!.Contains("csrftoken=synthetic-csrf", StringComparison.Ordinal),
                "old 1.1.3 archive cannot overwrite live cookie visibility");
            web!.ClearCache(true); await Load(XuetangPlatform.Login.AbsoluteUri);
            Check(JsonSerializer.Deserialize<string>(await Evaluate(web!, "document.cookie"))!.Contains("csrftoken=synthetic-csrf", StringComparison.Ordinal),
                "refreshing cache preserves current official cookie");
            Check(MobilePlatforms.Login("xuetang") == XuetangPlatform.Login, "Android opens the official homepage login control");
            var uc = await MobilePlatforms.ReadBrowserCookiesAsync("ucampus", web!);
            Check(MobileSessionRules.Account("ucampus", uc) == "synthetic-recovery-account", "WebView user path preserves Ucampus identity");
            Check(uc.Any(cookie => cookie.Name == "TGC" && cookie.AppliesTo(new("https://sso.unipus.cn/sso/login"))), "WebView SSO path captures renewal ticket");
            var zhs = await MobilePlatforms.ReadBrowserCookiesAsync("zhihuishu", web!);
            Check(!zhs.Single(cookie => cookie.Domain == "onlineweb.zhihuishu.com" && cookie.Name == "jt-cas").HttpOnly,
                "real WebView proves student authorization visibility");
            Check(zhs.Any(cookie => cookie.Name == "CASTGC" && cookie.Domain == "user.zhihuishu.com" && cookie.AppliesTo(new("https://user.zhihuishu.com/zhsuser/login/isLogin"))),
                "real WebView host-only CAS cookie is retained");
            Check(!zhs.Single(cookie => cookie.Name == "getByNative").HttpOnly && zhs.Single(cookie => cookie.Name == "getByNative").Path == "/atHomeworkExam/",
                "real WebView captures visible exam document credential");
            var observed = uc.Where(cookie => cookie.AppliesTo(new("https://u.unipus.cn/user/data/getToken"))).ToArray();
            var change = new MetadataSessionCookie("DTS_SESSIONID", "synthetic-renewed", "u.unipus.cn", "/user", true, true, null);
            await MobilePlatforms.ApplyResponseCookiesAsync("ucampus", new("https://u.unipus.cn/user/data/getToken"), observed, [change], default);
            Check(manager.GetCookie("https://u.unipus.cn/user/data/getToken")!.Contains("DTS_SESSIONID=synthetic-renewed", StringComparison.Ordinal),
                "official renewal reaches WebView cookie store");
            await Cookie("https://u.unipus.cn/user/", "ucamUserId=synthetic-other-account; Path=/user; Secure");
            await MobilePlatforms.ApplyResponseCookiesAsync("ucampus", new("https://u.unipus.cn/user/data/getToken"), observed, [change with { Value = "synthetic-stale" }], default);
            Check(!manager.GetCookie("https://u.unipus.cn/user/data/getToken")!.Contains("synthetic-stale", StringComparison.Ordinal),
                "new browser account blocks stale renewal writeback");
            await Cookie("https://www.xuetangx.com/", "csrftoken=synthetic-fresh; Path=/; Secure");
            await MobilePlatforms.RestoreBrowserAsync(snapshot);
            Check(manager.GetCookie("https://www.xuetangx.com/")!.Contains("csrftoken=synthetic-fresh", StringComparison.Ordinal),
                "reopening never overwrites a fresh browser login");
            await Cookie("https://www.xuetangx.com/", "csrftoken=synthetic-csrf; Path=/; Secure; Expires=" + expires);
            sync = new(store, vault, _ => new RecoveryWebHandler(Expired: true), writeBrowserCookies: WriteBrowserResponseCookies);
            await CaptureLogin("xuetang");
            Check(web is not null && vault.Load("xuetang") is null && store.LoadPlatformSessionInfo("xuetang").State == "expired",
                "XT001 keeps the official authorization page open and never confirms a rejected account");
            Check(connectionTitle?.Text == "尚未接入：官网仍要求登录" && connectionNext?.Text?.Contains("授权确认", StringComparison.Ordinal) == true
                && connectionDetails?.Text?.Contains("XT001", StringComparison.Ordinal) == true,
                "XT001 explains unconfirmed authorization with safe code in separate details");
            Check(loginDiagnostics!.Snapshot().Codes.Contains("XT001") && !loginDiagnostics.Snapshot().AccountConfirmed,
                "official rejection is diagnosed without logging credential values");
            Check(manager.GetCookie("https://www.xuetangx.com/api/")!.Contains("sessionid=synthetic-session", StringComparison.Ordinal),
                "XT001 alone does not clear the still-open official browser session");
            sync = new(store, vault, _ => new RecoveryWebHandler(), writeBrowserCookies: WriteBrowserResponseCookies);
            var loginWeb = web!;
            loginClient = new OfflineDiagnosticClient("xuetang", message => status.Text = message, LoginPageStarted, loginDiagnostics,
                () => networkRequests++);
            loginWeb.SetWebViewClient(loginClient);
            async Task WaitForDocument()
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (await Evaluate(loginWeb, "document.readyState==='complete' && document.getElementById('authorization-ready')!==null") == "true") return;
                    await Task.Delay(100);
                }
                throw new TimeoutException("Synthetic authorization document did not finish.");
            }
            loginWeb.LoadUrl(XuetangPlatform.Login.AbsoluteUri); await WaitForDocument();
            await ReopenOfficialLogin("xuetang");
            await Task.Delay(200); await WaitForDocument();
            Check(manager.GetCookie("https://www.xuetangx.com/api/")!.Contains("sessionid=synthetic-session", StringComparison.Ordinal),
                "reopening official authorization preserves the browser session after XT001");
            await Evaluate(loginWeb, "location.href='https://www.xuetangx.com.foreign.invalid/authorize?code=synthetic-private'");
            await WaitFor(() => loginDiagnostics.Snapshot().Events.Any(item => item.Outcome == "blocked"));
            Check(loginWeb.Url == XuetangPlatform.Login.AbsoluteUri, "unrelated authorization return cannot replace the official page");
            await Evaluate(loginWeb, "console.error('sessionid=synthetic-private account=91001')");
            await WaitFor(() => loginDiagnostics.Snapshot().Events.Any(item => item.Stage == "renderer" && item.Outcome == "script_failed"));
            var diagnosticText = File.ReadAllText(System.IO.Path.Combine(directory, "xuetang-login.json"));
            Check(!diagnosticText.Contains("synthetic-private", StringComparison.Ordinal) && !diagnosticText.Contains("91001", StringComparison.Ordinal)
                && !diagnosticText.Contains("https:", StringComparison.Ordinal), "real WebView console and blocked returns are redacted before persistence");

            var preserved = store.Create(new("登录恢复保留任务", "xuetang", "合成课程", DeadlineKind.Unknown, null, null, null, null, "", "", null), DateTimeOffset.UtcNow);
            var preservedRecording = store.LoadCourseRecording("xuetang");
            var otherCookies = manager.GetCookie("https://u.unipus.cn/user/");
            // Exercise the clear operation directly; the visible production entry separately requires the user's positive action.
            await ResetOfficialLogin("xuetang");
            Check(string.IsNullOrEmpty(manager.GetCookie("https://www.xuetangx.com/")) && string.IsNullOrEmpty(manager.GetCookie("https://www.xuetangx.com/api/")),
                "explicit platform reset removes visible and HttpOnly cookies across captured paths");
            Check(manager.GetCookie("https://u.unipus.cn/user/") == otherCookies && manager.GetCookie("https://foreign.invalid/")!.Contains("synthetic-foreign", StringComparison.Ordinal),
                "platform reset leaves other platforms and unrelated cookies intact");
            Check(store.Get(preserved.Id) is not null && vault.Load("xuetang") is null, "platform reset preserves tasks and removes only saved authorization");
            Check(store.LoadCourseRecording("xuetang").Revision == preservedRecording.Revision,
                "platform reset leaves course selection unchanged");
            loginLifetime?.Cancel();
            await Cookie("https://www.xuetangx.com/", "csrftoken=synthetic-csrf; Path=/; Secure; Expires=" + expires);
            await Cookie("https://www.xuetangx.com/api/", "sessionid=synthetic-session; Path=/api; Secure; HttpOnly; Expires=" + expires);
            await Load(XuetangPlatform.Login.AbsoluteUri);
            await CaptureLogin("xuetang", false);
            Check(vault.Load("xuetang") is null && web is not null, "anonymous homepage session never triggers automatic authorization");
            await Load(XuetangPlatform.Home.AbsoluteUri);
            await CaptureLogin("xuetang", false);
            Check(web is not null && vault.Load("xuetang")?.AccountId == "91001", "verified course return saves login and keeps consent page visible");
            Check(connectionTitle?.Text?.Contains("已接入", StringComparison.Ordinal) == true, "verified login exposes connection result before explicit completion");
            await CaptureLogin("xuetang");
            Check(web is not null && loginPlatform == "xuetang", "checking again keeps official page and verified result visible");
            ContinueConnectedPlatform("xuetang");
            Check(web is null && loginPlatform is null, "explicit continue returns to course selection after verification");
            Check(store.LoadTasks().Count == 1 && store.Get(preserved.Id) is not null && store.LoadCourseRecording("xuetang").Courses.All(course => course.Detail == "手动录入"),
                "login never imports unselected tasks");
            manager.Flush(); Report("passed");
        }
        catch (Exception error) { Report("failed", error.GetType().Name + ": " + error.Message); }
    }

    private sealed class OfflineRecoveryClient(Action intercepted, Action finished, Action started) : WebViewClient
    {
        public override void OnPageStarted(WebView? view, string? url, Android.Graphics.Bitmap? favicon) => started();
        public override void OnPageFinished(WebView? view, string? url) => finished();
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            intercepted();
            return new("text/html", "UTF-8", new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html><h2>应用内登录恢复隔离检查</h2>")));
        }
        public override void OnReceivedSslError(WebView? view, SslErrorHandler? handler, Android.Net.Http.SslError? error) => handler?.Cancel();
    }

    private sealed class OfflineDiagnosticClient(string platform, Action<string> message, Action started, MobileLoginDiagnostics diagnostics, Action intercepted)
        : OfficialLoginClient(platform, message, started, diagnostics)
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            intercepted();
            return new("text/html", "UTF-8", new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html><h2 id='authorization-ready'>应用内授权隔离检查</h2>")));
        }
    }

    private sealed class RecoveryWebHandler(bool Expired = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != XuetangPlatform.Host || request.Method != HttpMethod.Get || request.Content is not null)
                throw new InvalidDataException("Unexpected synthetic login request.");
            if (!request.Headers.TryGetValues("Cookie", out var values) || !values.Single().Contains("sessionid=synthetic-session", StringComparison.Ordinal)
                || request.Headers.GetValues("X-CSRFToken").Single() != "synthetic-csrf")
                throw new InvalidDataException("Official authorization requires the captured API session and CSRF header.");
            var json = request.RequestUri.AbsolutePath switch
            {
                XuetangPlatform.LoginCheck => "{\"success\":true,\"data\":{\"is_login\":" + (Expired ? "false" : "true") + "}}",
                XuetangPlatform.Profile => "{\"success\":true,\"data\":{\"id\":91001}}",
                XuetangPlatform.Courses => "{\"success\":true,\"data\":{\"product_list\":[],\"count\":0}}",
                _ => throw new InvalidDataException("Login fixture rejects task requests.")
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
