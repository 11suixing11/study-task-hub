using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessProbe;
using Microsoft.Data.Sqlite;

internal static class RainClassroomChecks
{
    private static int checks;
    private const string Catalog = """
        {"errcode":0,"data":{"list":[
          {"id":101,"name":"一班","course":{"id":900,"name":"线性代数"},"teacher":{"name":"教师"}},
          {"id":102,"name":"二班","course":{"id":901,"name":"程序设计"},"is_pro":true},
          {"id":103,"name":"三班","course_name":"写作"}
        ]}}
        """;
    private const string Chapter = """
        {"success":true,"data":{"course_chapter":[{"id":201,"name":"第一章","section_leaf_list":[
          {"id":501,"name":"第一份作业","leaf_type":6,"chapter_id":201,"score_deadline":1793001599000.0,"start_time":1792832407000},
          {"id":202,"name":"第二节","leaf_list":[
            {"id":502,"name":"第二份作业","leaf_type":6,"score_deadline":1793010000},
            {"id":503,"name":"期中考试","leaf_type":5,"chapter_id":201,"score_deadline":"2026-10-27 23:59:00"},
            {"id":504,"name":"未发布作业","leaf_type":6,"is_locked":true},
            {"id":505,"name":"视频","leaf_type":0}
          ]}
        ]}]}}
        """;
    private const string Schedule = """
        {"success":true,"data":{"leaf_schedules":{
          "501":{"done":2,"total":4},
          "502":{"done":3,"total":3},
          "503":{"done":4,"total":10,"status":3,"score":81},
          "999":{"done":0,"total":1}
        }}}
        """;
    private const string EmptyActivities = """{"errcode":0,"data":{"activities":[],"has_more":false}}""";

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rain-business-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var room = new RainClassroom("一班", "線性代数", "101", "900", "SYNTHETIC-SIGN", "19", "2", true);
        var schedule = RainClassroomParser.Schedule(Schedule);
        Check(schedule.Outcome == "ok" && schedule.Leaves.Count == 4);
        foreach (var platform in RainPlatform.All)
        {
            var batch = RainClassroomParser.Chapter(Chapter, platform, platform.DefaultHost, "77", room, schedule);
            Check(batch.Outcome == "ok" && batch.Tasks.Count == 3 && batch.Issues.Count == 0);
            Check(batch.Tasks.Single(task => task.RemoteId == "501").State == TaskState.Pending);
            Check(batch.Tasks.Single(task => task.RemoteId == "501").ProgressPercent == 50);
            var complete = batch.Tasks.Single(task => task.RemoteId == "502");
            Check(complete.State == TaskState.Completed && complete.StateEvidence == "platform_progress");
            Check(complete.PlatformStatusText.Contains("平台题目进度", StringComparison.Ordinal)
                && complete.StatusExplanation.Contains("提交记录", StringComparison.Ordinal));
            var exam = batch.Tasks.Single(task => task.RemoteId == "503");
            Check(exam.State == TaskState.Completed && exam.StateEvidence == "explicit" && exam.ProgressPercent == 40 && exam.ScoreText == "81");
            Check(exam.DeadlineUtc == new DateTimeOffset(2026, 10, 27, 15, 59, 0, TimeSpan.Zero));
            Check(batch.Tasks.All(task => task.Link!.StartsWith("https://" + platform.DefaultHost + "/ai-workspace/lms-graph/101/", StringComparison.Ordinal)));
            Check(batch.Tasks.All(task => task.Link!.Contains("node_id=201", StringComparison.Ordinal)));
            Check(batch.Tasks.All(task => RainTaskNavigation.IsOfficialTaskLink(platform.Id, task.Link!, task.ClassroomId, task.RemoteId, task.Kind)));
            var host = platform.DefaultHost;
            Check(RainAddressPolicy.IsReadAddress(platform, host, RainAddressPolicy.Api(host, "/v2/api/web/courses/list", ("identity", "2"))));
            foreach (var path in new[] { "/mooc-api/v1/lms/learn/leaf_info/101/501/", "/mooc-api/v1/lms/exercise/problem_apply/", "/v2/api/web/courses/del", "/ai-workspace/lms-graph/101/exercise/501" })
                Check(!RainAddressPolicy.IsReadAddress(platform, host, RainAddressPolicy.Api(host, path)));
            foreach (var address in new[]
            {
                "http://" + host + "/v2/api/web/userinfo",
                "https://" + host + ".example.invalid/v2/api/web/userinfo",
                "https://user@" + host + "/v2/api/web/userinfo",
                "https://" + host + "/v2/api/web/courses/list?identity=1",
                "https://" + host + "/v2/api/web/courses/list?identity=2&identity=2",
                "https://" + host + "/v2/api/web/courses/list?identity=2&redirect_url=1",
                "https://" + host + "/v2/api/web/userinfo#fragment",
                "https://" + host + "/v2/api/web/classrooms/101?role=1",
                "https://" + host + "/mooc-api/v1/lms/learn/course/chapter?cid=101&classroom_id=999&show_unpublished=0"
            }) Check(!RainAddressPolicy.IsReadAddress(platform, host, new(address)));
            Check(!RainTaskNavigation.IsOfficialTaskLink(platform.Id, batch.Tasks[0].Link!, "999", "501", "exercise"));
            Check(!RainTaskNavigation.IsOfficialTaskLink(platform.Id, batch.Tasks[0].Link!, "101", "999", "exercise"));
            Check(RainTaskNavigation.Key(platform.Id, batch.Tasks[0].Link!.Replace("https://", "http://", StringComparison.Ordinal)) is null);
            Check(RainTaskNavigation.Key(platform.Id, batch.Tasks[0].Link! + "&callback=1") is null);
        }
        var catalog = RainClassroomParser.Catalog(Catalog);
        Check(catalog.Outcome == "ok" && catalog.Covered && catalog.Courses.Count == 3);
        Check(catalog.Courses[0].Id == "101" && catalog.Courses[0].CourseId == "900" && catalog.Courses[0].CourseName == "线性代数");
        Check(RainClassroomParser.Catalog("""{"errcode":0,"data":{"list":[]}}""") is { Outcome: "ok", Covered: true, Courses.Count: 0 });
        Check(RainClassroomParser.Catalog("""{"errcode":0,"data":{"something":[]}}""").Covered == false);
        Check(RainClassroomParser.Catalog("""{"errcode":0,"data":{"list":[{"id":201,"name":"章节","leaf_type":6}]}}""") is { Covered: false, Courses.Count: 0 });
        var grouped = RainClassroomParser.Catalog("""{"errcode":0,"data":{"list":[{"id":900,"name":"课程","classrooms":[{"id":101,"name":"一班"},{"id":102,"name":"二班"}]}]}}""");
        Check(grouped.Covered && grouped.Courses.Count == 2 && grouped.Courses.All(course => course.CourseName == "课程"));
        Check(RainClassroomParser.AccountId("""{"errcode":0,"data":[{"user_id":77,"phone":"never persist"}]}""") == "77");
        Check(RainClassroomParser.AccountId("""{"errcode":0,"data":[{"user_id":77},{"user_id":78}]}""") is null);
        Check(RainClassroomParser.AccountId("""{"errcode":0,"data":{"id":77}}""") is null);
        foreach (var payload in new[]
        {
            """{"errcode":400000,"errmsg":"缺少必要字段identity","data":{"list":[]}}""",
            """{"success":false,"error_code":20002,"data":{"course_chapter":[]}}""",
            """{"success":true,"error_code":99,"data":{"list":[]}}""",
            """{"code":1,"data":{"list":[]}}"""
        }) Check(RainClassroomParser.EnvelopeOutcome(payload) == "api_error");
        Check(RainClassroomParser.EnvelopeOutcome("""{"errcode":1,"errmsg":"请登录"}""") == "login_required");
        Check(RainClassroomParser.EnvelopeOutcome("""{"data":{"list":[]}}""") == "unrecognized_response");
        Check(RainClassroomParser.EnvelopeOutcome("not-json") == "invalid_json");
        Check(RainClassroomParser.Schedule("""{"success":true,"data":{}}""").Outcome == "unrecognized_schedule");
        Check(RainClassroomParser.Chapter("""{"success":true,"data":{}}""", RainPlatform.Rain, "www.yuketang.cn", "77", room, schedule).Outcome == "unrecognized_chapter");
        Check(RainClassroomParser.Activities("""{"errcode":0,"data":{"activities":[]}}""", RainPlatform.Rain, "www.yuketang.cn", "77", room, schedule).Outcome == "unrecognized_activities");
        var malformed = RainClassroomParser.Chapter(Chapter.Replace("\"leaf_list\":[", "\"leaf_list\":null,\"unused\":[", StringComparison.Ordinal),
            RainPlatform.Rain, "www.yuketang.cn", "77", room, schedule);
        Check(malformed.Outcome == "partial_tasks" && malformed.Issues.Contains("tree_shape") && malformed.Tasks.Count == 1);
        var unknown = RainClassroomParser.Chapter(Chapter, RainPlatform.Rain, "www.yuketang.cn", "77", room,
            RainClassroomParser.Schedule("""{"success":true,"data":{"leaf_schedules":{}}}"""));
        Check(unknown.Tasks.All(task => task.State is null && task.StateEvidence == "unknown"));
        var forced = RainClassroomParser.Chapter(Chapter, RainPlatform.Rain, "www.yuketang.cn", "77", room,
            RainClassroomParser.Schedule("""{"success":true,"data":{"leaf_schedules":{"501":{"done":2,"total":4,"is_submitted":true}}}}"""));
        Check(forced.Tasks.Single(task => task.RemoteId == "501") is { State: TaskState.Completed, StateEvidence: "explicit", ProgressPercent: 50 });
        // Numeric exercise status 3 belongs to exams and does not prove an exercise is complete.
        var exerciseStatus = RainClassroomParser.Chapter(Chapter, RainPlatform.Rain, "www.yuketang.cn", "77", room,
            RainClassroomParser.Schedule("""{"success":true,"data":{"leaf_schedules":{"501":{"status":3}}}}"""));
        Check(exerciseStatus.Tasks.Single(task => task.RemoteId == "501").State is null);
        foreach (var time in new[] { "1793010000", "1793010000000", "1793010000000.0", "\"1793010000\"" })
        {
            using var parsed = JsonDocument.Parse(time);
            Check(RainClassroomParser.Time(parsed.RootElement) == DateTimeOffset.FromUnixTimeSeconds(1793010000));
        }
        foreach (var time in new[] { "0", "-1", "9999999999999999999999", "\"10-25 23:59\"", "\"invalid\"", "null" })
        {
            using var parsed = JsonDocument.Parse(time); Check(RainClassroomParser.Time(parsed.RootElement) is null);
        }
        using (var parsed = JsonDocument.Parse("\"2026-10-27T23:59:00+08:00\""))
            Check(RainClassroomParser.Time(parsed.RootElement) == new DateTimeOffset(2026, 10, 27, 15, 59, 0, TimeSpan.Zero));
        await StoreAndNavigationAsync(root, now, room, schedule);
        await TransportAsync();
        await SynchronizationAsync(root);
        Console.WriteLine($"PASS: {checks} rain/changjiang metadata, isolated storage, paging, transport and direct-link checks.");
    }

    private static async Task StoreAndNavigationAsync(string root, DateTimeOffset now, RainClassroom room, RainSchedule schedule)
    {
        var path = Path.Combine(root, "store.db");
        var protector = new FixtureProtector();
        var store = new TaskStore(path, protector);
        store.SaveSettings(new(false, true) { LastSyncOutcome = "chaoxing-existing", LastSyncAtUtc = now });
        var rain = RainClassroomParser.Chapter(Chapter, RainPlatform.Rain, "www.yuketang.cn", "77", room, schedule).Tasks;
        var changjiang = RainClassroomParser.Chapter(Chapter, RainPlatform.Changjiang, "changjiang.yuketang.cn", "77", room, schedule).Tasks;
        Check(store.ImportCollectedTasks(rain, now).Added == 3);
        Check(store.ImportCollectedTasks(changjiang, now).Added == 3 && store.LoadTasks().Count == 6);
        Check(store.ImportCollectedTasks(rain, now.AddMinutes(1)).Updated == 3 && store.LoadTasks().Count == 6);
        Check(store.ImportCollectedTasks([rain[0], rain[0]], now).DuplicateRows == 1);
        Check(store.ImportCollectedTasks([rain[0] with { AccountId = "78" }], now).Added == 1);
        Check(store.ImportCollectedTasks([rain[0] with { ClassroomId = "102", Link = rain[0].Link!.Replace("/101/", "/102/", StringComparison.Ordinal) }], now).Added == 1);
        Check(TaskStore.CollectedTaskKey(rain[0]) != TaskStore.CollectedTaskKey(rain[0] with { Kind = "exam" }));
        Check(TaskStore.CollectedTaskKey(rain[0]) == TaskStore.CollectedTaskKey(rain[0] with { Host = "WWW.YUKETANG.CN" }));
        var item = store.LoadTasks().Single(task => task.SourceKey == TaskStore.CollectedTaskKey(rain[0]));
        Check(TaskRules.IsCollected(item) && TaskRules.HasTrustedDeadline(item) && item.CollectedNavigationKey.Length == 64);
        Check(TaskRules.Confidence(item).Contains("雨课堂", StringComparison.Ordinal));
        Check(TaskRules.Priority(item with { DeadlineUtc = now.AddHours(1) }, now) == 1);
        Check(TaskRules.Priority(item with { DeadlineUtc = now.AddHours(10) }, now) == 2);
        Check(TaskRules.Priority(item with { DeadlineUtc = now.AddDays(3) }, now) == 3);
        Check(TaskRules.Priority(item with { DeadlineUtc = now.AddSeconds(-1) }, now) == 0);
        Check(TaskRules.Priority(item with { DeadlineUtc = null, DeadlineKind = DeadlineKind.Unknown }, now) == 4);
        item = store.SetState(item.Id, item.Revision, TaskState.Ignored, now);
        Check(item.Overrides.HasFlag(TaskOverrides.State));
        var userDraft = new TaskDraft("我的作业名称", item.PlatformId, "我的课程", DeadlineKind.Known,
            item.BeginUtc, now.AddDays(5), item.Link, 88, "手动分数", "用户备注", 2);
        item = store.Edit(item.Id, item.Revision, userDraft, now);
        store.ImportCollectedTasks([rain[0] with { Title = "平台名称已更新", State = TaskState.Completed,
            StateEvidence = "explicit", PlatformStatusText = "已提交", ProgressPercent = 100, ScoreText = "99", DeadlineUtc = now.AddDays(1) }], now.AddMinutes(2));
        item = store.Get(item.Id)!;
        Check(item.Title == "我的作业名称" && item.CourseName == "我的课程" && item.State == TaskState.Ignored);
        Check(item.DeadlineUtc == userDraft.DeadlineUtc && item.ProgressPercent == 88 && item.ScoreText == "手动分数" && item.Notes == "用户备注" && item.PriorityOverride == 2);
        Check(item.PlatformTitle == "平台名称已更新" && item.PlatformStatusText == "已提交");
        Reject(() => store.Edit(item.Id, item.Revision, userDraft with { PlatformId = "chaoxing" }, now), "TASK001");
        var before = store.LoadTasks().Count;
        Reject(() => store.ImportCollectedTasks([rain[0] with { RemoteId = "888", Link = rain[0].Link }, rain[1]], now), "RAIN003");
        Check(store.LoadTasks().Count == before);
        Reject(() => store.ImportCollectedTasks([rain[0] with { AccountId = "" }], now), "RAIN003");
        var connection = store.LoadPlatformSettings("yuketang");
        Check(!connection.Connected && connection.Host == "www.yuketang.cn");
        store.SavePlatformSettings("yuketang", connection with { Connected = true, AutomaticSyncEnabled = false });
        var summary = new RainSyncSummary("yuketang", 3, 3, 3, 3, 0, false, true, []);
        store.RecordPlatformSyncResult(summary, "completed", now);
        Check(store.LoadPlatformSettings("yuketang") is { Connected: true, AutomaticSyncEnabled: false, LastSyncOutcome: "completed" });
        Check(store.LoadPlatformSettings("changjiang").LastSyncAtUtc is null);
        Check(store.LoadSettings().LastSyncOutcome == "chaoxing-existing" && store.LoadSettings().LastSyncAtUtc == now);
        Reject(() => store.SavePlatformSettings("changjiang", new() { Host = "www.yuketang.cn" }), "RAIN003");
        var reopened = new TaskStore(path, protector);
        Check(reopened.LoadTasks().Count == before && reopened.LoadPlatformSettings("yuketang").LastSummary?.Tasks == 3);
        Check(!JsonSerializer.Serialize(rain[0]).Contains("SYNTHETIC-SIGN", StringComparison.Ordinal));
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("/ai-workspace/lms-graph/", StringComparison.Ordinal));
        Check(store.RefreshAutomaticMetadata(now) == 0);
        var opened = new List<string>(); var resolvedCalls = 0;
        var navigation = new TaskNavigationService(store, (_, _) => { resolvedCalls++; throw new InvalidOperationException("Rain must not invoke Chaoxing resolver."); }, opened.Add);
        var clicked = await navigation.OpenAsync(item.Id, CancellationToken.None);
        Check(clicked.Outcome == "opened" && clicked.Address == item.Link && opened.Single() == item.Link && resolvedCalls == 0);
        // Corrupt the saved URI without its binding: wrong task must not be opened.
        using (var connectionDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connectionDb.Open(); using var command = connectionDb.CreateCommand();
            command.CommandText = "UPDATE tasks SET protected_link=$link WHERE id=$id;";
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$link", protector.Protect(item.Link!.Replace("/501", "/999", StringComparison.Ordinal)));
            command.ExecuteNonQuery();
        }
        try { await navigation.OpenAsync(item.Id, CancellationToken.None); throw new InvalidOperationException("Mismatched task opened."); }
        catch (TaskValidationException error) { Check(error.Code == "LINK003" && opened.Count == 1); }
    }

    private static async Task TransportAsync()
    {
        var cookieReads = 0;
        var handler = new FixtureHandler(request =>
        {
            Check(request.Method == HttpMethod.Get && request.Headers.GetValues("XTBZ").Single() == "ykt");
            Check(request.Headers.GetValues("university-id").Single() == "19");
            Check(request.Headers.GetValues("X-CSRFToken").Single() == "SYNTHETIC-CSRF");
            return Json("""{"errcode":0,"data":{"list":[]}}""");
        });
        using (var reader = new RainHttpReader(RainPlatform.Rain, "www.yuketang.cn", (_, token) =>
        {
            token.ThrowIfCancellationRequested(); cookieReads++;
            return Task.FromResult<IReadOnlyList<Cookie>>([new("university_id", "19", "/", "www.yuketang.cn"), new("csrftoken", "SYNTHETIC-CSRF", "/", "www.yuketang.cn")]);
        }, handler))
        {
            Check((await reader.ReadAsync(new("https://www.yuketang.cn/v2/api/web/courses/list?identity=2"), CancellationToken.None)).Outcome == "ok");
            Check((await reader.ReadAsync(new("https://changjiang.yuketang.cn/v2/api/web/userinfo"), CancellationToken.None)).Outcome == "unsupported_address");
            Check(handler.Requests == 1 && cookieReads == 1);
        }
        foreach (var (status, outcome) in new[] { (HttpStatusCode.Unauthorized, "login_required"), (HttpStatusCode.Forbidden, "forbidden"),
            (HttpStatusCode.TooManyRequests, "rate_limited"), (HttpStatusCode.InternalServerError, "http_error") })
        {
            using var reader = Reader(new FixtureHandler(_ => new(status)));
            Check((await reader.ReadAsync(new("https://www.yuketang.cn/v2/api/web/userinfo"), CancellationToken.None)).Outcome == outcome);
        }
        foreach (var target in new[] { "https://www.yuketang.cn/web", "https://example.invalid/web", "https://changjiang.yuketang.cn/web" })
        {
            var redirectHandler = new FixtureHandler(_ => new(HttpStatusCode.Found) { Headers = { Location = new(target) } });
            using var reader = Reader(redirectHandler);
            var result = await reader.ReadAsync(new("https://www.yuketang.cn/v2/api/web/userinfo"), CancellationToken.None);
            Check(result.Outcome == (target == "https://www.yuketang.cn/web" ? "login_required" : "unsupported_redirect") && redirectHandler.Requests == 1);
        }
        using (var reader = Reader(new FixtureHandler(_ => Json("""{"errcode":99,"errmsg":"SYNTHETIC-SECRET"}"""))))
        {
            var response = await reader.ReadAsync(new("https://www.yuketang.cn/v2/api/web/userinfo"), CancellationToken.None);
            Check(response.Outcome == "api_error" && response.Json.Length == 0 && !JsonSerializer.Serialize(response).Contains("SYNTHETIC-SECRET", StringComparison.Ordinal));
        }
        using (var reader = Reader(new FixtureHandler(_ => throw new HttpRequestException("SYNTHETIC-SECRET"))))
            Check((await reader.ReadAsync(new("https://www.yuketang.cn/v2/api/web/userinfo"), CancellationToken.None)).Outcome == "network_error");
        using (var reader = Reader(new FixtureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") })))
            Check((await reader.ReadAsync(new("https://www.yuketang.cn/v2/api/web/userinfo"), CancellationToken.None)).Outcome == "unexpected_content");
        var limited = new FixtureHandler(_ => new(HttpStatusCode.TooManyRequests));
        using (var reader = Reader(limited))
        {
            Check((await reader.ReadAsync(new("https://www.yuketang.cn/v2/api/web/userinfo"), CancellationToken.None)).Outcome == "rate_limited");
            Check((await reader.ReadAsync(new("https://www.yuketang.cn/v2/api/web/courses/list?identity=2"), CancellationToken.None)).Outcome == "rate_limited");
            Check(limited.Requests == 1);
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using (var reader = Reader(new FixtureHandler(_ => throw new InvalidOperationException("Canceled request sent."))))
        {
            try { await reader.ReadAsync(new("https://www.yuketang.cn/v2/api/web/userinfo"), canceled.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { Check(true); }
        }
    }

    private static async Task SynchronizationAsync(string root)
    {
        foreach (var platform in RainPlatform.All)
        {
            var store = new TaskStore(Path.Combine(root, platform.Id + "-sync.db"), new FixtureProtector());
            var pages = new List<string>();
            var handler = new FixtureHandler(request =>
            {
                var address = request.RequestUri!;
                Check(RainAddressPolicy.IsReadAddress(platform, platform.DefaultHost, address));
                Check(request.Method == HttpMethod.Get && address.IdnHost == platform.DefaultHost);
                pages.Add(address.AbsolutePath + address.Query);
                if (address.AbsolutePath == "/v2/api/web/userinfo") return Json("""{"errcode":0,"data":[{"user_id":77}]}""");
                if (address.AbsolutePath == "/v2/api/web/courses/list") return Json(Catalog);
                if (address.AbsolutePath.StartsWith("/v2/api/web/classrooms/", StringComparison.Ordinal))
                {
                    var id = address.AbsolutePath.Split('/').Last();
                    return Json("{\"errcode\":0,\"data\":{\"id\":" + id + ",\"name\":\"班级\",\"course_name\":\"课程" + id + "\""
                        + (id == "102" ? ",\"course_sign\":\"SYNTHETIC-SIGN\",\"university_id\":19" : "") + "}}");
                }
                if (address.AbsolutePath.EndsWith("/schedule", StringComparison.Ordinal)) return Json(Schedule);
                if (address.AbsolutePath.EndsWith("/chapter", StringComparison.Ordinal)) return Json(Chapter);
                var fields = RainAddressPolicy.Query(address)!;
                if (address.AbsolutePath.EndsWith("/101", StringComparison.Ordinal))
                    return fields["page"] == "0" ? Json("""{"errcode":0,"data":{"has_more":true,"activities":[{"type":4,"courseware_id":601,"title":"旧版测验","deadline":1793010000000,"status":2,"progress":3,"problem_count":4}]}}""") : Json(EmptyActivities);
                return Json(EmptyActivities);
            });
            using var reader = new RainHttpReader(platform, platform.DefaultHost, (_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), handler);
            var first = await new RainSynchronizer(store, reader, platform, platform.DefaultHost).RunAsync();
            Check(first is { Courses: 3, CoursesRead: 3, Tasks: 4, Added: 4, Updated: 0, LoginRequired: false, CourseListCovered: true, Problems.Count: 0 });
            Check(first.TasksWithDeadline == 4 && first.TasksWithLink == 4 && first.StatusNeedsReview == 0);
            Check(store.LoadTasks().Count == 4 && store.LoadTasks().Count(task => task.State == TaskState.Completed) == 3);
            var legacy = store.LoadTasks().Single(task => task.Title == "旧版测验");
            Check(legacy.PlatformStateEvidence == "explicit" && legacy.ProgressPercent == 75 && legacy.Link!.Contains("/quizSummary/101/601", StringComparison.Ordinal));
            Check(pages.Count(page => page.StartsWith("/v2/api/web/logs/learn/101", StringComparison.Ordinal)) == 2);
            var second = await new RainSynchronizer(store, reader, platform, platform.DefaultHost).RunAsync();
            Check(second.Added == 0 && second.Updated == 4 && store.LoadTasks().Count == 4);
        }
        var isolated = new TaskStore(Path.Combine(root, "failures.db"), new FixtureProtector());
        using (var reader = Reader(new FixtureHandler(_ => new(HttpStatusCode.Unauthorized))))
        {
            var result = await new RainSynchronizer(isolated, reader, RainPlatform.Rain, "www.yuketang.cn").RunAsync();
            Check(result.LoginRequired && result.Problems.Single().Code == "RAIN001" && isolated.LoadTasks().Count == 0);
        }
        using (var reader = Reader(new FixtureHandler(request => request.RequestUri!.AbsolutePath.EndsWith("userinfo", StringComparison.Ordinal)
            ? Json("""{"errcode":0,"data":[{"user_id":77}]}""") : Json("""{"errcode":400000,"errmsg":"identity error"}"""))))
        {
            var result = await new RainSynchronizer(isolated, reader, RainPlatform.Rain, "www.yuketang.cn").RunAsync();
            Check(!result.CourseListCovered && result.Problems.Count == 1 && !result.LoginRequired);
        }
        using (var reader = Reader(new FixtureHandler(request => request.RequestUri!.AbsolutePath.EndsWith("userinfo", StringComparison.Ordinal)
            ? Json("""{"errcode":0,"data":[{"user_id":77}]}""") : Json("""{"errcode":0,"data":{"list":[]}}"""))))
        {
            var result = await new RainSynchronizer(isolated, reader, RainPlatform.Rain, "www.yuketang.cn").RunAsync();
            Check(result.CourseListCovered && result.Courses == 0 && result.Problems.Count == 0);
        }
        foreach (var mode in new[] { "loop", "missing_more", "detail_mismatch", "chapter_missing" })
        {
            var handler = new FixtureHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/v2/api/web/userinfo" => Json("""{"errcode":0,"data":[{"user_id":77}]}"""),
                "/v2/api/web/courses/list" => Json("""{"errcode":0,"data":{"list":[{"id":101,"name":"一班","course_name":"课程"}]}}"""),
                "/v2/api/web/classrooms/101" => mode == "detail_mismatch"
                    ? Json("""{"errcode":0,"data":{"id":999,"name":"别的班级"}}""")
                    : Json("{\"errcode\":0,\"data\":{\"id\":101,\"name\":\"一班\"" + (mode == "chapter_missing" ? ",\"course_sign\":\"SIGN\"" : "") + "}}"),
                "/mooc-api/v1/lms/learn/course/schedule" => Json(Schedule),
                "/mooc-api/v1/lms/learn/course/chapter" => Json("""{"success":true,"data":{}}"""),
                _ => mode == "loop" ? Json("""{"errcode":0,"data":{"activities":[],"has_more":true}}""")
                    : mode == "missing_more" ? Json("""{"errcode":0,"data":{"activities":[]}}""") : Json(EmptyActivities)
            });
            using var reader = Reader(handler);
            var result = await new RainSynchronizer(isolated, reader, RainPlatform.Rain, "www.yuketang.cn").RunAsync();
            Check(result.CoursesRead == 0 && result.Problems.Count > 0 && handler.Requests < 10);
        }
        await SynchronizationBoundsAsync(root);
    }

    private static async Task SynchronizationBoundsAsync(string root)
    {
        var fallback = new RainClassroom("班级", "目录中的课程名", "101", "900", "", "19", "2", true);
        var room = RainClassroomParser.Classroom("""{"errcode":0,"data":{"id":101,"name":"班级"}}""", "101", fallback)!;
        Check(room.HasChapterContent == true && room.CourseName == fallback.CourseName && room.UniversityId == "19");
        Check(RainClassroomParser.Classroom("""{"errcode":0,"data":{"id":102,"name":"班级"}}""", "102", fallback) is null);
        foreach (var time in new[] { "\"1999-12-31T23:59:00Z\"", "\"2125-01-01T00:00:00Z\"", "\"2125-01-01 00:00:00\"" })
        {
            using var value = JsonDocument.Parse(time);
            Check(RainClassroomParser.Time(value.RootElement) is null);
        }
        foreach (var mode in new[] { "catalog_chapter", "request_limit", "rate_limit" })
        {
            var stored = new TaskStore(Path.Combine(root, mode + ".db"), new FixtureProtector());
            var catalog = mode == "request_limit" ? JsonSerializer.Serialize(new
            {
                errcode = 0, data = new { list = Enumerable.Range(101, 13).Select(id => new { id, name = "班级", course_name = "课程" }).ToArray() }
            }) : """{"errcode":0,"data":{"list":[{"id":101,"name":"班级","course_name":"目录中的课程名","is_pro":true}]}}""";
            if (mode == "rate_limit") catalog = Catalog;
            var chapterCalls = 0;
            var handler = new FixtureHandler(request =>
            {
                var address = request.RequestUri!;
                if (address.AbsolutePath == "/v2/api/web/userinfo") return Json("""{"errcode":0,"data":[{"user_id":77}]}""");
                if (address.AbsolutePath == "/v2/api/web/courses/list") return Json(catalog);
                if (address.AbsolutePath.StartsWith("/v2/api/web/classrooms/", StringComparison.Ordinal))
                {
                    if (mode == "rate_limit" && address.AbsolutePath.EndsWith("/102", StringComparison.Ordinal)) return new(HttpStatusCode.TooManyRequests);
                    return Json("{\"errcode\":0,\"data\":{\"id\":" + address.AbsolutePath.Split('/').Last() + ",\"name\":\"班级\"}}");
                }
                if (address.AbsolutePath.EndsWith("/schedule", StringComparison.Ordinal)) return Json(Schedule);
                if (address.AbsolutePath.EndsWith("/chapter", StringComparison.Ordinal)) { chapterCalls++; return Json(Chapter); }
                if (mode == "request_limit") return Json("{\"errcode\":0,\"data\":{\"activities\":[],\"has_more\":true,\"page\":" + RainAddressPolicy.Query(address)!["page"] + "}}");
                if (mode == "rate_limit") return Json("""{"errcode":0,"data":{"activities":[{"type":4,"courseware_id":601,"title":"已读取的测验","status":0}],"has_more":false}}""");
                return Json(EmptyActivities);
            });
            using var reader = Reader(handler);
            var result = await new RainSynchronizer(stored, reader, RainPlatform.Rain, "www.yuketang.cn").RunAsync();
            Check(result.Requests == handler.Requests);
            if (mode == "catalog_chapter")
            {
                Check(chapterCalls == 1 && result.CoursesRead == 1 && result.Tasks == 3);
                Check(stored.LoadTasks().All(task => task.CourseName == "目录中的课程名"));
            }
            else if (mode == "request_limit")
            {
                Check(handler.Requests == RainSynchronizer.MaximumRequests && result.CoursesRead == 0);
                Check(result.Problems.Any(problem => problem.Code == "RAIN002" && problem.Message.Contains("读取上限", StringComparison.Ordinal)));
            }
            else
            {
                Check(handler.Requests == 6 && result.CoursesRead == 1 && result.Added == 1);
                Check(result.Problems.Single().Code == "RAIN002" && stored.LoadTasks().Count == 1);
            }
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Rain classroom check failed: " + (checks + 1)); checks++;
    }
    private static void Reject(Action action, string code)
    {
        try { action(); throw new InvalidOperationException("Expected rejection."); }
        catch (TaskValidationException error) { Check(error.Code == code); }
    }
    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    private static RainHttpReader Reader(HttpMessageHandler handler) => new(RainPlatform.Rain, "www.yuketang.cn", (_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), handler);
    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests++; return Task.FromResult(respond(request)); }
    }
    private sealed class FixtureProtector : ILocalLinkProtector
    {
        public byte[] Protect(string link) => Encoding.UTF8.GetBytes(link).Select(value => (byte)(value ^ 0x5a)).ToArray();
        public string Unprotect(byte[] value) => Encoding.UTF8.GetString(value.Select(item => (byte)(item ^ 0x5a)).ToArray());
    }
}
