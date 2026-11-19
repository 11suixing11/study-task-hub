using AccessProbe;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AccessProbe.Mobile;

public sealed record MobileConnectionCheckResult(string Outcome, int Checks, IReadOnlyList<string> Results,
    bool OfficialNetworkAccessed = false, bool ProductionDataAccessed = false);

public static class MobileConnectionChecks
{
    public static async Task<MobileConnectionCheckResult> RunAsync(string directory, ILocalLinkProtector protector)
    {
        Directory.CreateDirectory(directory); var results = new List<string>();
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidDataException("Mobile connection check: " + label); results.Add(label); }
        TaskStore Store(string name) => new(Path.Combine(directory, name + ".db"), protector);
        MobileSession Session(string platform, string? account = null) => new(Guid.NewGuid().ToString("N"), platform,
            DateTimeOffset.UtcNow, [new(platform == "mooc" ? "NTESSTUDYSI" : "sessionid", "synthetic-login",
                platform == "mooc" ? "www.icourse163.org" : "www.xuetangx.com", "/", true, true, null)], account);
        var missingStore = Store("missing");
        var missing = await new MobileSync(missingStore, new(missingStore, protector), _ => throw new InvalidDataException("No session may access HTTP.")).RunAsync("xuetang", true);
        Check(missing.Connection is { SessionAvailable: false, AccountVerified: false } && ConnectionFeedback.From(missing.Connection).Title == "尚未接入",
            "without a session, the UI explains that login is still needed before any HTTP request");
        foreach (var mode in new[] { "ok", "network", "forbidden", "rate", "schema", "login", "unverified" })
        {
            var store = Store("xt-" + mode); var vault = new MobileVault(store, protector); var handler = new XtHandler(mode);
            vault.Save(Session("xuetang"));
            var result = await new MobileSync(store, vault, _ => handler).RunAsync("xuetang", true);
            var report = result.Connection ?? throw new InvalidDataException("Connection report missing.");
            var feedback = ConnectionFeedback.From(report);
            Check(result.AccountVerified == report.AccountVerified && result.CatalogCovered == report.CatalogCovered && result.LoginRequired == report.LoginRequired,
                "mobile result and persistent connection agree: " + mode);
            Check(!report.TaskListsCovered && report.SelectedCourses == 0 && handler.TaskRequests == 0 && store.LoadTasks().Count == 0,
                "a login check never imports tasks or invents selected courses: " + mode);
            Check(new TaskStore(store.DatabasePath, protector).LoadConnectionReport("xuetang")?.CheckedAtUtc == report.CheckedAtUtc,
                "last check survives app reopening: " + mode);
            if (mode == "ok")
                Check(report is { AccountVerified: true, CatalogCovered: true, Courses: 0 } && feedback.Title.Contains("官网当前没有课程") && !feedback.Warning,
                    "verified empty official directory is explicitly explained");
            else if (mode == "login")
                Check(report is { AccountVerified: false, LoginRequired: true } && feedback.Title.Contains("官网仍要求登录") && feedback.Explanation.Contains("无法判断"),
                    "official rejection does not declare a scan failure or definite expiry");
            else if (mode == "unverified")
                Check(!report.AccountVerified && feedback.Title == "接入尚未确认" && store.LoadPlatformSessionInfo("xuetang").State != "ready",
                    "cookie evidence alone cannot be shown as confirmed login");
            else
            {
                Check(report is { AccountVerified: true, CatalogCovered: false } && feedback.Title == "登录已确认，课程读取未完成"
                    && feedback.Explanation.Contains("不能视为没有课程"), "directory failure keeps successful login distinct: " + mode);
                var expected = mode switch { "network" => "连接官网失败", "forbidden" => "访问权限", "rate" => "请求频率", _ => "尚未识别" };
                Check(feedback.Explanation.Contains(expected) && feedback.NextStep.Length > 0, "real failure category has a useful explanation and next step: " + mode);
            }
        }

        foreach (var withEvidence in new[] { false, true })
        {
            var store = Store("mooc-" + withEvidence); var vault = new MobileVault(store, protector); vault.Save(Session("mooc", "900001"));
            var result = await new MobileSync(store, vault, _ => new OfflineMoocHandler()).RunAsync("mooc", true,
                verifiedBrowserAccount: withEvidence ? "900001" : null);
            Check(result.AccountVerified == withEvidence && result.Connection?.AccountVerified == withEvidence,
                "saved MOOC account requires current website evidence when catalog is unreachable: " + withEvidence);
            Check(ConnectionFeedback.From(result.Connection!).Title == (withEvidence ? "登录已确认，课程读取未完成" : "接入尚未确认"),
                "MOOC explanation reflects the check actually performed: " + withEvidence);
        }
        var resetStore = Store("reset"); var resetVault = new MobileVault(resetStore, protector); resetVault.Save(Session("xuetang"));
        var successful = await new MobileSync(resetStore, resetVault, _ => new XtHandler("ok")).RunAsync("xuetang", true);
        resetStore.ClearPlatformSession("xuetang");
        Check(resetStore.LoadConnectionReport("xuetang") is null && resetVault.Load("xuetang") is null, "clearing authorization removes the old success result");
        var safeReport = JsonSerializer.Serialize(successful.Connection);
        Check(!safeReport.Contains("synthetic-login") && !safeReport.Contains("91001") && !safeReport.Contains("https:"),
            "connection results contain no account ID, cookie value or URL");
        return new("passed", results.Count, results);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class XtHandler(string mode) : HttpMessageHandler
    {
        public int TaskRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get || request.RequestUri.Host != XuetangPlatform.Host || request.Content is not null)
                throw new InvalidDataException("Unexpected connection HTTP request.");
            if (mode == "unverified") throw new HttpRequestException("Synthetic website unavailable before identity verification.");
            if (path == XuetangPlatform.LoginCheck) return Task.FromResult(Json("{\"success\":true,\"data\":{\"is_login\":" + (mode == "login" ? "false" : "true") + "}}"));
            if (path == XuetangPlatform.Profile) return Task.FromResult(Json("{\"success\":true,\"data\":{\"id\":91001}}"));
            if (path != XuetangPlatform.Courses) { TaskRequests++; throw new InvalidDataException("Login cannot read task endpoints."); }
            if (mode == "network") throw new HttpRequestException("Synthetic directory unavailable after verification.");
            return Task.FromResult(mode switch
            {
                "forbidden" => Json("{}", HttpStatusCode.Forbidden),
                "rate" => Json("{}", HttpStatusCode.TooManyRequests),
                "schema" => Json("{\"success\":true,\"data\":{\"unexpected\":[]}}"),
                _ => Json("{\"success\":true,\"data\":{\"product_list\":[],\"count\":0}}")
            });
        }
    }
    private sealed class OfflineMoocHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.RequestUri?.Host != MoocPlatform.Host || request.RequestUri.AbsolutePath != MoocPlatform.CourseRpc)
                throw new InvalidDataException("Unexpected MOOC connection request.");
            throw new HttpRequestException("Synthetic MOOC course service unavailable.");
        }
    }
}
