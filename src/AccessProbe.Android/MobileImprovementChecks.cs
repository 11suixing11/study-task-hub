using AccessProbe;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AccessProbe.Mobile;

public sealed record MobileImprovementResult(string Outcome, int Checks, IReadOnlyList<string> Results,
    bool OfficialNetworkAccessed = false, bool ProductionDataAccessed = false);

// Only synthetic responses and databases under the explicitly supplied test directory.
public static class MobileImprovementChecks
{
    private const string Account = "synthetic-uai-account";
    private const string ProfileJson = """{"code":1,"value":{"userInfo":{"ssoId":"synthetic-uai-account","school":1001,"roleType":0}}}""";
    private const string ModulesJson = """{"code":1,"data":[{"module_type":"UCAMPUS","authorization_status":"enabled"}]}""";
    private const string ShelfJson = """{"code":1,"value":{"groups":{"CLASS_COURSE":[{"resourceId":"1001","name":"同名资源"}],"PERSONAL":[],"EXPIRED":[]}}}""";
    private const string HomeworkJson = """{"code":1,"data":{"homework":{"total":3,"items":[{"platform":"UAI","assignmentId":"2001","title":"合成作业","classes":[{"className":"同名课程"}]}]}}}""";
    private const string Entry = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=201&cpi=301&workId=401&answerId=501&enc=SYNTHETIC%2BONLY";

    public static async Task<MobileImprovementResult> RunAsync(string directory, ILocalLinkProtector protector)
    {
        Directory.CreateDirectory(directory);
        var results = new List<string>(); var now = DateTimeOffset.UtcNow;
        void Check(bool value, string label)
        { if (!value) throw new InvalidDataException("Improvement check failed: " + label); results.Add(label); }
        TaskStore Store(string name) => new(Path.Combine(directory, name + ".db"), protector);
        async Task Cancelled(Func<Task> action, string label)
        {
            try { await action(); throw new InvalidDataException("Cancellation was ignored: " + label); }
            catch (OperationCanceledException) { Check(true, label); }
        }
        async Task Rejected(Func<Task> action, string code, string label)
        {
            try { await action(); throw new InvalidDataException("Rejection was ignored: " + label); }
            catch (TaskValidationException error) { Check(error.Code == code, label); }
        }

        var manualStore = Store("manual");
        var official = CourseRecordingRules.Metadata("pta", "900001", new("910001", "同名课程", "class", "官方班课"));
        var recording = manualStore.ObserveRecordingCourses("pta", [official], true, now);
        recording = manualStore.SaveCourseRecording("pta", false, [official.Course.Key], true, recording.Revision, now);
        var manual = manualStore.Create(new("手动新增", "pta", "同名课程", DeadlineKind.Known, null, now.AddHours(4), null, null, "", "", null), now);
        var selected = manualStore.LoadCourseRecordingOverview("pta"); var manualKey = CourseRecordingRules.TaskCourseKey(manual);
        Check(manualKey != official.Course.Key && manual.RecordingCourseKey.Length == 0, "manual: same name does not bind to official class");
        Check(selected.SelectedCourseKeys.Contains(manualKey) && selected.Revision == recording.Revision + 1, "manual: adding task selects its own local course atomically");
        Check(manualStore.LoadRecordedTasks().Any(task => task.Id == manual.Id) && selected.Courses.Any(course => course.Key == manualKey), "manual: newly added task and local course are visible");
        var manualDue = manualStore.TakeDue(now.AddSeconds(1));
        Check(manualDue.Any(item => item.Task.Id == manual.Id), "manual: newly added deadline has a reminder");
        manualStore.ReleaseReminderClaims(manualDue.Select(item => item.Plan.Id).ToArray());
        manualStore.SaveCourseRecording("pta", false, [official.Course.Key], true, selected.Revision, now);
        Check(manualStore.Get(manual.Id) is not null && manualStore.LoadRecordedTasks().All(task => task.Id != manual.Id), "manual: deselection hides without deleting");
        Check(manualStore.TakeDue(now.AddSeconds(2)).All(item => item.Task.Id != manual.Id), "manual: deselection stops reminders");

        var attentionStore = Store("attention");
        var catalog = attentionStore.ObserveRecordingCourses("pta", [official], true, now);
        attentionStore.SaveCourseRecording("pta", false, [official.Course.Key], true, catalog.Revision, now);
        PlatformCollectedTask Row(TaskState? state, string evidence = "explicit") => new("pta", "900001", "910001", "class", "920001", "problem-set",
            "官网作业", "同名课程", state, "合成状态", evidence, "只读合成元数据", null, null, "", "https://pintia.cn/problem-sets/920001/exam/overview") { MetadataVersion = 2 };
        attentionStore.ImportMetadataTasks("pta", [Row(TaskState.Completed)], now);
        var task = attentionStore.LoadTasks().Single();
        task = attentionStore.SetState(task.Id, task.Revision, TaskState.Completed, now);
        attentionStore.ImportMetadataTasks("pta", [Row(TaskState.Returned)], now.AddSeconds(1)); task = attentionStore.Get(task.Id)!;
        var attentionToken = task.PlatformAttentionToken;
        Check(task.State == TaskState.Completed && TaskRules.HasPlatformAttention(task) && TaskRules.IsActive(task), "attention: returned official work keeps manual completion and becomes active");
        Check(TaskRules.StateName(task).Contains("官网要求重做", StringComparison.Ordinal), "attention: official change is visible separately");
        var due = attentionStore.TakeDue(now.AddSeconds(2)); var changed = due.Single(item => item.Plan.Kind == ReminderKind.PlatformChanged);
        Check(changed.Task.Id == task.Id, "attention: official change schedules its own notification");
        attentionStore.RecordNotificationRequest([changed.Plan.Id], true, now.AddSeconds(2));
        attentionStore.ImportMetadataTasks("pta", [Row(TaskState.Pending)], now.AddSeconds(3)); task = attentionStore.Get(task.Id)!;
        Check(task.PlatformAttentionToken == attentionToken && attentionStore.TakeDue(now.AddSeconds(4)).Count == 0, "attention: repeated pending/returned sync does not repeat a delivered notification");
        attentionStore.ImportMetadataTasks("pta", [Row(null)], now.AddSeconds(5)); task = attentionStore.Get(task.Id)!;
        Check(TaskRules.HasPlatformAttention(task) && task.PlatformObservedState == TaskState.Pending, "attention: unknown official state retains previous requirement");
        attentionStore.ImportMetadataTasks("pta", [Row(TaskState.Pending, "recent_reminder")], now.AddSeconds(6)); task = attentionStore.Get(task.Id)!;
        Check(task.PlatformAttentionToken == attentionToken, "attention: weak pending evidence cannot replace official requirement");
        task = attentionStore.AcknowledgePlatformAttention(task.Id, task.Revision, now.AddSeconds(7));
        attentionStore.ImportMetadataTasks("pta", [Row(TaskState.Returned)], now.AddSeconds(8)); task = attentionStore.Get(task.Id)!;
        Check(!TaskRules.HasPlatformAttention(task) && task.PlatformAttentionAcknowledgedToken == attentionToken, "attention: acknowledgement persists through same official requirement");
        var reopened = new TaskStore(attentionStore.DatabasePath, protector).Get(task.Id)!;
        Check(reopened.PlatformAttentionAcknowledgedToken == attentionToken && reopened.State == TaskState.Completed, "attention: official evidence and acknowledgement survive reopen");
        attentionStore.ImportMetadataTasks("pta", [Row(TaskState.Completed)], now.AddSeconds(9));
        attentionStore.ImportMetadataTasks("pta", [Row(TaskState.Pending)], now.AddSeconds(10)); task = attentionStore.Get(task.Id)!;
        Check(TaskRules.HasPlatformAttention(task) && task.PlatformAttentionToken != attentionToken, "attention: a new official reopening creates a new event");
        var freshDue = attentionStore.TakeDue(now.AddSeconds(11));
        Check(freshDue.Count(item => item.Plan.Kind == ReminderKind.PlatformChanged) == 1, "attention: a distinct reopening notifies once");
        attentionStore.ReleaseReminderClaims(freshDue.Select(item => item.Plan.Id).ToArray());
        attentionStore.AcknowledgePlatformAttention(task.Id, task.Revision, now.AddSeconds(12));
        Check(attentionStore.TakeDue(now.AddSeconds(13)).Count == 0, "attention: acknowledgement cancels an unsent notification");
        var weak = new TaskItem { Source = TaskSource.Pta, State = TaskState.Completed, Overrides = TaskOverrides.State,
            PlatformObservedState = TaskState.Completed, PlatformStateEvidence = "recent_reminder" };
        Check(!TaskRules.HasPlatformAttention(TaskRules.ObservePlatformState(weak, weak, TaskState.Pending, now)), "attention: a recent overview cannot reopen a completed task");
        var ignored = weak with { State = TaskState.Ignored, PlatformStateEvidence = "explicit" };
        Check(TaskRules.HasPlatformAttention(TaskRules.ObservePlatformState(ignored, ignored, TaskState.Returned, now)), "attention: ignored task still exposes explicit official return");

        var appCalls = 0; var webCalls = 0;
        string? AppResult(MobileOfficialApp app, string address) { appCalls++; return "task"; }
        bool Browser(string address) { webCalls++; return true; }
        Check(MobileTaskRouting.Open("chaoxing", Entry, "课程", true, AppResult, Browser).Outcome == "app_task" && webCalls == 0, "routing: app handles task before browser");
        var home = MobileTaskRouting.Open("chaoxing", Entry, "合成课程", true, (_, _) => "home", Browser);
        Check(home.Outcome == "app_home" && home.Message.Contains("合成课程", StringComparison.Ordinal), "routing: app homepage gives course lookup guidance");
        Check(MobileTaskRouting.Open("chaoxing", Entry, "", true, (_, _) => null, Browser).Outcome == "web", "routing: unavailable app falls back to web");
        Check(MobileTaskRouting.Open("chaoxing", Entry, "", true, (_, _) => null, _ => false).Outcome == "unavailable", "routing: browser failure keeps course fallback actionable");
        var previousApps = appCalls;
        Check(MobileTaskRouting.Open("chaoxing", Entry, "", false, AppResult, Browser).Outcome == "web" && appCalls == previousApps, "routing: explicit web bypasses app");
        Check(MobileTaskRouting.Open("mooc", Entry, "", true, AppResult, Browser).Outcome == "web" && appCalls == previousApps, "routing: another platform URL never enters native app");
        Check(MobileTaskRouting.App("uai")?.Package == "cn.unipus.cloud" && MobileTaskRouting.App("mooc")?.Package == "com.netease.edu.ucmooc", "routing: confirmed UAI and MOOC package IDs are distinct");
        Check(new[] { "yuketang", "changjiang", "xuetang", "pta" }.All(platform => MobileTaskRouting.App(platform) is null), "routing: unconfirmed task apps are not guessed");
        Check(new[] { "chaoxing", "yuketang", "changjiang", "mooc", "xuetang", "ucampus", "zhihuishu", "pta", "uai" }
            .All(platform => MobileTaskRouting.IsOfficialWeb(platform, MobileTaskRouting.Home(platform))), "routing: each supported platform has official web fallback");
        Check(!MobileTaskRouting.IsOfficialWeb("uai", "https://uai.unipus.cn.example.invalid/") && !MobileTaskRouting.IsOfficialWeb("uai", "http://uai.unipus.cn/"), "routing: spoofed and insecure hosts are rejected");
        Check(LoginNavigationMessage.ForBlocked("chaoxing://synthetic").Contains("继续应用内官网确认", StringComparison.Ordinal), "login: blocked external callback keeps an in-app recovery action");

        var navigationStore = Store("navigation");
        var cx = CourseRecordingRules.Chaoxing(new("合成课程", "101", "201", "301", new Uri(Entry)));
        var cxCatalog = navigationStore.ObserveRecordingCourses("chaoxing", [cx], true, now);
        navigationStore.SaveCourseRecording("chaoxing", false, [cx.Course.Key], true, cxCatalog.Revision, now);
        void Import(string entry) => navigationStore.ImportWorks([new("合成跳转任务", "未交", "剩余50小时", WorkListParser.ParseTaskLocation(entry))], "合成课程", now);
        Import(Entry); var navigationTask = navigationStore.LoadTasks().Single(); var opened = new List<string>(); var resolves = 0;
        var latestEntry = Entry.Replace("enc=SYNTHETIC%2BONLY", "enc=SYNTHETIC-LATEST", StringComparison.Ordinal);
        var navigation = new TaskNavigationService(navigationStore, (uri, _) =>
        { resolves++; if (resolves == 1) Import(latestEntry); return Task.FromResult(new TaskLinkResolution("ready", "entry", uri.OriginalString)); }, opened.Add);
        await navigation.OpenAsync(navigationTask.Id, default);
        Check(resolves == 2 && opened.Single() == latestEntry, "navigation: asynchronous refresh opens the latest task entry");
        var wrong = new TaskNavigationService(navigationStore, (_, _) => Task.FromResult(new TaskLinkResolution("ready", "answer", latestEntry.Replace("workId=401", "workId=999", StringComparison.Ordinal))), opened.Add);
        await Rejected(() => wrong.OpenAsync(navigationTask.Id, default), "LINK003", "navigation: cross-task redirect is rejected");
        using (var cancel = new CancellationTokenSource())
        {
            var stopping = new TaskNavigationService(navigationStore, (uri, _) => { cancel.Cancel(); return Task.FromResult(new TaskLinkResolution("ready", "entry", uri.OriginalString)); }, opened.Add);
            await Cancelled(() => stopping.OpenAsync(navigationTask.Id, cancel.Token), "navigation: cancellation during resolution prevents launch");
        }
        var deletion = new TaskNavigationService(navigationStore, (uri, _) =>
        { var latest = navigationStore.Get(navigationTask.Id)!; navigationStore.Delete(latest.Id, latest.Revision); return Task.FromResult(new TaskLinkResolution("ready", "entry", uri.OriginalString)); }, opened.Add);
        await Rejected(() => deletion.OpenAsync(navigationTask.Id, default), "TASK004", "navigation: deleted task cannot launch after resolution");
        Check(opened.Count == 1, "navigation: failed resolutions do not launch destinations");

        var estimated = new TaskItem { Source = TaskSource.Chaoxing, Title = "合成估计日期", CourseName = "课程", DeadlineKind = DeadlineKind.Known,
            DeadlineUtc = now.AddHours(2), DeadlineEvidence = DeadlineEvidence.RemainingTime };
        Check(ReminderPlanner.TimedPlans(estimated, now).Count == 0 && TaskRules.DeadlineName(estimated).StartsWith("约 ", StringComparison.Ordinal), "deadline: estimated date is visible without timed reminders");
        var draft = new TaskDraft(estimated.Title, estimated.PlatformId, estimated.CourseName, estimated.DeadlineKind, null, estimated.DeadlineUtc, null, null, "", "", null) { ConfirmDeadline = true };
        var confirmed = TaskRules.ApplyDraft(estimated, draft, now);
        Check(TaskRules.HasTrustedDeadline(confirmed) && ReminderPlanner.TimedPlans(confirmed, now).Count == 2, "deadline: confirming unchanged estimate enables deadline reminders");
        Check(TaskRules.ParseEditedLocalTime(now.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), now) == now, "deadline: unchanged local input preserves original instant");

        var token = Token(Account, now.AddHours(1).ToUnixTimeSeconds());
        var uaiStore = Store("uai"); var vault = new MobileVault(uaiStore, protector);
        var uai = new MobileSession(Guid.NewGuid().ToString("N"), "uai", now, [], Account, token, MobileSessionRules.CookieFingerprint("uai", []));
        Check(MobileSessionRules.IsValid(uai, "uai", now) && !MobileSessionRules.IsValid(uai, "ucampus", now), "uai: independent JWT session cannot be reused by legacy Ucampus");
        Check(!MobileSessionRules.IsValid(uai with { AccountId = "different-account" }, "uai", now), "uai: token must match saved account");
        var foreignJwt = new MetadataSessionCookie("jwt", Token("other-account", null), ".unipus.cn", "/", true, true, null);
        Check(!MobileSessionRules.IsValid(uai with { Cookies = [foreignJwt], TokenBinding = MobileSessionRules.CookieFingerprint("uai", [foreignJwt]) }, "uai", now), "uai: conflicting cookie identity is rejected");
        Check(!UaiPortal.CookieValid(foreignJwt with { Domain = "evil.unipus.cn" }) && !UaiPortal.CookieValid(foreignJwt with { Secure = false })
            && !UaiPortal.CookieValid(foreignJwt with { Domain = null! }), "uai: cookie scope and malformed archive are rejected");
        Check(UaiPortal.TokenIdentity("invalid") is null && UaiPortal.TokenIdentity(Token(Account, null))?.OpenId == Account, "uai: malformed JWT rejected; identity decoding alone is not verification");
        Check(UaiPortal.Profile(ProfileJson, Account, 1001).Outcome == "ok" && UaiPortal.Profile(ProfileJson, "other-account", 1001).Outcome == "account_mismatch"
            && UaiPortal.Profile(ProfileJson, Account, 1002).Outcome == "account_mismatch", "uai: official profile binds account and school");
        Check(UaiPortal.Profile(ProfileJson.Replace("\"roleType\":0", "\"roleType\":1", StringComparison.Ordinal), Account, null).Outcome == "student_account_required", "uai: teacher profile is not student task access");
        foreach (var json in new[] { "", "null", "[]", "{}", "{\"code\":1}", "{\"code\":1,\"value\":[]}", "{\"code\":1,\"data\":null}", "{\"code\":1,\"value\":{\"groups\":{}}}" })
            Check(UaiPortal.Profile(json, Account, null).Outcome != "ok" && UaiPortal.Shelf(json).Resources is null && UaiPortal.Homework(json).Total is null && UaiPortal.Modules(json).Allowed is null,
                "uai: unknown structure remains unconfirmed " + results.Count);
        Check(UaiPortal.Shelf(ShelfJson) == (1, "preview") && UaiPortal.Modules(ModulesJson) == (true, "ok"), "uai: official shelf and module overview contracts parse");
        Check(UaiPortal.Homework(HomeworkJson) == (3, 1, "overview_truncated"), "uai: truncated homepage is not full task coverage");
        Check(UaiPortal.Homework(HomeworkJson.Replace("\"total\":3", "\"total\":1", StringComparison.Ordinal)).Outcome == "preview", "uai: equal homepage counts still mean preview only");
        Check(UaiPortal.Homework(HomeworkJson.Replace("\"total\":3", "\"total\":0", StringComparison.Ordinal)).Outcome == "unknown_structure", "uai: inconsistent totals are not zero tasks");

        vault.Save(uai); var handlers = new List<PortalHandler>();
        var sync = new MobileSync(uaiStore, vault, platform => { if (platform != "uai") throw new InvalidDataException("Unexpected platform."); var handler = new PortalHandler(); handlers.Add(handler); return handler; });
        var retained = uaiStore.Create(new("AI版人工记录", "uai", "同名课程", DeadlineKind.Unknown, null, null, null, null, "", "", null), now);
        var preview = await sync.RunAsync("uai", false);
        Check(preview.AccountVerified && !preview.CatalogCovered && !preview.FullTaskListsCovered && preview.Tasks == 0 && preview.Codes.Contains("UAI004") && preview.Codes.Contains("UAI005"), "uai: verified account exposes preview limits explicitly");
        Check(handlers.Single().Paths.SequenceEqual(new[] { UaiPortal.ProfilePath, UaiPortal.ModulesPath, UaiPortal.ShelfPath, UaiPortal.HomeworkPath }) && handlers.Single().ReadOnlyRequests, "uai: only four confirmed GET overview paths are requested");
        Check(vault.Load("uai")?.SchoolId == 1001 && handlers.Single().SchoolHeaders.Skip(1).All(school => school == "1001"), "uai: verified school binds subsequent overview requests");
        Check(uaiStore.LoadTasks().Single().Id == retained.Id && uaiStore.LoadCourseRecording("uai").Courses.Count == 0, "uai: class names and bookshelves never create inferred tasks or courses");
        var summary = new TaskStore(uaiStore.DatabasePath, protector).LoadUaiPortalSummary()!;
        Check(summary.OverviewTasks == 3 && summary.DisplayedTasks == 1 && !summary.FullTaskListsCovered && MobilePlatformStatus.Read(uaiStore, "uai").Outcome == "partial", "uai: preview coverage persists across reopen");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(uaiStore.DatabasePath)).Contains(token, StringComparison.Ordinal) && !JsonSerializer.Serialize(summary).Contains(Account, StringComparison.Ordinal), "uai: preview and database do not expose plaintext credentials");
        var catalogHandler = new PortalHandler();
        await new MobileSync(uaiStore, vault, _ => catalogHandler).RunAsync("uai", true);
        Check(!catalogHandler.Paths.Contains(UaiPortal.HomeworkPath), "uai: initial login validation skips homework overview");
        var limited = new PortalHandler(HttpStatusCode.TooManyRequests);
        using (var reader = new UaiPortalReader(_ => Task.FromResult((token, Account, (long?)1001)), limited))
        {
            Check((await reader.ReadAsync("/api/answer/submit", default)).Outcome == "unsupported_request" && limited.Paths.Count == 0, "uai: unknown and submission endpoints cannot be requested");
            Check((await reader.ReadAsync(UaiPortal.ProfilePath, default)).Outcome == "rate_limited"
                && (await reader.ReadAsync(UaiPortal.HomeworkPath, default)).Outcome == "rate_limited" && limited.Paths.Count == 1, "uai: 429 stops further requests without retry");
        }
        var expiredHandler = new PortalHandler();
        using (var reader = new UaiPortalReader(_ => Task.FromResult((Token(Account, now.AddMinutes(-1).ToUnixTimeSeconds()), Account, (long?)1001)), expiredHandler))
            Check((await reader.ReadAsync(UaiPortal.ProfilePath, default)).Outcome == "login_required" && expiredHandler.Paths.Count == 0, "uai: expired JWT requires internal login before network request");
        foreach (var (status, outcome) in new[] { (HttpStatusCode.Forbidden, "official_verification_required"), (HttpStatusCode.Found, "official_redirect_required"), (HttpStatusCode.ServiceUnavailable, "temporarily_unavailable") })
        {
            var handler = new PortalHandler(status);
            using var reader = new UaiPortalReader(_ => Task.FromResult((token, Account, (long?)1001)), handler);
            Check((await reader.ReadAsync(UaiPortal.ProfilePath, default)).Outcome == outcome && handler.Paths.Count == 1, "uai: official failure handled without bypass " + (int)status);
        }
        var changedSession = false;
        using (var reader = new UaiPortalReader(_ => Task.FromResult((changedSession ? Token("other-account", null) : token, changedSession ? "other-account" : Account, (long?)1001)),
            new PortalHandler(onRequest: () => changedSession = true)))
            await Cancelled(() => reader.ReadAsync(UaiPortal.ProfilePath, default), "uai: account change during response rejects stale result");
        var oversized = new PortalHandler(content: new string('x', 2 * 1024 * 1024 + 1));
        using (var reader = new UaiPortalReader(_ => Task.FromResult((token, Account, (long?)1001)), oversized))
            Check((await reader.ReadAsync(UaiPortal.ProfilePath, default)).Outcome == "limit_reached", "uai: oversized response remains incomplete");
        var unknown = await new MobileSync(uaiStore, vault, _ => new PortalHandler(content: "{\"code\":1}")).RunAsync("uai", false);
        Check(!unknown.AccountVerified && !unknown.FullTaskListsCovered && uaiStore.LoadTasks().Count == 1 && uaiStore.LoadUaiPortalSummary()?.OverviewTasks is null, "uai: unknown response preserves manual task without claiming zero overview");
        var expired = await new MobileSync(uaiStore, vault, _ => new PortalHandler(HttpStatusCode.Unauthorized)).RunAsync("uai", false);
        Check(expired.LoginRequired && vault.Load("uai") is null && vault.LoadForRevalidation("uai") is not null && uaiStore.Get(retained.Id) is not null, "uai: invalidated login retains only internal revalidation archive and task");
        return new("passed", results.Count, results);
    }

    private static string Token(string account, long? expiry)
    {
        static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new Dictionary<string, object> { ["openId"] = account };
        if (expiry is { } value) payload["exp"] = value;
        return Base64("{\"alg\":\"SYNTHETIC\"}") + "." + Base64(JsonSerializer.Serialize(payload)) + ".synthetic";
    }

    private sealed class PortalHandler(HttpStatusCode status = HttpStatusCode.OK, string? content = null, Action? onRequest = null) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string?> SchoolHeaders { get; } = [];
        public bool ReadOnlyRequests { get; private set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            SchoolHeaders.Add(request.Headers.TryGetValues("u-school", out var schools) ? schools.Single() : null);
            ReadOnlyRequests &= request.Method == HttpMethod.Get && request.RequestUri.IdnHost == UaiPortal.Host && request.RequestUri.Scheme == "https"
                && request.Content is null && request.Headers.TryGetValues("Authorization", out var tokens) && UaiPortal.TokenIdentity(tokens.Single())?.OpenId == Account;
            onRequest?.Invoke();
            var body = content ?? path switch { UaiPortal.ProfilePath => ProfileJson, UaiPortal.ModulesPath => ModulesJson, UaiPortal.ShelfPath => ShelfJson, UaiPortal.HomeworkPath => HomeworkJson, _ => "{}" };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
