using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AccessProbe;
using Microsoft.Data.Sqlite;
using XuetangTestData;

internal static class XuetangChecks
{
    private static int checks;
    public static async Task RunAsync()
    {
        LoginPageChecks();
        ParserChecks();
        await StorageAndSyncChecks();
        await HttpChecks();
        SessionChecks();
        Console.WriteLine($"PASS: {checks} Xuetang official-contract metadata, paging, delayed deadlines, isolation, cancellation and protected-session checks (synthetic only).");
    }

    private static void LoginPageChecks()
    {
        Check(XuetangPlatform.Login.AbsolutePath == "/" && XuetangPlatform.IsLoginNavigation("https://www.xuetangx.com/login"),
            "desktop login opens official home while retaining official return routes");
        var captcha = "https://newassets.hcaptcha.com/captcha/v1/10dd112850fe5c29df1e51ddc05fdfb8b5f6dbae/static/hcaptcha.html#frame=challenge&id=synthetic";
        foreach (var address in new[] { captcha, "https://turing.captcha.qcloud.com/template/placeholder_v2.html?appid=synthetic",
            "https://turing.captcha.qcloud.com/template/drag_ele.html", "https://ca.turing.captcha.qcloud.com/template/drag_ele_global.html" })
            Check(XuetangPlatform.IsLoginFrameNavigation(address) && !XuetangPlatform.IsLoginNavigation(address),
                "confirmed captcha document permitted only inside login frame");
        foreach (var address in new[] { "http://newassets.hcaptcha.com/captcha/v1/version/static/hcaptcha.html",
            "https://newassets.hcaptcha.com.evil.example/captcha/v1/version/static/hcaptcha.html",
            "https://newassets.hcaptcha.com:444/captcha/v1/version/static/hcaptcha.html",
            "https://user@newassets.hcaptcha.com/captcha/v1/version/static/hcaptcha.html",
            "https://newassets.hcaptcha.com/unrelated.html", "https://newassets.hcaptcha.com/captcha/v1/version/static/unrelated.html",
            "https://turing.captcha.qcloud.com/unrelated.html", "https://turing.captcha.qcloud.com/template/../unrelated.html" })
            Check(!XuetangPlatform.IsLoginFrameNavigation(address), "unverified verifier host path or insecure address remains blocked");
        Check(XuetangPlatform.LoginResourceKind("https://fe-static-yuketang.yuketang.cn/fe/static/xt/1.6.376/xtassets/app.js?synthetic=1") == "script"
            && XuetangPlatform.LoginResourceKind("https://fe-static-yuketang.yuketang.cn/fe/static/xt/1.6.999/xtassets/app.css") == "style",
            "application resource diagnostics follow future official asset versions");
        foreach (var address in new[] { "https://fe-static-yuketang.yuketang.cn/fe/static/assets/analytics.js",
            "https://evil.example/fe/static/xt/1.6.376/app.js", "https://fe-static-yuketang.yuketang.cn/fe/static/xt/1.6.376/image.png" })
            Check(XuetangPlatform.LoginResourceKind(address) == "none", "unrelated resource not reported as login application failure");

        var path = Path.Combine(Path.GetTempPath(), "xuetang-login-diagnostic-" + Guid.NewGuid().ToString("N"), "login.json");
        var diagnostic = new XuetangLoginDiagnostics(path);
        diagnostic.Record(XuetangLoginStage.Preparation, XuetangLoginResult.Started);
        diagnostic.Record(XuetangLoginStage.Resource, XuetangLoginResult.ScriptUnavailable,
            "https://fe-static-yuketang.yuketang.cn/fe/static/xt/1.6.376/xtassets/private-synthetic.js?token=synthetic-secret", 200);
        diagnostic.Record(XuetangLoginStage.MainNavigation, XuetangLoginResult.Ready, "https://www.xuetangx.com/?code=synthetic-secret");
        diagnostic.Record(XuetangLoginStage.Popup, XuetangLoginResult.Blocked, "https://evil.example/private-synthetic");
        Check(diagnostic.Snapshot().PageFailure is { Stage: XuetangLoginStage.Resource, Result: XuetangLoginResult.ScriptUnavailable }
            && diagnostic.Describe().Contains("登录脚本未能加载", StringComparison.Ordinal), "navigation or popup cannot erase resource failure");
        var serialized = File.ReadAllText(path);
        Check(!serialized.Contains("synthetic-secret", StringComparison.Ordinal) && !serialized.Contains("private-synthetic", StringComparison.Ordinal)
            && !serialized.Contains("https://", StringComparison.Ordinal), "persisted login diagnostic excludes paths queries and secrets");
        diagnostic.Record(XuetangLoginStage.Preparation, XuetangLoginResult.Started);
        Check(diagnostic.Snapshot().Attempts == 2 && diagnostic.Snapshot().PageFailure is null, "explicit retry resets current failure while retaining history");
        for (var index = 0; index < 30; index++) diagnostic.Record(XuetangLoginStage.MainNavigation, XuetangLoginResult.Ready);
        Check(diagnostic.Snapshot().Events.Count == 24 && diagnostic.PersistenceOutcome == "saved", "login diagnostic history bounded and persisted");
    }

    private static void ParserChecks()
    {
        Check(XuetangParser.LoginOutcome(Metadata.Login(false)) == "login_required", "official logged-out envelope recognized");
        Check(XuetangParser.LoginOutcome(Metadata.Login()) == "ok", "explicit logged-in envelope recognized");
        foreach (var json in new[] { "{}", "[]", "{\"success\":true,\"data\":{}}", "<html>login</html>", "{\"data\":{\"is_login\":true}}" })
            Check(XuetangParser.LoginOutcome(json) != "ok", "unknown login cannot authenticate");
        foreach (var code in new[] { 50008, 50012, 50014 })
            Check(XuetangParser.EnvelopeOutcome("{\"success\":false,\"code\":" + code + "}") == "login_required", "official token rejection recognized");
        Check(XuetangParser.EnvelopeOutcome("{\"success\":false,\"error_code\":500300}") == "login_required", "official missing-login error recognized");
        Check(XuetangParser.AccountId(Metadata.Profile()) == "91001" && XuetangParser.AccountId(Metadata.Envelope(new { nickname = "synthetic" })) is null,
            "account comes from basic profile id");
        foreach (var account in new[] { "0", "-1", "x", "1e2", "", new string('1', 65) })
            Check(XuetangParser.AccountId(Metadata.Profile(account)) is null, "invalid account rejected");
        var page = XuetangParser.Catalog(Metadata.Catalog([Metadata.Course(), Metadata.Course("102")], 2), 1);
        Check(page.Outcome == "ok" && page.Courses.Count == 2 && page.Total == 2, "flat official product-list classroom structure");
        Check(XuetangParser.Catalog(Metadata.Catalog([Metadata.Course(), Metadata.Course()], 2), 1).DuplicateRows == 1, "duplicate courses deduplicated");
        Check(XuetangParser.Catalog(Metadata.Catalog([Metadata.Course("101", 2)], 1), 1).Outcome != "ok", "status bucket mismatch detected");
        Check(XuetangParser.Catalog(Metadata.Catalog([Metadata.Course("101", 2, true)], 1), 0, true).Outcome == "ok", "stowed course preserves original status");
        Check(XuetangParser.Catalog(Metadata.Catalog([], 0), 1).Outcome == "ok", "explicit empty catalog recognized");
        foreach (var json in new[] { "{}", Metadata.Envelope(new { courses = new object[0] }), Metadata.Catalog([Metadata.Course()], 0),
            Metadata.Catalog([Metadata.Course()], 1).Replace("\"classroom_id\":\"101\"", "\"classroom_id\":null") })
            Check(XuetangParser.Catalog(json, 1).Outcome != "ok", "unknown or inconsistent catalog not empty success");
        var multi = XuetangParser.Catalog(Metadata.Catalog([Metadata.Course()], 1).Replace("\"property\":0", "\"property\":1"), 1);
        Check(multi.UnsupportedCourses == 1 && multi.Courses.Count == 0, "multi-course program cannot impersonate single classroom");
        var schedule = XuetangParser.Schedule(Metadata.Schedule()); var resets = XuetangParser.ResetTimes(Metadata.Resets());
        var tasks = XuetangParser.Chapter(Metadata.Tasks(), "91001", Metadata.Course(), schedule, resets);
        Check(tasks.Tasks.Count == 5 && tasks.Tasks.Count(task => task.Kind == "exam") == 1, "only visible exercises and exams selected");
        Check(tasks.Tasks.Single(task => task.RemoteId == "205").BeginUtc == Metadata.Now.AddHours(1).ToUniversalTime(), "published locked future task retained");
        Check(tasks.Tasks.Single(task => task.RemoteId == "201").DeadlineUtc == Metadata.Now.AddHours(8).ToUniversalTime(), "reset deadline overrides prior deadline");
        Check(tasks.Tasks.Single(task => task.RemoteId == "202").DeadlineUtc == Metadata.Now.AddHours(4).ToUniversalTime(), "millisecond cutoff converted correctly");
        Check(tasks.Tasks.Single(task => task.RemoteId == "202").State == TaskState.Completed && tasks.Tasks.Single(task => task.RemoteId == "203").ProgressPercent == 50,
            "official fractional learning progress used");
        Check(tasks.Tasks.Single(task => task.RemoteId == "204").State is null, "missing individual progress stays unknown");
        Check(tasks.Tasks.All(task => task.StateEvidence == "unknown" || task.StatusExplanation.Contains("提交", StringComparison.Ordinal)), "progress evidence is not submission evidence");
        Check(tasks.Tasks.All(task => task.Link!.Contains("/learn/product101/course101/101/", StringComparison.Ordinal)), "task routes contain confirmed product course and classroom");
        Check(XuetangParser.Schedule(Metadata.Envelope(new { leaf_schedules = new { x = 1, y = 2 } })).Outcome != "ok", "invalid schedule keys and range detected");
        foreach (var json in new[] { Metadata.Envelope(new { leaf_schedules = new { @override = new { status = 3 } } }), Metadata.Envelope(new { leaf_schedules = new[] { 0 } }), "{}" })
            Check(XuetangParser.Schedule(json).Outcome != "ok", "rain-specific schedule shape not reused");
        var unverified = XuetangParser.Chapter(Metadata.Tasks(), "91001", Metadata.Course(), schedule,
            new("reset_scope_missing", new Dictionary<string, DateTimeOffset>()));
        Check(unverified.Tasks.Count == 5 && unverified.Tasks.All(task => task.DeadlineUtc is null) && unverified.Issues.Contains("reset_times_unverified"),
            "unverified extensions never become reliable old deadlines");
        foreach (var leaf in new[] { Metadata.Leaf("201").Replace("\"score_deadline\":" + Metadata.Now.AddHours(4).ToUnixTimeMilliseconds(), "\"score_deadline\":\"tomorrow\""),
            Metadata.Leaf("201", deadline: 1700000000), Metadata.Leaf("201", deadline: long.MaxValue) })
            Check(XuetangParser.Chapter(Metadata.Chapter(leaf), "91001", Metadata.Course(), schedule, resets with { Deadlines = new Dictionary<string, DateTimeOffset>() })
                is { Issues.Count: > 0 } invalid && invalid.Tasks.All(task => task.DeadlineUtc is null), "bad time clears reliable deadline");
        Check(XuetangParser.ResetTimes(Metadata.Envelope(new[] { new { leaf_id = "201", reset_deadline = 1 } })).Outcome != "ok", "bad reset date not confirmed");
        Check(XuetangParser.ResetTimes(Metadata.Resets(false)).Outcome == "ok", "explicit empty reset list valid");
        var earlyEnd = Metadata.Leaf("201").Replace("\"end_time\":0", "\"end_time\":" + Metadata.Now.AddHours(2).ToUnixTimeMilliseconds());
        var emptyResets = XuetangParser.ResetTimes(Metadata.Resets(false));
        Check(XuetangParser.Chapter(Metadata.Chapter(earlyEnd), "91001", Metadata.Course(), schedule, emptyResets).Tasks.Single().DeadlineUtc == Metadata.Now.AddHours(2).ToUniversalTime(),
            "separate access cutoff kept conservatively");
        var reversed = XuetangParser.Chapter(Metadata.Chapter(Metadata.Leaf("201", begin: Metadata.Now.AddDays(1).ToUnixTimeMilliseconds())), "91001", Metadata.Course(), schedule, emptyResets);
        Check(reversed.Tasks.Single().DeadlineUtc is null && reversed.Issues.Contains("invalid_time_window"), "reversed time window not reminded");
        var nested = "{\"success\":true,\"data\":{\"course_chapter\":[{\"section_leaf_list\":[{\"name\":\"section\",\"leaf_list\":[" + Metadata.Leaf("201") + "]}]}]}}";
        Check(XuetangParser.Chapter(nested, "91001", Metadata.Course(), schedule, emptyResets).Tasks.Count == 1, "nested official section structure walked");
        Check(XuetangParser.Chapter(nested.Replace("\"leaf_list\":[", "\"other_list\":["), "91001", Metadata.Course(), schedule, emptyResets).Outcome != "ok", "unknown tree structure not empty complete");
        var conflict = XuetangParser.Chapter(Metadata.Chapter(Metadata.Leaf("201"), Metadata.Leaf("201", 5)), "91001", Metadata.Course(), schedule, emptyResets);
        Check(conflict.Tasks.Count == 0 && conflict.Issues.Contains("conflicting_task"), "conflicting same-id task is excluded");
        Check(XuetangParser.Chapter(Metadata.Chapter(Metadata.Leaf("201"), Metadata.Leaf("201")), "91001", Metadata.Course(), schedule, emptyResets).DuplicateRows == 1, "same task deduplication");
        Check(XuetangParser.Chapter(Metadata.Chapter(Metadata.Leaf("201", 99)), "91001", Metadata.Course(), schedule, emptyResets).Outcome != "ok", "new leaf type flags coverage");
        var diagnostics = JsonSerializer.Serialize(tasks.Tasks);
        Check(!diagnostics.Contains("91001", StringComparison.Ordinal) && !diagnostics.Contains("product101", StringComparison.Ordinal)
            && !diagnostics.Contains("https://", StringComparison.Ordinal), "task diagnostics omit account and navigation identifiers");
    }

    private static async Task StorageAndSyncChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "xuetang-business-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tasks.db"); var store = FixtureTaskStore.Create(path);
        var other = store.Create(new("其他平台", "ucampus", "合成课程", DeadlineKind.Unknown, null, null, null, null, "", "", null), Metadata.Now);
        var rows = XuetangParser.Chapter(Metadata.Tasks(), "91001", Metadata.Course(), XuetangParser.Schedule(Metadata.Schedule()), XuetangParser.ResetTimes(Metadata.Resets())).Tasks;
        var initial = store.ImportXuetangTasks(rows, Metadata.Now);
        Check(initial.Added == 5 && store.LoadTasks().Count == 6 && store.Get(other.Id)!.Source == TaskSource.Manual, "unified store retains independent platforms");
        Check(store.ImportXuetangTasks(rows, Metadata.Now).Updated == 5 && store.LoadTasks().Count == 6, "resync stable task identities");
        Check(store.LoadTasks().Single(task => task.PlatformTitle == "合成任务 204").State == TaskState.NeedsReview, "unknown automatic task appears needs-review");
        var task = store.LoadTasks().Single(item => item.SourceKey == TaskStore.XuetangTaskKey(rows[0]));
        var draft = new TaskDraft("手动标题", "xuetang", "手动课程", DeadlineKind.Known, Metadata.Now, Metadata.Now.AddHours(24),
            "https://www.xuetangx.com/manual-entry", 100, "人工成绩", "人工备注", 2);
        var manuallyEdited = store.Edit(task.Id, task.Revision, draft, Metadata.Now);
        store.SetState(task.Id, manuallyEdited.Revision, TaskState.Completed, Metadata.Now);
        store.ImportXuetangTasks(rows.Select(row => row with { Title = "官方新标题", DeadlineUtc = Metadata.Now.AddHours(2) }).ToArray(), Metadata.Now);
        var edited = store.Get(task.Id)!;
        Check(edited.Title == draft.Title && edited.CourseName == draft.CourseName && edited.DeadlineUtc == draft.DeadlineUtc
            && edited.Link == draft.Link && edited.ProgressPercent == 100 && edited.State == TaskState.Completed && edited.ScoreText == "人工成绩"
            && edited.Notes == "人工备注" && edited.PriorityOverride == 2, "all explicit manual metadata overrides survive sync");
        Check(edited.PlatformTitle == "官方新标题" && edited.PlatformListTimeText.Length > 0, "official evidence still updated under overrides");
        Check(TaskStore.XuetangTaskKey(rows[0]) != TaskStore.XuetangTaskKey(rows[0] with { AccountId = "91002" })
            && TaskStore.XuetangTaskKey(rows[0]) != TaskStore.XuetangTaskKey(rows[0] with { CourseSign = "othercourse" }), "task identity covers account and course scope");
        var count = store.LoadTasks().Count;
        Reject(() => store.ImportXuetangTasks([rows[0] with { AccountId = "91002" }, rows[1] with { RemoteId = "wrong" }], Metadata.Now), "XT003");
        Check(store.LoadTasks().Count == count, "invalid batch transaction rolled back");
        Reject(() => store.ImportXuetangTasks([rows[0] with { Link = rows[1].Link }], Metadata.Now), "XT003");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(() => Task.Run(() => store.ImportXuetangTasks(rows, Metadata.Now, cancelled.Token)), "cancelled store does not import");
        Check(FixtureTaskStore.Create(path).LoadTasks().Count == count, "reopened unified store retains all tasks");
        using (var database = new SqliteConnection("Data Source=" + path))
        {
            database.Open(); using var command = database.CreateCommand(); command.CommandText = "SELECT payload,protected_link FROM tasks WHERE id=$id";
            command.Parameters.AddWithValue("$id", task.Id); using var reader = command.ExecuteReader(); reader.Read();
            Check(!reader.GetString(0).Contains("91001", StringComparison.Ordinal) && !reader.GetString(0).Contains("https://", StringComparison.Ordinal)
                && !Encoding.UTF8.GetString((byte[])reader.GetValue(1)).Contains("manual-entry", StringComparison.Ordinal), "database protects task link and hashes scope");
        }
        var navigationRows = rows.Where(row => row.RemoteId == "202").ToArray();
        var navTask = store.LoadTasks().Single(item => item.SourceKey == TaskStore.XuetangTaskKey(navigationRows[0])); var opened = new List<string>();
        var navigation = new TaskNavigationService(store, null, opened.Add);
        var result = await navigation.OpenAsync(navTask.Id, default);
        Check(result.Destination == "entry" && opened.Single() == navTask.Link, "completed task opens official leaf entry without claiming rendered content");
        foreach (var address in new[] { "https://www.xuetangx.com/", navTask.Link! + "?next=https://evil.example/", navTask.Link! + "#test",
            navTask.Link!.Replace("www.xuetangx.com", "xuetangx.com.evil.example"), navTask.Link!.Replace("/quiz/", "/answer/") })
            Check(XuetangTaskNavigation.Key(address) is null, "unsupported official task navigation blocked");
        using (var db = new SqliteConnection("Data Source=" + path))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE tasks SET payload=json_set(payload,'$.CollectedNavigationKey','wrong') WHERE id=$id"; cmd.Parameters.AddWithValue("$id", navTask.Id); cmd.ExecuteNonQuery(); }
        await Rejected(() => navigation.OpenAsync(navTask.Id, default), "LINK003", "changed navigation binding never opens browser");
        Check(opened.Count == 1, "rejected navigation no external side effect");

        var syncStore = new TaskStore(Path.Combine(root, "sync.db")); var reader1 = new Reader(Metadata.Respond);
        var pending = await new XuetangSynchronizer(syncStore, reader1).RunAsync();
        Check(pending.Courses == 1 && pending.Tasks == 0 && pending.CoursesRead == 0
            && reader1.Requests.All(uri => uri.AbsolutePath is not (XuetangPlatform.Chapter or XuetangPlatform.Schedule or XuetangPlatform.Product or XuetangPlatform.ResetDeadlines)),
            "unconfirmed Xuetang reads all courses without any task requests");
        var initialSelection = syncStore.LoadCourseRecording("xuetang");
        syncStore.SaveCourseRecording("xuetang", false, initialSelection.Courses.Select(course => course.Key).ToArray(), true, initialSelection.Revision, Metadata.Now);
        reader1.Requests.Clear();
        var summary = await new XuetangSynchronizer(syncStore, reader1).RunAsync();
        Check(summary.CourseListCovered && summary.Courses == 1 && summary.CoursesRead == 1 && summary.Tasks == 5 && summary.Added == 5,
            "full scoped metadata sync imports tasks");
        Check(summary.TasksWithDeadline == 5 && summary.TasksWithLink == 5 && summary.StatusNeedsReview == 1 && summary.CatalogPages == 4,
            "deadline link review and catalog counters preserved");
        Check(reader1.Requests.All(XuetangPlatform.IsReadAddress), "all metadata reads restricted by official allowlist");
        var repeated = await new XuetangSynchronizer(syncStore, reader1).RunAsync();
        Check(repeated.Added == 0 && repeated.Updated == 5 && syncStore.LoadTasks().Count == 5, "automatic repeat no duplication");
        syncStore.RecordXuetangSyncResult(summary, "completed", Metadata.Now);
        Check(syncStore.LoadXuetangSettings().LastSummary!.Tasks == 5 && syncStore.LoadMoocSettings().LastSummary is null, "sync summary isolated per platform");
        var record = syncStore.LoadCourseRecording("xuetang");
        syncStore.SaveCourseRecording("xuetang", false, [], true, record.Revision, Metadata.Now);
        reader1.Requests.Clear(); var excluded = await new XuetangSynchronizer(syncStore, reader1).RunAsync();
        Check(excluded.SelectedCourses == 0 && excluded.Tasks == 0 && syncStore.LoadTasks().Count == 5 && syncStore.LoadRecordedTasks().Count == 0,
            "course deselection retains and hides history");
        Check(reader1.Requests.All(uri => uri.AbsolutePath is not (XuetangPlatform.Chapter or XuetangPlatform.Schedule or XuetangPlatform.Product or XuetangPlatform.ResetDeadlines)),
            "deselected courses make no task requests");
        Check(syncStore.TakeDue(Metadata.Now.AddHours(24)).Count == 0, "deselected tasks do not notify");
        var after = syncStore.LoadCourseRecording("xuetang"); syncStore.SaveCourseRecording("xuetang", true, [], true, after.Revision, Metadata.Now);
        reader1.Requests.Clear(); var catalogOnly = await new XuetangSynchronizer(syncStore, reader1).RunAsync(catalogOnly: true);
        Check(catalogOnly.CourseListCovered && catalogOnly.Tasks == 0 && reader1.Requests.Count == 6 && syncStore.LoadTasks().Count == 5,
            "catalog-only refresh leaves tasks untouched");
        syncStore.RecordCourseCatalogResult("xuetang", "partial", ["XT006"], Metadata.Now);
        Check(FixtureTaskStore.Create(syncStore.DatabasePath).LoadCourseRecording("xuetang").LastCatalogRefresh!.ProblemCodes.Single() == "XT006"
            && syncStore.LoadXuetangSettings().LastSyncOutcome == "completed", "catalog failure persistence keeps prior task sync");
        var expired = new Reader(_ => new("login_required", 200));
        var rejected = await new XuetangSynchronizer(syncStore, expired).RunAsync();
        Check(rejected.LoginRequired && !rejected.CourseListCovered && rejected.Problems.Single().Code == "XT001" && syncStore.LoadTasks().Count == 5,
            "expired login retains history and flags unknown coverage");
        var unknown = new Reader(uri => uri.AbsolutePath == XuetangPlatform.Courses ? Metadata.Ok(Metadata.Envelope(new { unexpected = new object[0] })) : Metadata.Respond(uri));
        Check(!(await new XuetangSynchronizer(syncStore, unknown).RunAsync()).CourseListCovered, "unknown catalog never marked complete");
        var loop = new Reader(uri => uri.AbsolutePath == XuetangPlatform.Courses && RainAddressPolicy.Query(uri)!.GetValueOrDefault("status") == "1"
            ? Metadata.Ok(Metadata.Catalog([Metadata.Course()])) : Metadata.Respond(uri));
        var loopSummary = await new XuetangSynchronizer(syncStore, loop).RunAsync(catalogOnly: true);
        Check(!loopSummary.CourseListCovered && loopSummary.Problems.Any(problem => problem.Code == "XT005"), "repeated page explicitly incomplete");
        var paging = new Reader(uri =>
        {
            var fields = RainAddressPolicy.Query(uri)!;
            return uri.AbsolutePath == XuetangPlatform.Courses && fields.GetValueOrDefault("status") == "1"
                ? Metadata.Ok(fields["page"] == "1" ? Metadata.Catalog([Metadata.Course()], 2) : Metadata.Catalog([Metadata.Course("102")], 2)) : Metadata.Respond(uri);
        });
        var paged = await new XuetangSynchronizer(FixtureTaskStore.Create(Path.Combine(root, "paging.db")), paging).RunAsync();
        Check(paged.CourseListCovered && paged.Courses == 2 && paged.CoursesRead == 2 && paged.Tasks == 10, "count-driven course pagination complete");
        var unlimited = new Reader(uri =>
        {
            var fields = RainAddressPolicy.Query(uri)!;
            return uri.AbsolutePath == XuetangPlatform.Courses && fields.GetValueOrDefault("status") == "1"
                ? Metadata.Ok(Metadata.Catalog([Metadata.Course((1000 + int.Parse(fields["page"])).ToString())])) : Metadata.Respond(uri);
        });
        var limited = await new XuetangSynchronizer(FixtureTaskStore.Create(Path.Combine(root, "limit.db")), unlimited).RunAsync(catalogOnly: true);
        Check(!limited.CourseListCovered && limited.Problems.Any(problem => problem.Code == "XT005") && limited.CatalogPages <= 104,
            "page ceiling never interpreted as no remaining tasks");
        var rate = new Reader(uri => uri.AbsolutePath == XuetangPlatform.Courses ? new("rate_limited", 429) : Metadata.Respond(uri));
        var rateSummary = await new XuetangSynchronizer(syncStore, rate).RunAsync();
        Check(!rateSummary.CourseListCovered && rate.Requests.Count == 3 && syncStore.LoadTasks().Count == 5, "rate limit stops all following requests");
        var missingReset = new Reader(uri => uri.AbsolutePath == XuetangPlatform.ResetDeadlines ? new("network_error", null) : Metadata.Respond(uri));
        var missing = await new XuetangSynchronizer(FixtureTaskStore.Create(Path.Combine(root, "reset.db")), missingReset).RunAsync();
        Check(missing.Tasks == 5 && missing.TasksWithDeadline == 0 && missing.CoursesRead == 0 && missing.Problems.Any(problem => problem.Code == "XT006"),
            "unread extension metadata retained with reviewable deadline limitation");
        var changedStore = FixtureTaskStore.Create(Path.Combine(root, "changed.db")); var switched = false;
        var changedReader = new Reader(uri =>
        {
            if (uri.AbsolutePath == XuetangPlatform.Chapter && !switched)
            { switched = true; var current = changedStore.LoadCourseRecording("xuetang"); changedStore.SaveCourseRecording("xuetang", false, [], true, current.Revision, Metadata.Now); }
            return Metadata.Respond(uri);
        });
        await Cancelled(() => new XuetangSynchronizer(changedStore, changedReader).RunAsync(), "selection revision cancels stale import");
        Check(changedStore.LoadTasks().Count == 0, "no stale tasks imported after selection change");
        var sessionStore = FixtureTaskStore.Create(Path.Combine(root, "session-change.db")); var ready = false;
        var sessionReader = new Reader(uri => { if (uri.AbsolutePath == XuetangPlatform.ResetDeadlines) ready = true; return Metadata.Respond(uri); });
        await Cancelled(() => new XuetangSynchronizer(sessionStore, sessionReader, _ => ready ? throw new OperationCanceledException() : Task.CompletedTask).RunAsync(), "changed session checked before writes");
        Check(sessionStore.LoadTasks().Count == 0, "account switch never imports old response");
    }

    private static async Task HttpChecks()
    {
        var cookie = new XuetangSessionCookie("csrftoken", "synthetic-csrf", ".xuetangx.com", "/", true, false, null, "Lax");
        var requests = new List<HttpRequestMessage>(); var renewed = 0;
        using var reader = new XuetangHttpReader((_, _) => Task.FromResult<IReadOnlyList<XuetangSessionCookie>>([cookie,
            cookie with { Name = "wrong", Domain = "other.example" }, cookie with { Name = "expired", ExpiresUtc = DateTimeOffset.UtcNow.AddHours(-1) }]),
            new Handler((request, _) =>
            {
                requests.Add(request);
                Check(request.Method == HttpMethod.Get && request.Content is null && XuetangPlatform.IsReadAddress(request.RequestUri!), "GET-only exact metadata requests");
                Check(request.Headers.GetValues("xtbz").Single() == "xt" && request.Headers.GetValues("x-client").Single() == "web"
                    && request.Headers.GetValues("app-name").Single() == "xtzx" && request.Headers.GetValues("terminal-type").Single() == "web", "official main-site request headers");
                var response = Metadata.Json(Metadata.Login());
                if (requests.Count == 1) response.Headers.TryAddWithoutValidation("Set-Cookie", "csrftoken=synthetic-renewed; Domain=.xuetangx.com; Path=/; Secure; SameSite=Lax");
                return Task.FromResult(response);
            }), (_, _, values, _) => { renewed += values.Count; return Task.CompletedTask; });
        Check((await reader.ReadAsync(XuetangPlatform.Api(XuetangPlatform.LoginCheck), default)).Outcome == "ok", "official login read transport");
        Check((await reader.ReadAsync(XuetangPlatform.Api(XuetangPlatform.LoginCheck), default)).Outcome == "ok" && renewed == 1, "cookie renewals accepted");
        Check(requests[0].Headers.GetValues("Cookie").Single() == "csrftoken=synthetic-csrf" && requests[1].Headers.GetValues("X-CSRFToken").Single() == "synthetic-renewed", "only scoped fresh cookies and renewed CSRF sent");
        foreach (var uri in new[] { new Uri("https://evil.example/api/v1/u/login/check_is_l/"), new Uri("http://www.xuetangx.com/api/v1/u/login/check_is_l/"),
            XuetangPlatform.Api("/api/v1/lms/exercise/answer_apply/"), XuetangPlatform.Api("/api/v1/lms/learn/leaf_info/101/201/"),
            XuetangPlatform.Api(XuetangPlatform.Courses, ("status", "1"), ("page", "101")), XuetangPlatform.Api(XuetangPlatform.Chapter, ("cid", "101"), ("sign", "product101"), ("show_unpublished", "1")),
            new Uri("https://www.xuetangx.com/api/v1/u/login/check_is_l/?x=1&x=2") })
            Check((await reader.ReadAsync(uri, default)).Outcome == "unsupported_request", "non-metadata action cannot send cookies");
        Check(requests.Count == 2, "rejected addresses have zero network requests");
        foreach (var pair in new[] { (HttpStatusCode.Unauthorized, "login_required"), (HttpStatusCode.Forbidden, "forbidden"), (HttpStatusCode.InternalServerError, "http_error") })
        {
            using var http = Transport(_ => Metadata.Json("{}", pair.Item1));
            Check((await http.ReadAsync(XuetangPlatform.Api(XuetangPlatform.Profile), default)).Outcome == pair.Item2, "HTTP failure distinct from empty catalog");
        }
        var sent = 0; using (var limited = Transport(_ => { sent++; return Metadata.Json("{}", HttpStatusCode.TooManyRequests); }))
        { await limited.ReadAsync(XuetangPlatform.Api(XuetangPlatform.Profile), default); await limited.ReadAsync(XuetangPlatform.Api(XuetangPlatform.Profile), default); Check(sent == 1, "HTTP rate-limit latch stops subsequent sends"); }
        using (var redirected = Transport(_ => { var response = Metadata.Json("", HttpStatusCode.Found); response.Headers.Location = new Uri("https://www.xuetangx.com/login"); return response; }))
            Check((await redirected.ReadAsync(XuetangPlatform.Api(XuetangPlatform.Profile), default)).Outcome == "login_required", "official auth redirect reported");
        using (var redirected = Transport(_ => { var response = Metadata.Json("", HttpStatusCode.Found); response.Headers.Location = new("https://evil.example/login"); return response; }))
            Check((await redirected.ReadAsync(XuetangPlatform.Api(XuetangPlatform.Profile), default)).Outcome == "unsupported_redirect", "external redirect never followed");
        using (var large = Transport(_ => Metadata.Json(new string('a', XuetangParser.MaximumBytes + 1))))
            Check((await large.ReadAsync(XuetangPlatform.Api(XuetangPlatform.Profile), default)).Outcome == "response_too_large", "oversized content bounded");
        using (var unknown = Transport(_ => Metadata.Json("<html>logged out</html>")))
            Check((await unknown.ReadAsync(XuetangPlatform.Api(XuetangPlatform.Profile), default)).Outcome == "invalid_json", "HTML page not empty metadata");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(() => reader.ReadAsync(XuetangPlatform.Api(XuetangPlatform.LoginCheck), cancelled.Token), "cancelled HTTP no metadata outcome fabricated");

        XuetangHttpReader Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) => new((_, _) => Task.FromResult<IReadOnlyList<XuetangSessionCookie>>([]),
            new Handler((request, _) => Task.FromResult(respond(request))));
    }

    private static void SessionChecks()
    {
        var now = DateTimeOffset.UtcNow;
        var cookie = new XuetangSessionCookie("sessionid", "synthetic-protected-value", ".xuetangx.com", "/", true, true, null, "Strict");
        Check(XuetangSessionStore.IsValid(cookie) && cookie.AppliesTo(XuetangPlatform.Home), "official main-site session scope");
        foreach (var invalid in new[] { cookie with { Domain = ".yuketang.cn" }, cookie with { Domain = ".xuetangx.com.evil.example" },
            cookie with { Value = "test\r\nheader" }, cookie with { Name = "invalid;name" }, cookie with { SameSite = "other" } })
            Check(!XuetangSessionStore.IsValid(invalid), "invalid archive cookie excluded");
        Check(XuetangSessionStore.MissingForRestore([cookie], [cookie with { Value = "new-login" }]).Count == 0, "new profile login preferred over archive");
        Check(XuetangSessionStore.MissingForRestore([cookie], []).Count == 1, "missing temporary session restored");
        var header = "sessionid=renewed; Domain=.xuetangx.com; Path=/; Secure; HttpOnly; SameSite=Strict, deleted=; Domain=.xuetangx.com; Path=/; Max-Age=0";
        var parsed = XuetangResponseCookies.Parse(XuetangPlatform.Home, [header, "bad=x; Domain=.evil.example; Path=/"], now);
        Check(parsed.Count == 2 && parsed.Single(value => value.Name == "deleted").IsExpired(now), "combined cookie renewal and deletion parsed with domain checks");
        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Path.GetTempPath(), "xuetang-session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var path = Path.Combine(root, "session.bin"); var archive = new XuetangSessionStore(path);
            archive.Save([cookie, cookie with { Name = "expired", ExpiresUtc = now.AddHours(-1) }], now);
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(cookie.Value, StringComparison.Ordinal), "DPAPI archive encrypts session credentials");
            Check(archive.Load(now).Single() == cookie && archive.Load(now).Single().ExpiresUtc is null, "archive restore preserves temporary expiry and flags");
            Check(archive.Load(now.AddDays(31)).Count == 0 && archive.LastLoadOutcome == "too_old", "stale encrypted snapshot not reused");
            archive.Clear(); Check(archive.Load(now).Count == 0, "explicit clear removes archive");
            File.WriteAllText(path, "synthetic-corrupt"); Check(archive.Load(now).Count == 0 && archive.LastLoadOutcome == "unavailable", "corrupt protected archive reported");
        }
    }

    private sealed class Reader(Func<Uri, XuetangResponse> respond) : IXuetangMetadataReader
    {
        public List<Uri> Requests { get; } = [];
        public Task<XuetangResponse> ReadAsync(Uri address, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Requests.Add(address); return Task.FromResult(respond(address)); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken); }
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("Xuetang: " + label); checks++; }
    private static void Reject(Action action, string code)
    { try { action(); throw new InvalidOperationException("Expected validation rejection."); } catch (TaskValidationException error) { Check(error.Code == code, "expected validation code"); } }
    private static async Task Rejected(Func<Task> action, string code, string label)
    { try { await action(); throw new InvalidOperationException("Expected validation rejection."); } catch (TaskValidationException error) { Check(error.Code == code, label); } }
    private static async Task Cancelled(Func<Task> action, string label)
    { try { await action(); throw new InvalidOperationException("Expected cancellation."); } catch (OperationCanceledException) { Check(true, label); } }
}
