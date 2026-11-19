using AccessProbe;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AccessProbe.Mobile;

public sealed record MobileLoginRecoveryResult(string Outcome, int Checks, IReadOnlyList<string> Results,
    bool OfficialNetworkAccessed = false, bool ProductionDataAccessed = false);

public static class MobileLoginRecoveryChecks
{
    private const string Account = "synthetic-recovery-account";
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef");
    public static async Task<MobileLoginRecoveryResult> RunAsync(string directory, ILocalLinkProtector protector)
    {
        Directory.CreateDirectory(directory); var results = new List<string>(); var now = DateTimeOffset.UtcNow;
        void Check(bool value, string label)
        { if (!value) throw new InvalidDataException("Login recovery check failed: " + label); results.Add(label); }
        TaskStore Store(string name) => new(Path.Combine(directory, name + ".db"), protector);
        MobileSession Session(string platform, MetadataSessionCookie[] cookies, string? account = null) =>
            new(Guid.NewGuid().ToString("N"), platform, now, cookies, account, InferredScopeKeys: cookies.Select(cookie => cookie.Key).ToArray());

        var xt = MobileCookieSnapshot.Capture("xuetang", [new("https://www.xuetangx.com/", "csrftoken=synthetic-csrf", "csrftoken=synthetic-csrf"),
            new("https://www.xuetangx.com/api/", "csrftoken=synthetic-csrf; sessionid=synthetic-session"),
            new("https://www.xuetangx.com" + XuetangPlatform.Profile, "csrftoken=synthetic-csrf; sessionid=synthetic-session")]);
        Check(xt.Count(cookie => cookie.Name == "csrftoken") == 1 && !xt.Single(cookie => cookie.Name == "csrftoken").HttpOnly,
            "WebView-visible CSRF stays visible without duplicate path aliases");
        Check(xt.Single(cookie => cookie.Name == "sessionid").Path == "/api/" && xt.Single(cookie => cookie.Name == "sessionid").HttpOnly,
            "API-only session remains scoped and HttpOnly");
        Check(!xt.Single(cookie => cookie.Name == "sessionid").AppliesTo(XuetangPlatform.Login), "API session never reaches the homepage");
        var ucampus = MobileCookieSnapshot.Capture("ucampus", [new("https://u.unipus.cn/", ""),
            new("https://u.unipus.cn/user/", "ucamUserId=" + Account + "; DTS_SESSIONID=synthetic-old", "ucamUserId=" + Account),
            new("https://sso.unipus.cn/sso/", "TGC=synthetic-tgc")]);
        Check(MobileSessionRules.Account("ucampus", ucampus) == Account, "user-scoped Ucampus identity is captured");
        Check(ucampus.Single(cookie => cookie.Name == "TGC").AppliesTo(new("https://sso.unipus.cn/sso/login")), "scoped Ucampus SSO ticket survives capture");
        Check(ucampus.All(cookie => !cookie.AppliesTo(new("https://unipus.cn/"))), "captured host scope is never promoted to parent domain");
        var caslogc = Uri.EscapeDataString(JsonSerializer.Serialize(new { uuid = Account }));
        var zhs = MobileCookieSnapshot.Capture("zhihuishu", [new(MetadataPlatform.Zhihuishu.Home.AbsoluteUri, "CASLOGC=" + caslogc + "; jt-cas=synthetic-agent", "CASLOGC=" + caslogc + "; jt-cas=synthetic-agent"),
            new("https://user.zhihuishu.com/zhsuser/login/", "CASTGC=synthetic-cas"),
            new("https://hiexam.zhihuishu.com/atHomeworkExam/", "getByNative=synthetic-exam", "getByNative=synthetic-exam")]);
        Check(MobileSessionRules.Account("zhihuishu", zhs) == Account && !zhs.Single(cookie => cookie.Name == "jt-cas").HttpOnly,
            "student-home credential visibility comes from WebView evidence");
        Check(zhs.Single(cookie => cookie.Name == "CASTGC").AppliesTo(new("https://user.zhihuishu.com/zhsuser/login/isLogin")),
            "host-only Zhihuishu CAS is captured on its verification path");
        Check(!zhs.Single(cookie => cookie.Name == "getByNative").HttpOnly && !zhs.Single(cookie => cookie.Name == "getByNative").AppliesTo(MetadataPlatform.Zhihuishu.Home),
            "exam credential is visible only within its proven document scope");
        try { MobileCookieSnapshot.Capture("xuetang", [new("https://foreign.invalid/", "sessionid=synthetic")]); throw new Exception("Foreign scope accepted"); }
        catch (InvalidDataException) { Check(true, "foreign cookie reads are rejected"); }
        try { MobileCookieSnapshot.Capture("xuetang", [new("https://www.xuetangx.com/api/", "sessionid=a; sessionid=b")]); throw new Exception("Ambiguity accepted"); }
        catch (InvalidDataException) { Check(true, "unknown duplicate scope cannot silently choose credentials"); }
        var saved = Session("xuetang", xt);
        Check(MobileCookieSnapshot.MissingForRestore(saved, xt).Count == 0, "reopening preserves browser attributes and existing cookie values");
        Check(MobileCookieSnapshot.MissingForRestore(saved, []).Count == xt.Length, "empty browser can restore encrypted session");
        Check(MobileCookieSnapshot.MissingForRestore(saved, [xt[0], xt[1] with { Value = "synthetic-new-login" }]).Count == 0,
            "fresh browser login blocks restoring old session");
        Check(MobileCookieSnapshot.CanApplyResponse("ucampus", "ucamUserId=" + Account + "; DTS_SESSIONID=synthetic-old", ucampus.Where(cookie => cookie.Domain == "u.unipus.cn").ToArray()),
            "same browser generation accepts official renewal");
        Check(!MobileCookieSnapshot.CanApplyResponse("ucampus", "ucamUserId=other; DTS_SESSIONID=synthetic-old", ucampus.Where(cookie => cookie.Domain == "u.unipus.cn").ToArray()),
            "browser account change rejects stale response writeback");
        var renewed = MobileSessionRules.Renew(Session("ucampus", ucampus, Account), ucampus,
            [new("DTS_SESSIONID", "synthetic-renewed", "u.unipus.cn", "/user", true, true, null)], now);
        Check(renewed.Cookies.Count(cookie => cookie.Name == "DTS_SESSIONID") == 1 && renewed.Cookies.Single(cookie => cookie.Name == "DTS_SESSIONID").Value == "synthetic-renewed",
            "known Set-Cookie replaces the observed inferred path alias");
        var diagnosticPath = Path.Combine(directory, "login-diagnostic.json");
        var diagnostics = new MobileLoginDiagnostics("xuetang", diagnosticPath);
        diagnostics.Record("main", "blocked", "https://foreign.invalid/?sessionid=synthetic-private&account=91001");
        diagnostics.Record("resource", "script_failed", "https://fe-static-yuketang.yuketang.cn/fe/static/xt/1.6.378/xtassets/login.js?token=synthetic-private", 404);
        diagnostics.Observe(xt);
        diagnostics.Complete(new("xuetang", true, false, 0, ["XT001", "synthetic-private"], "synthetic-private"));
        var diagnosticText = File.ReadAllText(diagnosticPath);
        Check(!diagnosticText.Contains("synthetic-private", StringComparison.Ordinal) && !diagnosticText.Contains("91001", StringComparison.Ordinal)
            && !diagnosticText.Contains("synthetic-session", StringComparison.Ordinal) && !diagnosticText.Contains("https:", StringComparison.Ordinal),
            "login diagnostics never persist credentials, account IDs, messages or URLs");
        Check(diagnostics.Snapshot() is { Cookies: 2, SessionPresent: true, AccountConfirmed: false, CatalogCovered: false }
            && diagnostics.Snapshot().Codes.SequenceEqual(["XT001"]), "XT001 diagnostic keeps only safe session facts");
        Check(MobileLoginDiagnostics.ResourceKind("xuetang", "https://fe-static-yuketang.yuketang.cn/fe/static/xt/1.6.378/xtassets/login.js") == "script"
            && MobileLoginDiagnostics.ResourceKind("zhihuishu", "https://login.zhihuishu.com/assets/index.js") == "script"
            && MobileLoginDiagnostics.ResourceKind("zhihuishu", "https://login.zhihuishu.com/styles/index.css") == "style",
            "official login assets are classified without their query strings");
        Check(MobileLoginDiagnostics.ResourceKind("zhihuishu", "https://login.zhihuishu.com.foreign.invalid/assets/index.js") == "none"
            && MobileLoginDiagnostics.Destination("xuetang", "intent://private") == "unsupported", "unrelated resource and external protocol cannot enter diagnostic identity");

        foreach (var mode in new[] { "ok", "unknown-catalog", "forbidden", "offline", "expired", "changed-account" })
        {
            var store = Store("xt-" + mode); var vault = new MobileVault(store, protector); var handler = new XtHandler(mode);
            vault.Save(Session("xuetang", xt, mode == "changed-account" ? "91002" : null));
            var manual = store.Create(new("保留的手动任务", "xuetang", "测试课程", DeadlineKind.Unknown, null, null, null, null, "", "", null), now);
            var sync = new MobileSync(store, vault, _ => handler);
            if (mode == "changed-account")
            {
                try { await sync.RunAsync("xuetang", true); throw new Exception("Changed account accepted"); }
                catch (OperationCanceledException) { Check(handler.CatalogCalls == 0, "verified account change stops before reading any catalog"); }
                continue;
            }
            var result = await sync.RunAsync("xuetang", true);
            Check(store.Get(manual.Id) is not null && result.Tasks == 0 && handler.TaskCalls == 0, "login and catalog failure preserve tasks: " + mode);
            Check(result.AccountVerified == (mode != "expired") && result.LoginRequired == (mode == "expired"), "account verification is independent of catalog coverage: " + mode);
            Check(result.CatalogCovered == (mode == "ok"), "unknown or failed catalog stays incomplete: " + mode);
            Check(mode == "expired" ? store.LoadPlatformSessionInfo("xuetang").State == "expired" : vault.Load("xuetang")?.AccountId == "91001",
                "official profile is bound before catalog result: " + mode);
        }

        foreach (var mode in new[] { "renewed", "expired", "offline" })
        {
            var store = Store("uc-" + mode); var vault = new MobileVault(store, protector); var original = Session("ucampus", ucampus, Account);
            vault.Save(original); vault.Finish(original, true, false, default);
            Check(vault.Load("ucampus") is null && vault.LoadForRevalidation("ucampus") is not null, "unverified Ucampus session remains available for official recheck: " + mode);
            var handler = new UcHandler(mode); var writes = 0;
            var sync = new MobileSync(store, vault, _ => handler, writeBrowserCookies: (_, uri, _, changes, _) =>
            { Check(uri.Host == "u.unipus.cn" && changes.All(cookie => cookie.Value == "synthetic-renewed"), "writeback contains only official response cookies"); writes++; return Task.CompletedTask; });
            var result = await sync.RunAsync("ucampus", true);
            Check(result.AccountVerified == (mode == "renewed") && result.LoginRequired == (mode == "expired"), "SSO renewal separates real expiry and unavailable service: " + mode);
            Check(mode == "renewed" ? writes == 1 && vault.Load("ucampus")!.Cookies.Single(cookie => cookie.Name == "DTS_SESSIONID").Value == "synthetic-renewed"
                : vault.LoadForRevalidation("ucampus") is not null, "renewed cookies persist and rejected recheck retains encrypted evidence: " + mode);
            Check(handler.Requests <= 7 && handler.OnlyGet, "official SSO navigation is read only and bounded: " + mode);
        }

        var zStore = Store("zhs"); var zVault = new MobileVault(zStore, protector); zVault.Save(Session("zhihuishu", zhs, Account));
        using (var reader = new MetadataHttpReader(MetadataPlatform.Zhihuishu,
            (uri, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(zhs.Where(cookie => cookie.AppliesTo(uri)).ToArray()),
            new CallbackHandler(request =>
            {
                Check(request.RequestUri!.Host == "cloudapi.polymas.com" && request.Headers.GetValues("Authorization").Single() == "synthetic-agent"
                    && !request.Headers.Contains("Cookie"), "agent API receives only proven visible authorization, no CAS cookies");
                return Json("{\"code\":200,\"success\":true,\"data\":{\"list\":[],\"total\":0,\"totalPage\":0,\"page\":0,\"size\":0}}");
            }), testMetadataKey: Key))
            Check((await reader.ReadAsync(new("agent-courses"), default)).Outcome == "ok", "Android visible credential reaches existing agent catalog contract");
        using (var reader = new MetadataHttpReader(MetadataPlatform.Zhihuishu,
            (uri, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(zhs.Where(cookie => cookie.AppliesTo(uri)).ToArray()),
            new CallbackHandler(request =>
            {
                Check(request.RequestUri!.Host == "hike-examstu.zhihuishu.com" && request.Headers.GetValues("Authorization").Single() == "synthetic-exam",
                    "flip exam API receives its document-scoped visible credential");
                return Json("{\"status\":200,\"rt\":{\"list\":[],\"rowCount\":0}}");
            })))
            Check((await reader.ReadAsync(new("flip-exam") { Course = new("101", "合成翻转课", "flip", "") }, default)).Outcome == "ok",
                "exam credential is independent of student localStorage token");
        var zHandler = new ZhsHandler();
        var zResult = await new MobileSync(zStore, zVault, _ => zHandler, Key).RunAsync("zhihuishu", true);
        Check(zResult.AccountVerified && zResult.CatalogCovered && !zResult.LoginRequired && zHandler.CasCalls == 1,
            "host-only CAS verification recovers Android student session");
        Check(zStore.LoadTasks().Count == 0 && zHandler.NoCredentialLeak, "SSO recovery preserves course selection and secret boundaries");
        return new("passed", results.Count, results);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
    private sealed class XtHandler(string mode) : HttpMessageHandler
    {
        public int CatalogCalls { get; private set; }
        public int TaskCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get || request.RequestUri.Host != XuetangPlatform.Host || request.Content is not null)
                throw new InvalidDataException("Unexpected synthetic Xuetang request.");
            if (!request.Headers.GetValues("Cookie").Single().Contains("sessionid=synthetic-session", StringComparison.Ordinal))
                throw new InvalidDataException("Scoped browser session missing from API request.");
            if (path == XuetangPlatform.LoginCheck) return Task.FromResult(Json("{\"success\":true,\"data\":{\"is_login\":" + (mode == "expired" ? "false" : "true") + "}}"));
            if (path == XuetangPlatform.Profile) return Task.FromResult(Json("{\"success\":true,\"data\":{\"id\":91001}}"));
            if (path != XuetangPlatform.Courses) { TaskCalls++; throw new InvalidDataException("Login must not read tasks."); }
            CatalogCalls++;
            if (mode == "offline") throw new HttpRequestException("Synthetic unavailable directory.");
            return Task.FromResult(mode == "forbidden" ? Json("{}", HttpStatusCode.Forbidden)
                : Json(mode == "unknown-catalog" ? "{\"success\":true,\"data\":{\"unexpected\":[]}}"
                    : "{\"success\":true,\"data\":{\"product_list\":[],\"count\":0}}"));
        }
    }
    private sealed class UcHandler(string mode) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool OnlyGet { get; private set; } = true;
        private int profiles, callbacks;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; var uri = request.RequestUri!; OnlyGet &= request.Method == HttpMethod.Get && request.Content is null && uri.Scheme == "https";
            if (uri.AbsolutePath == "/user/data/getToken")
                return Task.FromResult(Json(++profiles == 1 ? "{\"code\":401,\"message\":\"未登录\"}" : JsonSerializer.Serialize(new { openId = Account, token = "synthetic-unused" })));
            if (uri.AbsolutePath == "/user/comm/login")
            {
                var result = Json("", HttpStatusCode.Found);
                result.Headers.Location = new Uri(++callbacks == 1 ? "https://sso.unipus.cn/sso/login?service=" + Uri.EscapeDataString(MetadataPlatform.Ucampus.Login.AbsoluteUri)
                    : "https://u.unipus.cn/user/student?school_id=");
                if (callbacks > 1) result.Headers.TryAddWithoutValidation("Set-Cookie", "DTS_SESSIONID=synthetic-renewed; Path=/user; Secure; HttpOnly");
                return Task.FromResult(result);
            }
            if (uri.Host == "sso.unipus.cn")
            {
                if (!request.Headers.GetValues("Cookie").Single().Contains("TGC=synthetic-tgc", StringComparison.Ordinal)) throw new InvalidDataException("Missing scoped SSO ticket.");
                if (mode == "offline") throw new HttpRequestException("Synthetic unavailable SSO.");
                if (mode == "expired") return Task.FromResult(Json("<!doctype html><form action='/sso/login'>合成登录</form>"));
                var result = Json("", HttpStatusCode.Found); result.Headers.Location = new Uri(MetadataPlatform.Ucampus.Login.AbsoluteUri + "&ticket=ST-synthetic"); return Task.FromResult(result);
            }
            if (uri.AbsolutePath is "/user/student" or "/user/student/index")
                return Task.FromResult(Json("<!doctype html><div class='my_course_box'><div class='my_course_list'></div></div>"));
            throw new InvalidDataException("Unexpected synthetic Ucampus request.");
        }
    }
    private sealed class ZhsHandler : HttpMessageHandler
    {
        public int CasCalls { get; private set; }
        public bool NoCredentialLeak { get; private set; } = true;
        private int profiles;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; string json;
            if (uri.AbsolutePath.EndsWith("getLoginUserInfo", StringComparison.Ordinal))
                json = ++profiles == 1 ? "{\"code\":401,\"message\":\"未登录\"}" : JsonSerializer.Serialize(new { code = 200, result = new { uuid = Account } });
            else if (uri.Host == "user.zhihuishu.com") { CasCalls++; json = "{\"status\":200,\"rt\":true}"; }
            else if (uri.Host == "passport.zhihuishu.com")
            {
                var redirect = Json("", HttpStatusCode.Found); redirect.Headers.Location = new Uri("https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect=" + Uri.EscapeDataString(MetadataPlatform.Zhihuishu.Home.AbsoluteUri));
                return Task.FromResult(redirect);
            }
            else if (uri.Host == "cloudapi.polymas.com" && uri.AbsolutePath.StartsWith("/oauth/", StringComparison.Ordinal))
            {
                NoCredentialLeak &= !request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization");
                var redirect = Json("", HttpStatusCode.Found); redirect.Headers.Location = MetadataPlatform.Zhihuishu.Home; return Task.FromResult(redirect);
            }
            else if (uri.Host == MetadataPlatform.Zhihuishu.Host) json = "<!doctype html><title>合成学生首页</title>";
            else if (uri.Host == "cloudapi.polymas.com") json = "{\"code\":200,\"success\":true,\"data\":{\"list\":[],\"total\":0,\"totalPage\":0,\"page\":0,\"size\":0}}";
            else if (uri.AbsolutePath.EndsWith("getCourseList", StringComparison.Ordinal)) json = "{\"status\":200,\"rt\":{\"pageList\":[],\"pageNum\":0,\"pageSize\":20,\"totalCount\":0,\"totalPages\":0}}";
            else if (uri.AbsolutePath.EndsWith("queryShareCourseInfo", StringComparison.Ordinal)) json = "{\"code\":200,\"result\":{\"courseOpenDtos\":[],\"totalCount\":0}}";
            else if (uri.AbsolutePath.EndsWith("queryStudentAICourseList", StringComparison.Ordinal)) json = "{\"status\":200,\"rt\":[]}";
            else throw new InvalidDataException("Unexpected synthetic Zhihuishu request.");
            return Task.FromResult(Json(json));
        }
    }
}
