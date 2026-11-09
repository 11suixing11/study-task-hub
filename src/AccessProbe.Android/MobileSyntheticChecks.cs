using AccessProbe;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe.Mobile;

public sealed record MobileCheckResult(string Outcome, int Checks, int Tasks, bool RestartChecked,
    bool OfficialNetworkAccessed = false, bool ProductionDataAccessed = false);

public static class MobileSyntheticChecks
{
    private const string Account = "900001";
    private const string Secret = "SYNTHETIC-ONLY-SESSION";
    public static async Task<MobileCheckResult> RunAsync(string directory, ILocalLinkProtector protector, bool restart = false)
    {
        Directory.CreateDirectory(directory);
        var store = new TaskStore(Path.Combine(directory, "synthetic-tasks.db"), protector);
        var vault = new MobileVault(store, protector);
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidDataException("Synthetic check failed: " + label); checks++; }
        if (restart)
        {
            Check(store.LoadTasks().Count == 2, "tasks survive process restart");
            Check(store.LoadTasks().Any(task => task.Source == TaskSource.Pta && task.Title == "手机手动标题" && task.State == TaskState.Ignored), "manual changes survive restart");
            Check(vault.Load("pta")?.Cookies.Any(cookie => cookie.Value == Secret) == true, "Keystore session can be read after process restart");
            Check(store.LoadCourseRecording("pta").SelectionConfirmed, "course selection survives restart");
            return new("passed", checks, store.LoadTasks().Count, true);
        }

        var encrypted = protector.Protect(Secret);
        Check(protector.Unprotect(encrypted) == Secret, "encryption round trip");
        Check(!Encoding.UTF8.GetString(encrypted).Contains(Secret, StringComparison.Ordinal), "ciphertext contains no cookie pairs");
        var tampered = encrypted.ToArray(); tampered[^1] ^= 1;
        try { protector.Unprotect(tampered); throw new InvalidDataException("Tampering was accepted."); }
        catch (CryptographicException) { checks++; }

        var manual = store.LoadTasks().FirstOrDefault(task => task.Source == TaskSource.Manual)
            ?? store.Create(new("手机合成手动任务", "pta", "合成课程", DeadlineKind.Known, null, DateTimeOffset.UtcNow.AddHours(12),
                "https://pintia.cn/problem-sets/990001", null, "", "合成备注", null), DateTimeOffset.UtcNow);
        Check(store.Get(manual.Id)?.Link == manual.Link, "native SQLite and encrypted link round trip");
        Check(store.LoadReminderHistory().Any(item => item.Plan.TaskId == manual.Id), "manual deadline produces reminder plans");
        var cookie = new MetadataSessionCookie("PTASession", Secret, ".pintia.cn", "/", true, true, null, "Strict");
        var session = new MobileSession(Guid.NewGuid().ToString("N"), "pta", DateTimeOffset.UtcNow, [cookie]); vault.Save(session);
        Check(vault.Load("pta")?.Id == session.Id, "vault stores session in SQLite");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.DatabasePath)).Contains(Secret, StringComparison.Ordinal), "database contains no plaintext session");
        Check(!MobileSessionRules.CookieValid("pta", cookie with { Domain = "example.com" }), "cross-platform Cookie rejected");
        Check(!MobileSessionRules.IsValid(session with { Cookies = null! }, "pta", DateTimeOffset.UtcNow), "invalid stored session rejected");
        Check(!MobileSessionRules.IsValid(session with { SavedAtUtc = DateTimeOffset.UtcNow.AddDays(1) }, "pta", DateTimeOffset.UtcNow), "future archive rejected");
        Check(cookie.AppliesTo(new("https://passport.pintia.cn/api/u/current")) && !cookie.AppliesTo(new("https://pintia.cn.example.com/")), "cookie domain boundary retained");
        Check(!cookie.AppliesTo(new("http://pintia.cn/")), "secure cookies cannot be sent over HTTP");

        var handlers = new List<SyntheticPtaHandler>();
        var sync = new MobileSync(store, vault, platform =>
        {
            if (platform != "pta") throw new InvalidDataException("Synthetic route is not configured.");
            var handler = new SyntheticPtaHandler(); handlers.Add(handler); return handler;
        });
        var catalog = await sync.RunAsync("pta", false);
        Check(catalog.AccountVerified && catalog.CatalogCovered && !catalog.LoginRequired, "official metadata account and courses verified");
        Check(store.LoadTasks().Count == 1 && catalog.Tasks == 0, "unconfirmed selection does not collect tasks");
        Check(handlers[^1].Paths.All(path => !path.EndsWith("/exams", StringComparison.Ordinal)), "catalog-only mode never reads exam states");
        var recording = store.LoadCourseRecording("pta");
        Check(recording.Courses.Count == 2 && recording.Courses.All(course => !CourseRecordingRules.IsSelected(recording, course.Key)), "all courses shown and unchecked by default");
        var courseKey = CourseRecordingRules.MetadataKey("pta", Account, "910001", "class");
        store.SaveCourseRecording("pta", false, [courseKey], true, recording.Revision, DateTimeOffset.UtcNow);
        var collected = await sync.RunAsync("pta", false);
        Check(collected.Tasks == 1 && store.LoadTasks().Count == 2, "selected course collected into unified tasks");
        Check(handlers[^1].Paths.Contains("/api/problem-sets/user-groups/910001") && !handlers[^1].Paths.Contains("/api/problem-sets/user-groups/910002"), "only chosen course is collected");
        Check(store.LoadMetadataSettings("pta").LastSummary?.Tasks == 1, "sync summary persisted");
        var task = store.LoadTasks().Single(task => task.Source == TaskSource.Pta);
        Check(task.State == TaskState.Pending && task.DeadlineUtc is not null && task.Link == "https://pintia.cn/problem-sets/920001/exam/overview", "state deadline and official navigation projected");
        task = store.SetState(task.Id, task.Revision, TaskState.Ignored, DateTimeOffset.UtcNow);
        task = store.Edit(task.Id, task.Revision, new("手机手动标题", "pta", task.CourseName, DeadlineKind.Known, task.BeginUtc,
            DateTimeOffset.UtcNow.AddDays(7), task.Link, null, "", "手机手动备注", null), DateTimeOffset.UtcNow);
        await sync.RunAsync("pta", false);
        var updated = store.Get(task.Id)!;
        Check(updated.Title == "手机手动标题" && updated.State == TaskState.Ignored && updated.Notes == "手机手动备注", "automatic refresh preserves user edits");
        Check(updated.DeadlineUtc?.ToUnixTimeMilliseconds() == task.DeadlineUtc?.ToUnixTimeMilliseconds(), "manual deadline remains authoritative");

        var expected = vault.Load("pta")!;
        var renewed = vault.Renew(expected, expected.Cookies, [cookie with { Value = "SYNTHETIC-ONLY-RENEWED" }], default);
        Check(vault.Load("pta")?.Cookies.Single(cookie => cookie.Name == "PTASession").Value == "SYNTHETIC-ONLY-RENEWED", "known response renewal persisted");
        Check(MobileSessionRules.Fingerprint(renewed) != MobileSessionRules.Fingerprint(expected), "authentication renewal updates binding");
        var js = cookie with { Name = "JSESSIONID", Domain = "pintia.cn", Value = "SYNTHETIC-SERVER-1", SameSite = null };
        var serverRenewed = vault.Renew(renewed, renewed.Cookies, [js], default);
        Check(MobileSessionRules.Fingerprint(serverRenewed) == MobileSessionRules.Fingerprint(renewed), "normal PTA server session does not imply account change");
        var alias1 = cookie with { Domain = "pintia.cn" }; var alias2 = cookie with { Domain = "passport.pintia.cn" };
        var inferred = session with { Cookies = [alias1, alias2], InferredScopeKeys = [alias1.Key, alias2.Key] };
        var inferredRenewed = MobileSessionRules.Renew(inferred, [alias2], [cookie with { Value = "SYNTHETIC-NEW" }], DateTimeOffset.UtcNow);
        Check(inferredRenewed.Cookies.Length == 1 && inferredRenewed.InferredScopeKeys?.Length == 0, "exact response scope replaces inferred aliases");
        vault.Save(session);
        var stale = vault.Load("pta")!; vault.Save(stale with { Cookies = [cookie with { Value = "SYNTHETIC-OTHER-LOGIN" }] });
        try { vault.Validate(stale, default); throw new InvalidDataException("Changed authentication was accepted."); }
        catch (OperationCanceledException) { checks++; }
        var unknown = new MobileSync(store, vault, _ => new SyntheticPtaHandler { UnknownCatalog = true });
        vault.Save(session); var beforeUnknown = store.LoadTasks().Count;
        var unknownResult = await unknown.RunAsync("pta", true);
        Check(!unknownResult.CatalogCovered && unknownResult.Codes.Count > 0 && store.LoadTasks().Count == beforeUnknown, "unknown structure remains partial and keeps records");
        var rejecting = new MobileSync(store, vault, _ => new SyntheticPtaHandler { RejectLogin = true });
        var expired = await rejecting.RunAsync("pta", true);
        Check(expired.LoginRequired && vault.Load("pta") is null && store.LoadPlatformSessionInfo("pta").State == "expired", "server expiry invalidates reusable cookie");
        Check(store.LoadTasks().Count == 2, "expiry preserves tasks");
        vault.Save(session);

        var zhsCookie = new MetadataSessionCookie("CASLOGC", Uri.EscapeDataString(JsonSerializer.Serialize(new { uuid = "synthetic-account" })), ".zhihuishu.com", "/", true, true, null);
        var zhs = new MobileSession(Guid.NewGuid().ToString("N"), "zhihuishu", DateTimeOffset.UtcNow, [zhsCookie], "synthetic-account", "SYNTHETIC-TOKEN", MobileSessionRules.CookieFingerprint("zhihuishu", [zhsCookie]));
        Check(MobileSessionRules.IsValid(zhs, "zhihuishu", DateTimeOffset.UtcNow), "token is bound to confirmed official account cookies");
        Check(!MobileSessionRules.IsValid(zhs with { TokenBinding = "invalid" }, "zhihuishu", DateTimeOffset.UtcNow), "stale token binding rejected");
        foreach (var platform in new[] { "zhihuishu", "ucampus" })
        {
            var accountCookie = platform == "zhihuishu" ? zhsCookie : zhsCookie with { Name = "ucamUserId", Value = "synthetic-legacy-account", Domain = ".unipus.cn" };
            var login = platform == "zhihuishu" ? zhs : new MobileSession(Guid.NewGuid().ToString("N"), platform, DateTimeOffset.UtcNow, [accountCookie]);
            vault.Save(login); vault.Finish(login, true, false, default);
            Check(vault.Load(platform) is null && vault.LoadForRevalidation(platform)?.Id == login.Id
                && store.LoadPlatformSessionInfo(platform).State == "expired", "unconfirmed mobile login is retained only for official revalidation: " + platform);
            vault.Finish(login, false, true, default);
            Check(vault.Load(platform)?.Id == login.Id && store.LoadPlatformSessionInfo(platform).State == "ready", "official account recovery confirms retained mobile login: " + platform);
            store.ClearPlatformSession(platform);
            Check(vault.LoadForRevalidation(platform) is null, "explicit mobile reconnect permanently clears retained login: " + platform);
        }
        try { MobileSessionRules.Renew(zhs, zhs.Cookies, [zhsCookie with { Value = Uri.EscapeDataString(JsonSerializer.Serialize(new { uuid = "other-synthetic-account" })) }], DateTimeOffset.UtcNow); throw new InvalidDataException("Changed account was accepted."); }
        catch (OperationCanceledException) { checks++; }
        Check(new TaskStore(store.DatabasePath, protector).LoadTasks().Count == 2, "database can be reopened");
        return new("passed", checks, store.LoadTasks().Count, false);
    }

    private sealed class SyntheticPtaHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public bool RejectLogin { get; init; }
        public bool UnknownCatalog { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var uri = request.RequestUri!; Paths.Add(uri.AbsolutePath);
            if (request.Method != HttpMethod.Get || request.Content is not null || uri.Scheme != "https" || uri.IdnHost is not ("pintia.cn" or "passport.pintia.cn"))
                throw new InvalidDataException("Synthetic handler rejects any non-metadata request.");
            var loggedIn = !RejectLogin && request.Headers.TryGetValues("Cookie", out var pairs) && pairs.Any(pair => pair.Contains("PTASession=" + Secret, StringComparison.Ordinal));
            var set = new { id = "920001", name = "合成教学题目集", timeType = "FIXED_TIME", type = "EXERCISE", feature = "NONE", startAt = "2026-11-01T00:00:00+08:00", endAt = "2026-12-31T23:59:59+08:00" };
            var group = new { id = "910001", name = "合成选中课程", type = "STUDENT_USER" };
            object body = uri.AbsolutePath switch
            {
                "/api/u/current" => loggedIn ? new { user = new { id = Account, roles = new[] { "USER" } }, emailLogin = true } : new { emailLogin = false, omsLogin = false, studentUserLogin = false, phoneLogin = false },
                _ when !loggedIn => new { error = new { code = "USER_NOT_LOGGED_IN" } },
                "/api/user-groups/member-visible" when UnknownCatalog => new { unexpected = true },
                "/api/user-groups/member-visible" => new { userGroups = new[] { group, new { id = "910002", name = "合成未选课程", type = "STUDENT_USER" } }, total = 2 },
                "/api/problem-sets" => new { problemSets = new[] { set }, total = 1, userGroupsByProblemSetId = new Dictionary<string, object> { ["920001"] = new { items = new[] { group } } } },
                "/api/problem-sets/user-groups/910001" => new { problemSets = new[] { set }, total = 1 },
                "/api/problem-sets/920001/exams" => new { problemSet = set, status = "READY" },
                _ => throw new InvalidDataException("Synthetic handler rejects unknown route.")
            };
            var response = new HttpResponseMessage(uri.AbsolutePath == "/api/u/current" || loggedIn ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            if (uri.IdnHost == "pintia.cn") response.Headers.TryAddWithoutValidation("Set-Cookie", "JSESSIONID=SYNTHETIC-SERVER-" + Paths.Count + "; Path=/; Secure; HttpOnly");
            return Task.FromResult(response);
        }
    }
}
