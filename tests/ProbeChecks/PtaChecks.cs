using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessProbe;
using F = PlatformMetadataTestData.PtaFixtures;

internal static class PtaChecks
{
    private static int checks;
    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException("PTA: " + label); checks++; }
    public static async Task RunAsync()
    {
        Catalogs(); StatesAndDates(); await HttpAsync(); await SyncAsync();
        Console.WriteLine($"PASS: {checks} PTA teaching-course, problem-set, session, pagination and navigation checks; synthetic metadata only.");
    }
    private static void Catalogs()
    {
        Check((int)TaskSource.Pta == 8 && (int)TaskSource.Zhihuishu == 7, "source values preserve stored platform history");
        Check(PlatformCatalog.Sites.Any(site => site.Id == "pta") && MetadataPlatform.Enabled.Contains(MetadataPlatform.Pta)
            && CourseRecordingRules.SupportsAutomatic("pta"), "platform registered across task and course catalogs");
        Check(MetadataPlatform.Pta.Home.AbsoluteUri == "https://pintia.cn/course-class"
            && MetadataPlatform.Pta.IsLoginNavigation(MetadataPlatform.Pta.Login.AbsoluteUri), "official student course and login entries");
        foreach (var name in new[] { "_ga", "_ga_ZHCNP8KECW", "_bl_uid", "_gid", "_gat" })
        {
            var tracking = F.Cookie() with { Name = name, HttpOnly = false };
            Check(!MetadataPlatform.Pta.IsAccountCookie(tracking), "analytics do not identify a signed-in PTA account");
            Check(MetadataSessionStore.MissingForRestore(MetadataPlatform.Pta, [F.Cookie(), tracking],
                [tracking with { Value = "synthetic-new-analytics" }]).Any(cookie => cookie.Name == F.Cookie().Name),
                "changed analytics cannot block restoration of reusable PTA login");
        }
        Check(MetadataPlatform.Pta.IsAccountCookie(F.Cookie()), "authentication cookie continues to guard PTA account switches");
        var serverSession = F.Cookie() with { Name = "JSESSIONID", Domain = "pintia.cn", Secure = false };
        Check(!MetadataPlatform.Pta.IsAccountCookie(serverSession) && MetadataSessionStore.IsValid(MetadataPlatform.Pta, serverSession),
            "renewable PTA server cookie is retained without identifying an account switch");
        Check(MetadataSessionStore.MissingForRestore(MetadataPlatform.Pta, [F.Cookie(), serverSession],
            [serverSession with { Value = "synthetic-server-renewed" }]).Single().Name == "PTASession",
            "server-session renewal permits restoring missing authentication cookie without overwriting current server session");
        Check(MetadataSessionStore.MissingForRestore(MetadataPlatform.Pta, [F.Cookie(), serverSession],
            [F.Cookie() with { Value = "synthetic-other-login" }]).Count == 0, "different authentication cookie prevents mixed-account restore");
        foreach (var host in new[] { "pintia.cn", "passport.pintia.cn" }) Check(MetadataPlatform.Pta.IsCookieDomain(host), "official cookie host");
        foreach (var host in new[] { "evil.pintia.cn", "pintia.cn.evil.invalid", "passport.zhihuishu.com", "evilpintia.cn" })
            Check(!MetadataPlatform.Pta.IsCookieDomain(host), "unrelated cookie host rejected");
        foreach (var host in new[] { "turing.captcha.qcloud.com", "ca.turing.captcha.qcloud.com" })
        {
            foreach (var path in new[] { "placeholder_v2.html", "new_slide_placeholder.html", "drag_ele.html", "drag_ele_global.html" })
            {
                var address = "https://" + host + "/template/" + path + "?aid=synthetic-captcha";
                Check(MetadataPlatform.Pta.IsLoginFrameNavigation(address) && !MetadataPlatform.Pta.IsLoginNavigation(address),
                    "official captcha loads inside login frame only");
                Check(!MetadataPlatform.Zhihuishu.IsLoginFrameNavigation(address) && !MetadataPlatform.Ucampus.IsLoginFrameNavigation(address),
                    "PTA verifier policy does not expand other platforms");
            }
            Check(!MetadataSessionStore.IsValid(MetadataPlatform.Pta, F.Cookie() with { Domain = host }), "captcha cookie cannot enter platform archive");
        }
        foreach (var address in new[] { "http://turing.captcha.qcloud.com/template/drag_ele.html", "https://turing.captcha.qcloud.com.evil.invalid/template/drag_ele.html",
            "https://turing.captcha.qcloud.com/template/unknown.html", "https://turing.captcha.qcloud.com/TCaptcha.js",
            "https://turing.captcha.qcloud.com:9443/template/drag_ele.html", "https://turing.captcha.qcloud.com/template/drag_ele.html#unsupported" })
            Check(!MetadataPlatform.Pta.IsLoginFrameNavigation(address), "unconfirmed verifier navigation rejected");
        Check(MetadataPlatformParser.PtaAccount(F.Profile()) == F.Account, "profile establishes account without guessed cookie id");
        Check(MetadataPlatformParser.PtaEnvelope("profile", F.Guest) == "login_required", "HTTP 200 guest response requires login");
        foreach (var json in new[] { "{}", "{\"user\":{\"id\":\"0\"}}", "{\"user\":{\"id\":\"secret-account\"}}", "[]", "null", "invalid" })
            Check(MetadataPlatformParser.PtaAccount(json) is null, "unknown profile cannot establish identity");
        Check(MetadataPlatformParser.PtaEnvelope("pta-exam", "{\"error\":{\"code\":\"PERMISSION_DENIED\"}}") == "course_access_denied", "course permission not account expiry");
        var catalog = MetadataPlatformParser.PtaCatalog(F.Catalog(), 1);
        Check(catalog is { Outcome: "ok", Rows: 2, TotalRows: 2 } && catalog.Courses.Count == 2, "student classes returned");
        Check(MetadataPlatformParser.PtaCatalog(F.Catalog([]), 1) is { Outcome: "ok", Rows: 0, TotalRows: 0 }, "explicit complete empty catalog");
        foreach (var json in new[] { "{}", "{\"userGroups\":[]}", F.Catalog([F.Course], 20), "{\"userGroups\":null,\"total\":0}" })
            Check(MetadataPlatformParser.PtaCatalog(json, 1).Outcome == "unrecognized_catalog", "unknown or short catalog not empty");
        var mixed = JsonNode.Parse(F.Catalog())!; mixed["userGroups"]![0]!["type"] = "NORMAL_USER";
        Check(MetadataPlatformParser.PtaCatalog(mixed.ToJsonString(), 1).Courses.Count == 1, "invitation and public content excluded");
        mixed["userGroups"]![0]!["type"] = "UNKNOWN_TYPE";
        Check(MetadataPlatformParser.PtaCatalog(mixed.ToJsonString(), 1).Outcome == "partial_catalog", "unknown class type reported");
        var teaching = MetadataPlatformParser.PtaSets(F.Teaching(true), 1, true);
        Check(teaching.Outcome == "ok" && teaching.CoursesBySet["50001"].Single().Id == F.HiddenCourse.Id, "teacher-hidden class discoverable from teaching sets");
        var unknown = JsonNode.Parse(F.Teaching())!; unknown.AsObject().Remove("userGroupsByProblemSetId");
        Check(MetadataPlatformParser.PtaSets(unknown.ToJsonString(), 1, true).Outcome == "unrecognized_tasks", "course associations required");
        var duplicate = JsonNode.Parse(F.Catalog())!; duplicate["userGroups"]![1] = duplicate["userGroups"]![0]!.DeepClone();
        Check(MetadataPlatformParser.PtaCatalog(duplicate.ToJsonString(), 1).Outcome == "partial_catalog", "duplicate class rejected");
    }
    private static PtaProblemSet Listed(JsonObject set) => MetadataPlatformParser.PtaSets(F.Sets([set]), 1).Items.Single();
    private static void StatesAndDates()
    {
        var set = F.Set("30001"); var listed = Listed(set);
        var pending = MetadataPlatformParser.PtaTask(F.Exam(set), F.Account, F.Course, listed);
        Check(pending is { Outcome: "ok", Task.State: TaskState.Pending }, "missing personal exam and READY means not started");
        var closed = MetadataPlatformParser.PtaTask(F.Exam(set, status: "END"), F.Account, F.Course, listed);
        Check(closed.Task?.State == TaskState.Pending, "closed set does not imply completed");
        var completed = MetadataPlatformParser.PtaTask(F.Exam(set, status: "END", examStatus: "END", ended: true), F.Account, F.Course, listed);
        Check(completed.Task?.State == TaskState.Completed && completed.Task.StateEvidence == "explicit", "explicit candidate ended marker");
        Check(completed.Task?.DeadlineUtc == F.Now.AddDays(1), "actual finish time never replaces scheduled deadline");
        foreach (var ended in new bool?[] { false, null })
        {
            var expired = MetadataPlatformParser.PtaTask(F.Exam(set, status: "END", examStatus: "END", ended: ended), F.Account, F.Course, listed);
            Check(expired is { Outcome: "partial_tasks", Task.State: null }, "END with score but no explicit completion requires review");
            var active = MetadataPlatformParser.PtaTask(F.Exam(set, status: "PROCESSING", examStatus: "PROCESSING", ended: ended), F.Account, F.Course, listed);
            Check(active.Task?.State == TaskState.Pending, "scored processing exam remains pending");
        }
        var duration = F.Set("30002", timeType: "FIXED_DURATION"); var limit = F.Now.AddMinutes(35);
        var personal = MetadataPlatformParser.PtaTask(F.Exam(duration, status: "PROCESSING", examStatus: "PROCESSING", ended: false, candidateEnd: limit), F.Account, F.Course, Listed(duration));
        Check(personal.Task?.DeadlineUtc == limit && personal.Task.TimeText.Contains("deadlineSource=exam.endAt", StringComparison.Ordinal), "personal fixed duration end wins over global close");
        var delayed = JsonNode.Parse(F.Exam(duration, status: "PROCESSING", examStatus: "PROCESSING", ended: false, candidateEnd: limit))!;
        delayed["exam"]!["delayedEndAt"] = F.Now.AddMinutes(20).ToString("O");
        Check(MetadataPlatformParser.PtaTask(delayed.ToJsonString(), F.Account, F.Course, Listed(duration)).Task?.DeadlineUtc == limit,
            "exam.endAt is official answering deadline and delayed evidence preserved");
        var noEnd = JsonNode.Parse(F.Exam(duration, status: "PROCESSING", examStatus: "PROCESSING"))!;
        noEnd["exam"]!.AsObject().Remove("endAt");
        Check(MetadataPlatformParser.PtaTask(noEnd.ToJsonString(), F.Account, F.Course, Listed(duration)) is { Outcome: "partial_tasks", Task.DeadlineUtc: null },
            "missing personal duration deadline not guessed");
        var always = F.Set("30005", timeType: "ALWAYS_AVAILABLE");
        var noDeadline = MetadataPlatformParser.PtaTask(F.Exam(always), F.Account, F.Course, Listed(always));
        Check(noDeadline is { Outcome: "ok", Task.NoDeadline: true, Task.DeadlineUtc: null }, "always available explicitly has no deadline");
        foreach (var type in new[] { "PEER_REVIEW", "ICPC", "ENTERPRISE_EXAM", "UNRECOGNIZED" })
        {
            var special = F.Set("30010", type: type);
            var task = MetadataPlatformParser.PtaTask(F.Exam(special, status: "END", examStatus: "END", ended: true), F.Account, F.Course, Listed(special));
            Check(task.Task?.State is null && task.Outcome == "partial_tasks", "special workflow requires review");
        }
        var wrong = MetadataPlatformParser.PtaTask(F.Exam(set, account: F.OtherAccount, status: "PROCESSING", examStatus: "PROCESSING"), F.Account, F.Course, listed);
        Check(wrong is { Outcome: "account_mismatch", Task: null }, "foreign candidate exam never imported");
        var foreignSet = F.Set("39999");
        Check(MetadataPlatformParser.PtaTask(F.Exam(foreignSet), F.Account, F.Course, listed) is { Outcome: "task_mismatch", Task: null }, "detail set id must match selected list");
        var date = F.Set("30011"); date["endAt"] = "2026-11-05T08:00:00";
        Check(MetadataPlatformParser.PtaTask(F.Exam(date), F.Account, F.Course, Listed(date)) is { Outcome: "partial_tasks", Task.DeadlineUtc: null }, "timezone-free date not guessed");
        date["endAt"] = F.Now.AddDays(-2).ToString("O");
        Check(MetadataPlatformParser.PtaTask(F.Exam(date), F.Account, F.Course, Listed(date)).Task?.DeadlineUtc is null, "reverse date order rejected");
        Check(MetadataTaskNavigation.Matches(pending.Task!) && MetadataTaskNavigation.Key("pta", pending.Task!.Link) is not null, "official set entry bound to remote id");
        foreach (var address in new[] { "http://pintia.cn/problem-sets/30001/exam/overview", "https://pintia.cn.evil.invalid/problem-sets/30001/exam/overview",
            "https://pintia.cn/problem-sets/30001/exam/problems", "https://pintia.cn/problem-sets/30001/exam/overview?start=1",
            "https://pintia.cn/problem-sets/30001/exam/overview#answers", "https://pintia.cn:9443/problem-sets/30001/exam/overview",
            "https://pintia.cn/problem-sets/30000/../30001/exam/overview", "https://pintia.cn/problem-sets/030001/exam/overview" })
            Check(MetadataTaskNavigation.Key("pta", address) is null, "unconfirmed or answer navigation rejected");
        Check(!MetadataTaskNavigation.Matches(pending.Task! with { RemoteId = "30099" }), "altered set link blocked");
    }
    private static async Task HttpAsync()
    {
        var cookies = new List<MetadataSessionCookie> { F.Cookie() }; var observed = new List<Uri>(); var handler = new F.Handler { RenewNext = true };
        using var reader = new MetadataHttpReader(MetadataPlatform.Pta, (address, _) =>
        { observed.Add(address); return Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(cookies.Where(cookie => cookie.AppliesTo(address)).ToArray()); }, handler,
            (_, _, renewed, _) => { foreach (var cookie in renewed) { cookies.RemoveAll(previous => previous.Key == cookie.Key); cookies.Add(cookie); } return Task.CompletedTask; });
        var profile = await reader.ReadAsync(new("profile"), default);
        Check(profile.Outcome == "ok" && profile.AccountId == F.Account, "HTTP profile exposes validated official user");
        Check(cookies.Single().Value == handler.Session && cookies.Single() is { Secure: true, HttpOnly: true, SameSite: "Strict" }, "HTTP session renewal retains flags");
        await reader.ReadAsync(new("pta-courses"), default); await reader.ReadAsync(new("pta-teaching-sets"), default);
        await reader.ReadAsync(new("pta-course-sets") { Course = F.Course }, default);
        await reader.ReadAsync(new("pta-exam") { Course = F.Course, ProblemSetId = "30001" }, default);
        Check(handler.Requests.All(request => request.Method == HttpMethod.Get && request.Content is null), "only GET metadata routes");
        Check(handler.Requests.Single(request => request.RequestUri!.AbsolutePath == "/api/user-groups/member-visible").RequestUri!.Query == "?page=0&limit=12", "official zero based class paging");
        Check(Uri.UnescapeDataString(handler.Requests.Single(request => request.RequestUri!.AbsolutePath == "/api/problem-sets").RequestUri!.Query)
            .Contains("userGroupMemberJoinType\":\"CLASS", StringComparison.Ordinal), "teaching filter excludes public practice catalog");
        Check(observed.All(uri => uri.IdnHost is "pintia.cn" or "passport.pintia.cn"), "credential scope remains official");
        foreach (var request in new[] { new MetadataRequest("start-exam"), new MetadataRequest("pta-exam") { Course = F.Course, ProblemSetId = "../30001" },
            new MetadataRequest("pta-course-sets", 0) { Course = F.Course }, new MetadataRequest("pta-course-sets", 101) { Course = F.Course },
            new MetadataRequest("pta-course-sets") { Course = F.Course with { Kind = "shared" } }, new MetadataRequest("pta-exam", 2) { Course = F.Course, ProblemSetId = "30001" } })
        {
            var count = handler.Paths.Count;
            Check((await reader.ReadAsync(request, default)).Outcome == "unsupported_request" && handler.Paths.Count == count, "unsupported request makes no network call");
        }
        handler.Override = request => F.Handler.Reply(request, HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"PERMISSION_DENIED\"}}");
        Check((await reader.ReadAsync(new("pta-exam") { Course = F.Course, ProblemSetId = "30001" }, default)).Outcome == "course_access_denied", "HTTP 403 course denial does not expire cookie");
        handler.Override = request => F.Handler.Reply(request, HttpStatusCode.OK, F.Guest);
        Check((await reader.ReadAsync(new("profile"), default)).Outcome == "login_required", "200 guest expiry classified");
        handler.Override = request => F.Handler.Reply(request, HttpStatusCode.OK, "{\"error\":{\"code\":\"USER_NOT_LOGGED_IN\"}}");
        Check((await reader.ReadAsync(new("pta-courses"), default)).Outcome == "login_required", "embedded login expiry classified");
        handler.Override = request => F.Handler.Reply(request, HttpStatusCode.NotFound, "{\"error\":{\"code\":\"USER_NOT_FOUND\"}}");
        Check((await reader.ReadAsync(new("pta-teaching-sets"), default)).Outcome == "platform_error", "USER_NOT_FOUND not empty or account expiry guess");
        handler.Override = request => F.Handler.Reply(request, HttpStatusCode.TooManyRequests, "{}");
        Check((await reader.ReadAsync(new("pta-courses"), default)).Outcome == "rate_limited", "429 stops requests");
        var before = handler.Paths.Count;
        Check((await reader.ReadAsync(new("profile"), default)).Outcome == "rate_limited" && handler.Paths.Count == before, "rate limit applies whole round");
    }
    private static async Task SyncAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pta-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new TaskStore(Path.Combine(root, "tasks.db")); var reader = new F.Reader();
        var catalog = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(catalog is { Courses: 2, Tasks: 0, CourseListCovered: true, AccountVerified: true }, "first sync is full course catalog only");
        Check(reader.Requests.All(request => request.Operation is "profile" or "pta-courses" or "pta-teaching-sets"), "no per-course or exam requests before selection");
        var recording = store.LoadCourseRecording("pta");
        var key = CourseRecordingRules.MetadataKey("pta", F.Account, F.Course.Id, "class");
        store.SaveCourseRecording("pta", false, [key], true, recording.Revision, F.Now); reader.Requests.Clear();
        var sync = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(sync.Tasks == 5 && sync.Added == 5 && sync.SelectedCourses == 1 && sync.StatusNeedsReview == 1, "selected teaching course imports five set tasks");
        Check(sync.TaskListCoursesRead == 1 && sync.CoursesRead == 0 && !sync.FullTaskListsCovered,
            "complete list remains distinct from unresolved personal answer status");
        Check(reader.Requests.Where(request => request.Operation is "pta-course-sets" or "pta-exam").All(request => request.Course?.Id == F.Course.Id), "unselected course details never read");
        Check(store.LoadTasks().All(task => task.Source == TaskSource.Pta && task.PlatformId == "pta"), "unified data source");
        Check(store.LoadTasks().Single(task => task.PlatformTitle == "合成一直可用题目集") is { DeadlineKind: DeadlineKind.None, DeadlineUtc: null }, "no deadline survives storage");
        Check(store.LoadTasks().Single(task => task.PlatformTitle == "合成已超时答卷题目集").State == TaskState.NeedsReview, "timeout remains review state in store");
        var original = store.LoadTasks().Single(task => task.PlatformTitle == "合成尚未开始题目集");
        var manual = store.Edit(original.Id, original.Revision, new("人工题目集标题", "pta", "人工课程名", DeadlineKind.Known, null,
            F.Now.AddDays(3), original.Link, null, "人工成绩", "人工备注", 2), F.Now);
        store.SetState(manual.Id, manual.Revision, TaskState.Completed, F.Now);
        sync = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync(); var latest = store.Get(original.Id)!;
        Check(sync.Added == 0 && store.LoadTasks().Count == 5 && latest.Title == "人工题目集标题" && latest.State == TaskState.Completed
            && latest.DeadlineUtc == F.Now.AddDays(3) && latest.ScoreText == "人工成绩", "stable ids and manual overrides retained");
        var opened = ""; var navigation = new TaskNavigationService(store, null, address => opened = address);
        await navigation.OpenAsync(original.Id, default);
        Check(opened == "https://pintia.cn/problem-sets/30001/exam/overview" && TaskNavigationRules.UsesSavedPlatformEntry(latest), "external official entry requires no answering request");
        reader.Override = request => request.Operation == "pta-exam" ? new("course_access_denied", 403) : null;
        var denied = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(!denied.LoginRequired && denied.Problems.Any(problem => problem.Code == "PTA006") && store.LoadTasks().Count == 5, "course denial retains session and task history");
        Check(denied.Problems.Where(problem => problem.Code == "PTA006").All(problem => problem.Message.Contains("HTTP 403", StringComparison.Ordinal)
            && problem.Message.Contains("当前登录保留", StringComparison.Ordinal)), "PTA denial detail exposes safe status and retained login");
        Check(store.LoadTasks().Single(task => task.PlatformTitle == "合成已结束答题题目集").State == TaskState.Completed, "failed detail cannot erase confirmed completed state");
        reader.Override = request => request.Operation == "profile" ? new("login_required", 200) : null;
        var expired = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(expired.LoginRequired && store.LoadTasks().Count == 5 && store.Get(original.Id)!.State == TaskState.Completed, "expiry preserves history and manual edits");
        reader.Override = request => request.Operation == "pta-course-sets" ? new("ok", 200, F.Sets([F.Set("39999")], 50)) : null;
        var partial = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(!partial.FullTaskListsCovered && partial.Tasks == 0 && store.LoadTasks().Count == 5, "short pagination never imports incomplete list");
        reader.Override = request => request.Operation == "pta-course-sets" ? new("ok", 200, F.Sets(Enumerable.Range(1, 20).Select(i => F.Set((31000 + i).ToString())), 40)) : null;
        var repeat = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(repeat.Problems.Any(problem => problem.Code == "PTA005") && repeat.Tasks == 0 && store.LoadTasks().Count == 5, "repeated pagination refused");
        reader.Override = request => request.Operation == "pta-exam" ? new("ok", 200, F.ExamFor(request.ProblemSetId, F.OtherAccount)) : null;
        var foreign = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(!foreign.AccountVerified && foreign.Tasks == 0 && store.LoadTasks().Count == 5, "foreign answer sheet cancels course import");
        var profileCalls = 0;
        reader.Override = request => request.Operation == "profile" && ++profileCalls >= 3 ? new("ok", 200, F.Profile(F.OtherAccount)) : null;
        var changed = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(!changed.AccountVerified && changed.Tasks == 0 && store.LoadTasks().Count == 5, "account rechecked immediately before import");
        reader.Override = null; reader.Hidden = true; var refreshed = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync(catalogOnly: true);
        Check(refreshed.Courses == 3 && refreshed.CourseListCovered, "hidden teaching course included in picker");
        recording = store.LoadCourseRecording("pta");
        store.SaveCourseRecording("pta", false, [CourseRecordingRules.MetadataKey("pta", F.Account, F.HiddenCourse.Id, "class")], true, recording.Revision, F.Now);
        var hidden = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(hidden.Tasks == 1 && !hidden.LoginRequired && store.LoadTasks().Count == 6, "hidden class uses verified teaching directory fallback");
        Check(hidden is { TaskListCoursesRead: 1, TeachingDirectoryFallbackCourses: 1, CoursesRead: 1, FullTaskListsCovered: true }
            && hidden.Problems.Count == 0, "recovered first-page hidden class permission denial is not a remaining error");
        store.RecordMetadataSyncResult(hidden, "completed", F.Now);
        Check(new TaskStore(store.DatabasePath).LoadMetadataSettings("pta").LastSummary is { TaskListCoursesRead: 1, TeachingDirectoryFallbackCourses: 1 },
            "recovery counters survive reopening the task store");
        reader.Override = request => request.Operation == "pta-exam" ? new("ok", 200, F.Exam(F.Set(request.ProblemSetId), F.Account, "END", "END", false)) : null;
        var review = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(review is { Tasks: 1, TaskListCoursesRead: 1, TeachingDirectoryFallbackCourses: 1, CoursesRead: 0, StatusNeedsReview: 1, FullTaskListsCovered: false }
            && review.Problems.All(problem => problem.Code == "PTA003"), "directory recovery keeps genuine answer-status review without stale permission error");
        foreach (var (outcome, code, status) in new[] { ("network_error", "PTA002", 0), ("unrecognized_response", "PTA003", 200),
            ("entry_unavailable", "PTA006", 404), ("login_required", "PTA001", 401), ("rate_limited", "PTA005", 429) })
        {
            reader.Requests.Clear();
            reader.Override = request => request.Operation == "pta-course-sets" ? new(outcome, status == 0 ? null : status) : null;
            var failed = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
            Check(failed is { Tasks: 0, TaskListCoursesRead: 0, TeachingDirectoryFallbackCourses: 0, FullTaskListsCovered: false }
                && failed.Problems.Any(problem => problem.Code == code) && reader.Requests.All(request => request.Operation != "pta-exam"),
                "hidden course fallback cannot mask " + outcome);
            Check(store.LoadTasks().Count == 6 && store.Get(original.Id)!.State == TaskState.Completed, "failed fallback preserves history and manual state: " + outcome);
        }
        reader.Override = request => request.Operation == "pta-course-sets" ? new("ok", 200, "{}") : null;
        var unknownHidden = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(unknownHidden.Tasks == 0 && unknownHidden.TeachingDirectoryFallbackCourses == 0
            && unknownHidden.Problems.Any(problem => problem.Code == "PTA003"), "unknown hidden-course list is not replaced with a successful fallback");
        var hiddenPage = Enumerable.Range(0, 20).Select(index => F.Set((50001 + index).ToString())).ToArray();
        reader.Override = request => request.Operation == "pta-course-sets"
            ? request.Page == 1 ? new("ok", 200, F.Sets(hiddenPage, 21)) : new("course_access_denied", 403) : null;
        var laterDenied = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(laterDenied.Tasks == 0 && laterDenied.TeachingDirectoryFallbackCourses == 0 && laterDenied.Problems.Any(problem => problem.Code == "PTA006"),
            "permission denied after a readable page remains incomplete");
        reader.Override = request => request.Operation == "pta-course-sets" ? new("ok", 200, F.Sets(hiddenPage, 40)) : null;
        var hiddenRepeated = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(hiddenRepeated.Tasks == 0 && hiddenRepeated.TeachingDirectoryFallbackCourses == 0 && hiddenRepeated.Problems.Any(problem => problem.Code == "PTA005"),
            "hidden-course repeated pagination is never overwritten by directory fallback");
        reader.Override = request => request.Operation == "pta-courses" ? new("unrecognized_catalog", 200) : null;
        var catalogUnknown = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(catalogUnknown.Tasks == 0 && catalogUnknown.TeachingDirectoryFallbackCourses == 0 && !catalogUnknown.CourseListCovered,
            "unknown visible catalog cannot establish a teacher-hidden class");
        reader.Override = request => request.Operation == "pta-courses" ? new("ok", 200, F.Catalog([F.Course, F.OtherCourse, F.HiddenCourse])) : null;
        var visibleDenied = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(visibleDenied.Tasks == 0 && visibleDenied.TeachingDirectoryFallbackCourses == 0 && visibleDenied.Problems.Any(problem => problem.Code == "PTA006"),
            "visible class permission denial cannot use hidden-class fallback");
        reader.Override = request => request.Operation == "pta-exam" ? new("rate_limited", 429) : null;
        var throttledExam = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(throttledExam.Tasks == 0 && throttledExam.TeachingDirectoryFallbackCourses == 0 && throttledExam.Problems.Any(problem => problem.Code == "PTA005"),
            "throttled answer metadata reports the real remaining failure before importing");
        reader.Override = null;
        recording = store.LoadCourseRecording("pta"); store.SaveCourseRecording("pta", false, [], true, recording.Revision, F.Now); reader.Requests.Clear();
        var deselected = await new MetadataSynchronizer(MetadataPlatform.Pta, store, reader).RunAsync();
        Check(deselected.Tasks == 0 && reader.Requests.All(request => request.Operation is "profile" or "pta-courses" or "pta-teaching-sets") && store.LoadTasks().Count == 6,
            "selecting none stops per-course reads and retains history");
        var archive = new MetadataSessionStore(MetadataPlatform.Pta, Path.Combine(root, "pta-session.bin"), store);
        archive.Save([F.Cookie()], F.Now); store.ConfirmPlatformSession("pta", F.Now);
        Check(archive.Load(F.Now).Single().Value == F.Cookie().Value && store.LoadPlatformSessionInfo("pta").State == "ready", "session stored protected in repository");
        Check(!File.ReadAllText(store.DatabasePath).Contains(F.Cookie().Value, StringComparison.Ordinal), "plaintext cookie never in task database");
        archive.Invalidate(F.Now); Check(archive.Load(F.Now).Count == 0 && store.LoadPlatformSessionInfo("pta").State == "expired", "expired session removed from restore");
        Check(MetadataSessionStore.MissingForRestore(MetadataPlatform.Pta, [F.Cookie()], [F.Cookie("synthetic-new-login")]).Count == 0, "current login wins over archived cookie");
        var diagnostics = new MetadataLoginDiagnostics(MetadataPlatform.Pta, Path.Combine(root, "login-diagnostics.json"));
        diagnostics.Record(MetadataLoginStage.FrameNavigation, MetadataLoginResult.Started,
            "https://turing.captcha.qcloud.com/template/drag_ele.html?ticket=synthetic-verifier-secret");
        Check(diagnostics.Snapshot().Events.Single().Destination == "captcha" && !File.ReadAllText(diagnostics.Path).Contains("synthetic-verifier-secret", StringComparison.Ordinal),
            "captcha diagnostics retain destination kind without verifier tokens");
        Directory.Delete(root, true);
    }
}
