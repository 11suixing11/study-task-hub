using System.Net;
using System.Text;
using System.Text.Json;
using AccessProbe;
using Microsoft.Data.Sqlite;

internal static class MoocChecks
{
    private static int checks;
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly MoocCourse Course = new("线性代数", "1001", "2001", "PKU", 0);
    private static readonly string CourseOne = """{"id":1001,"name":"线性代数","mode":0,"learnedCount":99,"schoolPanel":{"shortName":"PKU"},"termPanel":{"id":2001,"courseId":1001,"endTime":1893456000000}}""";
    private static readonly string CourseTwo = """{"id":1001,"name":"线性代数","mode":20,"schoolPanel":{"schoolSN":"PKU"},"termPanel":{"id":2002,"courseId":1001}}""";

    public static async Task RunAsync()
    {
        ParserChecks();
        NewExamChecks();
        LearnedTermChecks();
        await StoreAndSyncChecks();
        await HttpChecks();
        SessionChecks();
        Console.WriteLine($"PASS: {checks} MOOC metadata, pagination, task-link, merge and protected-session checks.");
    }

    private static void LearnedTermChecks()
    {
        using var dto = JsonDocument.Parse(Term());
        var learned = "{\"code\":0,\"result\":{\"mocTermDto\":" + dto.RootElement.GetProperty("result").GetRawText() + ",\"lastLearnUnitId\":null}}";
        var batch = MoocParser.Term(learned, "9001", Course);
        Check(batch.Outcome == "ok" && batch.Tasks.Count == 5, "student term wrapper retains enrolled task metadata");
        Check(batch.Tasks.Count(task => task.State == TaskState.Completed) == 3 && batch.Tasks.Count(task => task.State == TaskState.Pending) == 2,
            "student term submission evidence is preserved");
        Check(MoocParser.Term(learned.Replace("\"id\":2001", "\"id\":9999"), "9001", Course).Outcome == "term_identity_mismatch",
            "student term rejects mismatched ownership");
        Check(MoocParser.Term("{\"code\":0,\"result\":{\"mocTermDto\":null}}", "9001", Course).Outcome != "ok",
            "missing student DTO is not an empty course");
        var explicitNullType = MoocParser.Term(Term().Replace("\"type\":1,\"deadline\"", "\"type\":null,\"deadline\"")
            .Replace("\"type\":1,\"objectTest\"", "\"type\":null,\"objectTest\""), "9001", Course);
        Check(explicitNullType.Outcome == "ok" && explicitNullType.Tasks.Where(task => task.Kind.StartsWith("exam_", StringComparison.Ordinal))
            .All(task => task.Link!.EndsWith("examType=null", StringComparison.Ordinal)), "official explicit null exam type matches legacy card links");
        var missingType = MoocParser.Term(Term().Replace("\"type\":1,\"deadline\"", "\"deadline\"")
            .Replace("\"type\":1,\"objectTest\"", "\"objectTest\""), "9001", Course);
        Check(missingType.Tasks.Where(task => task.Kind.StartsWith("exam_", StringComparison.Ordinal)).All(task => task.Link is null),
            "absent exam type still cannot invent a route");
        foreach (var task in explicitNullType.Tasks.Where(task => task.Kind.StartsWith("exam_", StringComparison.Ordinal)))
            Check(MoocTaskNavigation.IsOfficialTaskLink(task.Link!, Course.Id, Course.TermId, task.RemoteId, task.Kind, task.ExamId),
                "explicit null exam route passes ownership validation");
        Check(MoocTaskNavigation.Create(Course, "exam_object", "4002", "4001") is null,
            "unconfirmed null exam type stays blocked");
        Check(MoocPlatform.IsReadRequest(MoocPlatform.LearnedTermRpc, new Dictionary<string, string> { ["termId"] = "2001" })
            && !MoocPlatform.IsReadRequest(MoocPlatform.LearnedTermRpc, new Dictionary<string, string> { ["termId"] = "2001", ["isDraft"] = "0" }),
            "student term request has exact official fields");
    }

    private static void NewExamChecks()
    {
        var batch = MoocParser.NewExams(NewExamList(), "9001", Course);
        Check(batch.Outcome == "ok" && batch.Tasks.Count == 3, "new exam official array parsed");
        var pending = batch.Tasks.Single(task => task.RemoteId == "7001");
        Check(pending.State == TaskState.Pending && pending.ScoreText.Length == 0, "new exam default zero grade without attempts is pending");
        var completed = batch.Tasks.Single(task => task.RemoteId == "7002");
        Check(completed.State == TaskState.Completed && completed.ScoreText == "0/100", "new exam submitted zero grade completed");
        Check(batch.Tasks.Single(task => task.RemoteId == "7003").State is null, "new exam score without attempt evidence needs review");
        Check(batch.Tasks.All(task => task.DeadlineUtc == Now.AddHours(4)), "new exam exact task deadline");
        Check(pending.Link == "https://www.icourse163.org/learn/PKU-1001?tid=2001#/learn/newExamAgreement?eid=7001", "new exam opens its official agreement page");
        Check(MoocTaskNavigation.IsOfficialTaskLink(pending.Link!, "1001", "2001", "7001", "exam_new", "7001"), "new exam route ownership valid");
        foreach (var address in new[] { pending.Link + "&id=7001", pending.Link + "&eid=7001", pending.Link!.Replace("eid=7001", "eid=7002"),
            pending.Link.Replace("newExamAgreement", "newExam"), pending.Link.Replace("?tid=2001", "?tid=2002") })
            Check(!MoocTaskNavigation.IsOfficialTaskLink(address, "1001", "2001", "7001", "exam_new", "7001"), "new exam wrong or extra parameters rejected");
        Check(MoocTaskNavigation.Create(Course, "exam_new", "7002", "7001") is null, "new exam identifiers must agree");
        Check(MoocParser.NewExams("{\"code\":0,\"result\":[]}", "9001", Course).Outcome == "ok", "explicit new exam empty list valid");
        foreach (var json in new[] { "{\"code\":0,\"result\":null}", "{\"code\":0,\"result\":{\"list\":[]}}", "{\"code\":-2}" })
            Check(MoocParser.NewExams(json, "9001", Course).Outcome != "ok", "unknown new exam response not empty success");
        var wrongOwner = MoocParser.NewExams("{\"code\":0,\"result\":[{\"id\":7001,\"name\":\"考试\",\"termId\":9999}]}", "9001", Course);
        Check(wrongOwner.Tasks.Count == 0 && wrongOwner.Issues.Contains("new_exam_owner_mismatch"), "new exam explicit wrong owner not imported");
        var duplicate = MoocParser.NewExams("{\"code\":0,\"result\":[{\"id\":7001,\"name\":\"考试\",\"usedTryCount\":1},{\"id\":7001,\"name\":\"考试\",\"usedTryCount\":0}]}", "9001", Course);
        Check(duplicate.Tasks.Count == 1 && duplicate.DuplicateRows == 1 && duplicate.Tasks[0].State == TaskState.Completed, "duplicate new exam retains submission evidence");
        var placeholder = MoocParser.Term("{\"code\":0,\"result\":{\"id\":2001,\"chapters\":[],\"exams\":[{\"id\":7001,\"name\":\"旧接口占位\",\"type\":2}]}}", "9001", Course);
        var merged = MoocParser.MergeTaskBatches(placeholder, batch);
        Check(merged.Tasks.Count == 3 && merged.Tasks.All(task => task.Kind == "exam_new") && merged.Outcome == "ok", "new exam replaces legacy placeholder without duplicate");
        Check(MoocPlatform.IsReadRequest(MoocPlatform.NewExamRpc, new Dictionary<string, string> { ["termId"] = "2001" }), "new exam metadata RPC allowlisted");
        Check(!MoocPlatform.IsReadRequest(MoocPlatform.NewExamRpc, new Dictionary<string, string> { ["termId"] = "2001", ["examId"] = "7001" }), "new exam RPC fields restricted");
        Check(!MoocPlatform.IsReadRequest("/mm-tiku/web/j/mocExamBean.getPaper.rpc", new Dictionary<string, string> { ["examId"] = "7001" }), "start exam RPC not allowed");
        var path = Path.Combine(Path.GetTempPath(), "mooc-new-exam-" + Guid.NewGuid().ToString("N"), "tasks.db");
        var store = new TaskStore(path); var imported = store.ImportMoocTasks(batch.Tasks, Now);
        Check(imported.Added == 3 && store.LoadTasks().Count == 3 && store.LoadTasks().Count(task => task.State == TaskState.NeedsReview) == 1, "new exam tasks share store and review status");
    }

    private static void ParserChecks()
    {
        Check(MoocPlatform.Login.IdnHost == MoocPlatform.Host && MoocPlatform.Login.AbsolutePath == "/member/login.htm"
            && Encoding.UTF8.GetString(Convert.FromBase64String(RainAddressPolicy.Query(MoocPlatform.Login)!["returnUrl"])) == MoocPlatform.Home.AbsoluteUri,
            "official sign-in route returns to personal home without account parameters");
        Check(MoocParser.EnvelopeOutcome("""{"code":-2,"message":"尚未登录"}""") == "login_required", "expired RPC is not empty");
        Check(MoocParser.EnvelopeOutcome("""{"code":-1002,"message":"非法跨域"}""") == "csrf_rejected", "CSRF rejection is distinct from expired login");
        foreach (var json in new[] { "{}", "[]", "null", "<html>登录</html>", "{", """{"code":0}""", """{"result":[]}""" })
            Check(MoocParser.EnvelopeOutcome(json) != "ok", "unknown envelope rejected");
        Check(MoocParser.EnvelopeOutcome("""{"code":-1007,"message":"参数不支持"}""") == "rpc_error", "contract failure explicit");
        var catalog = MoocParser.Catalog(Page([CourseOne, CourseTwo, CourseOne], 1, 1, 3));
        Check(catalog.Courses.Count == 2 && catalog.DuplicateRows == 1 && catalog.TotalCount == 3, "same course, distinct terms retained");
        Check(catalog.Courses[1].IsSpoc && catalog.Courses[1].School == "PKU", "official SPOC mode and schoolSN parsed");
        Check(MoocParser.Catalog(Page([CourseOne.Replace("\"mode\":0", "\"mode\":30")], 1, 1, 1)).Issues.Contains("course_mode_unrecognized"), "unknown course mode visible");
        Check(MoocParser.Catalog("""{"code":0,"result":{"result":[]}}""").Issues.Contains("pagination_unrecognized"), "missing pagination not declared complete");
        Check(MoocParser.Catalog("""{"code":0,"result":{"result":[],"pagination":{"totlePageCount":0,"totleCount":0}}}""").Outcome == "ok", "known empty course list");
        Check(MoocParser.Catalog("""{"code":0,"result":{"anything":[]}}""").Outcome == "unrecognized_catalog", "unknown empty list not accepted");
        var term = MoocParser.Term(Term(), "9001", Course);
        Check(term.Outcome == "ok" && term.Tasks.Count == 5 && term.DuplicateRows == 1, "homework quiz and split exam imported");
        Check(term.Tasks.Single(task => task.Kind == "homework").State == TaskState.Completed, "submitted homework completed");
        var zeroScore = term.Tasks.Single(task => task.RemoteId == "3002");
        Check(zeroScore.State == TaskState.Completed && zeroScore.ScoreText == "0/100", "zero grade is a real submission");
        Check(term.Tasks.Single(task => task.RemoteId == "3003").State == TaskState.Pending, "explicit zero attempts pending");
        Check(term.Tasks.All(task => task.DeadlineUtc == Now.AddHours(4)), "precise task deadlines, not course end");
        var objective = term.Tasks.Single(task => task.Kind == "exam_object");
        Check(objective.State == TaskState.Completed && objective.StateEvidence == "usedTryCount=2", "duplicate incomplete exam preserves stronger status");
        Check(objective.Link!.Contains("#/learn/examObject?eid=4001&id=4002&examType=1", StringComparison.Ordinal), "exam route has parent and component ids");
        Check(term.Tasks.Single(task => task.Kind == "exam_subject").State == TaskState.Pending, "exam draft not counted as submission");
        Check(MoocParser.Term("""{"code":0,"result":{"id":2001,"chapters":[],"exams":[]}}""", "9001", Course).Outcome == "ok", "explicit empty term");
        foreach (var body in new[] { "{}", """{"id":2001}""", """{"id":2001,"chapters":null}""", """{"id":2001,"chapters":[{"name":"未知"}]}""", """{"id":2002,"chapters":[]}""" })
            Check(MoocParser.Term("{\"code\":0,\"result\":" + body + "}", "9001", Course).Outcome != "ok", "unknown term not no-tasks");
        var unknown = MoocParser.Term(OneTask("{}"), "9001", Course).Tasks.Single();
        Check(unknown.State is null && unknown.PlatformStatusText == "状态待核对", "missing evidence not pending");
        var draft = MoocParser.Term(OneTask("{\"userSubmitStatus\":1}"), "9001", Course).Tasks.Single();
        Check(draft.State == TaskState.Pending && draft.PlatformStatusText.Contains("草稿", StringComparison.Ordinal), "draft not completed");
        var badTime = MoocParser.Term(OneTask("{\"deadline\":\"yesterday\"}"), "9001", Course);
        Check(badTime.Issues.Contains("deadline_unrecognized") && badTime.Tasks.Single().DeadlineUtc is null, "invalid deadline not fabricated");
        var missingExam = MoocParser.Term("""{"code":0,"result":{"id":2001,"chapters":[],"exams":[{"id":501,"name":"新考试","type":2}]}}""", "9001", Course);
        Check(missingExam.Outcome == "partial_tasks" && missingExam.Tasks.Single().Link is null, "unknown exam routes explicit");
        var oj = MoocParser.Term("""{"code":0,"result":{"id":2001,"chapters":[{"lessons":[{"id":1,"name":"编程","contentType":3,"test":{"id":77,"type":7,"usedTryCount":0}}]}],"exams":[]}}""", "9001", Course).Tasks.Single();
        Check(oj.Kind == "homework_oj" && oj.Link!.Contains("#/learn/ojhw?id=77", StringComparison.Ordinal), "OJ task uses official route");
        foreach (var task in term.Tasks)
            Check(MoocTaskNavigation.IsOfficialTaskLink(task.Link!, task.CourseId, task.TermId, task.RemoteId, task.Kind, task.ExamId), "all task deep links owned");
        var link = zeroScore.Link!;
        foreach (var changed in new[] { link.Replace("www.icourse163.org", "www.icourse163.org.evil.test"), link.Replace("tid=2001", "tid=2002"),
            link.Replace("id=3002", "id=3009"), link.Replace("#/learn/quiz", "#/learn/content"), link + "&answer=1", link.Replace("https://", "http://"), link.Replace("?tid=2001", "?tid=2001&tid=2001") })
            Check(!MoocTaskNavigation.IsOfficialTaskLink(changed, "1001", "2001", "3002", "quiz", ""), "wrong task route rejected");
        Check(MoocTaskNavigation.Create(Course with { School = "" }, "quiz", "3002") is null, "unknown school not numeric route guess");
        Check(MoocTaskNavigation.Create(Course with { Mode = 10 }, "quiz", "3002")!.Contains("/spoc/learn/PKU-1001", StringComparison.Ordinal), "sync SPOC route");
    }

    private static async Task StoreAndSyncChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "mooc-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tasks.db"); var store = new TaskStore(path);
        var existing = store.Create(new("手动任务", "chaoxing", "旧课程", DeadlineKind.Unknown, null, null, null, null, "", "", null), Now);
        var collected = MoocParser.Term(Term(), "9001", Course).Tasks;
        var imported = store.ImportMoocTasks(collected, Now);
        Check(imported.Added == 5 && store.LoadTasks().Count == 6, "MOOC shares existing task table");
        var task = store.LoadTasks().Single(task => task.PlatformStateEvidence == "usedTryCount=0");
        Check(TaskRules.Priority(task, Now) == 2 && TaskRules.HasTrustedDeadline(task), "task deadline drives priority");
        Check(task.Source == TaskSource.Mooc && task.SourceKey!.StartsWith("mooc:", StringComparison.Ordinal), "independent source scope");
        Check(!File.ReadAllText(path).Contains("#/learn/quiz", StringComparison.Ordinal), "navigation is protected");
        task = store.Edit(task.Id, task.Revision, new("自定义标题", "mooc", "自定义课程", DeadlineKind.Known, null, Now.AddHours(1),
            task.Link, null, "自定义成绩", "备注保留", 1), Now);
        task = store.SetState(task.Id, task.Revision, TaskState.Ignored, Now);
        store.ImportMoocTasks(collected, Now.AddMinutes(1)); var preserved = store.Get(task.Id)!;
        Check(preserved.Title == "自定义标题" && preserved.CourseName == "自定义课程" && preserved.DeadlineUtc == Now.AddHours(1), "manual metadata wins");
        Check(preserved.State == TaskState.Ignored && preserved.ScoreText == "自定义成绩" && preserved.Notes == "备注保留" && preserved.PriorityOverride == 1, "manual state score notes priority survive sync");
        var unknown = MoocParser.Term(OneTask("{}"), "9001", Course).Tasks;
        store.ImportMoocTasks(unknown, Now);
        Check(store.LoadTasks().Single(task => task.PlatformStatusText == "状态待核对").State == TaskState.NeedsReview, "unknown task shown honestly");
        Check(store.Get(existing.Id)!.Title == "手动任务", "other platform unchanged");
        var alternate = collected.Select(row => row with { AccountId = "9002" }).ToArray();
        Check(store.ImportMoocTasks(alternate, Now).Added == 5, "different account not merged");
        Check(TaskStore.MoocTaskKey(collected[0]) != TaskStore.MoocTaskKey(collected[0] with { TermId = "9999" }), "term identity included");
        var initialCount = store.LoadTasks().Count;
        Reject(() => store.ImportMoocTasks([collected[0] with { AccountId = "9003" }, collected[1] with { RemoteId = "invalid" }], Now), "MOOC003");
        Check(store.LoadTasks().Count == initialCount, "invalid batch rolled back");
        Reject(() => store.ImportMoocTasks([collected[0] with { Link = collected[0].Link!.Replace("tid=2001", "tid=9001") }], Now), "MOOC003");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { store.ImportMoocTasks(collected, Now, cancelled.Token); throw new Exception("MOOC check: cancellation ignored"); } catch (OperationCanceledException) { checks++; }
        Check(new TaskStore(path).LoadTasks().Count == initialCount, "reopen store preserves all platforms");
        var opened = new List<string>(); var navigation = new TaskNavigationService(store, null, opened.Add);
        var target = store.LoadTasks().First(task => task.PlatformStatusText == "已批改");
        var result = await navigation.OpenAsync(target.Id, CancellationToken.None);
        Check(opened.Single() == target.Link && result.Destination == "view", "user click opens task directly");
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tasks SET payload=json_set(payload,'$.CollectedNavigationKey','wrong') WHERE id=$id"; command.Parameters.AddWithValue("$id", target.Id); command.ExecuteNonQuery();
        }
        try { await navigation.OpenAsync(target.Id, CancellationToken.None); throw new Exception("MOOC check: mismatched navigation opened"); }
        catch (TaskValidationException error) { Check(error.Code == "LINK003" && opened.Count == 1, "navigation mismatch blocks browser"); }

        var syncStore = new TaskStore(Path.Combine(root, "sync.db"));
        var reader = new FakeReader((path, fields) => path == MoocPlatform.CourseRpc
            ? Ok(fields["p"] == "1" ? Page([CourseOne], 1, 2, 2) : Page([CourseTwo], 2, 2, 2))
            : Ok(path == MoocPlatform.NewExamRpc ? "{\"code\":0,\"result\":[]}" : Term(fields["termId"])));
        var summary = await new MoocSynchronizer(syncStore, reader, "9001").RunAsync();
        Check(summary.CourseListCovered && summary.Courses == 2 && summary.CoursesRead == 2 && summary.Tasks == 10 && summary.CatalogPages == 2, "all course pages and terms read");
        Check(summary.SpocCourses == 1 && summary.TasksWithDeadline == 10 && summary.TasksWithLink == 10, "metadata counters across MOOC SPOC");
        Check(reader.Requests.Count == 6 && reader.Requests.All(request => MoocPlatform.IsReadRequest(request.Path, request.Fields)), "only allowlisted read RPCs including new exams");
        Check(reader.Requests.Count(request => request.Path == MoocPlatform.LearnedTermRpc) == 2
            && reader.Requests.All(request => request.Path != MoocPlatform.TermRpc), "automatic sync uses enrolled student metadata for every term");
        var twice = await new MoocSynchronizer(syncStore, reader, "9001").RunAsync();
        Check(twice.Added == 0 && twice.Updated == 10 && syncStore.LoadTasks().Count == 10, "repeat sync stable ids");
        syncStore.RecordMoocSyncResult(summary, "completed", Now);
        Check(syncStore.LoadMoocSettings().LastSummary!.Tasks == 10 && !syncStore.LoadPlatformSettings("yuketang").Connected, "platform settings independent");
        var loop = new FakeReader((path, fields) => path == MoocPlatform.CourseRpc ? Ok(Page([CourseOne], 1, 2, 2)) : Ok(path == MoocPlatform.NewExamRpc ? "{\"code\":0,\"result\":[]}" : Term()));
        var loopSummary = await new MoocSynchronizer(syncStore, loop, "9001").RunAsync();
        Check(!loopSummary.CourseListCovered && loopSummary.Problems.Any(problem => problem.Message.Contains("分页", StringComparison.Ordinal)), "repeated page not complete");
        var partial = new FakeReader((path, fields) => path == MoocPlatform.CourseRpc ? Ok(Page([CourseOne], 1, 1, 2)) : Ok(path == MoocPlatform.NewExamRpc ? "{\"code\":0,\"result\":[]}" : Term()));
        Check(!(await new MoocSynchronizer(syncStore, partial, "9001").RunAsync()).CourseListCovered, "count mismatch partial");
        var expired = new FakeReader((_, _) => new("login_required", 200));
        var failure = await new MoocSynchronizer(syncStore, expired, "9001").RunAsync();
        Check(failure.LoginRequired && failure.Problems.Single().Code == "MOOC001" && syncStore.LoadTasks().Count == 10, "expired login keeps tasks");
        var contract = new FakeReader((_, _) => new("rpc_error", 200));
        Check((await new MoocSynchronizer(syncStore, contract, "9001").RunAsync()).Problems.Single().Code == "MOOC003", "bad RPC not empty result");
        var newReader = new FakeReader((path, _) => Ok(path == MoocPlatform.CourseRpc ? Page([CourseOne], 1, 1, 1) : path == MoocPlatform.NewExamRpc ? NewExamList() : Term()));
        var newSummary = await new MoocSynchronizer(new TaskStore(Path.Combine(root, "new-sync.db")), newReader, "9001").RunAsync();
        Check(newSummary.CoursesRead == 1 && newSummary.Tasks == 8 && newSummary.NewExamTasks == 3 && newSummary.StatusNeedsReview == 1, "synchronizer includes new exams and review counters");
        var failedNewReader = new FakeReader((path, _) => path == MoocPlatform.NewExamRpc ? new("rpc_error", 200) : Ok(path == MoocPlatform.CourseRpc ? Page([CourseOne], 1, 1, 1) : Term()));
        var failedNew = await new MoocSynchronizer(new TaskStore(Path.Combine(root, "failed-new.db")), failedNewReader, "9001").RunAsync();
        Check(failedNew.CoursesRead == 0 && failedNew.Tasks == 5 && failedNew.Problems.Any(problem => problem.Message.Contains("新版考试", StringComparison.Ordinal)), "unrecognized new exam request keeps legacy tasks and partial coverage");
        var deniedSession = new FakeReader((path, _) => Ok(path == MoocPlatform.CourseRpc ? Page([CourseOne], 1, 1, 1) : path == MoocPlatform.NewExamRpc ? "{\"code\":0,\"result\":[]}" : Term()));
        try
        {
            await new MoocSynchronizer(syncStore, deniedSession, "9001", _ => throw new OperationCanceledException()).RunAsync();
            throw new Exception("MOOC check: changed session imported");
        }
        catch (OperationCanceledException) { Check(syncStore.LoadTasks().Count == 10, "session validated before import"); }
    }

    private static async Task HttpChecks()
    {
        var csrf = Cookie("NTESSTUDYSI", "fixture-csrf");
        var renewalCount = 0; var requests = new List<(HttpMethod Method, string Body, string Csrf, string Cookies)>();
        using var reader = new MoocHttpReader((_, _) => Task.FromResult<IReadOnlyList<MoocSessionCookie>>([csrf]),
            new Handler(async (request, token) =>
            {
                requests.Add((request.Method, await request.Content!.ReadAsStringAsync(token), RainAddressPolicy.Query(request.RequestUri!)!["csrfKey"], request.Headers.GetValues("Cookie").Single()));
                var response = Json("{\"code\":0,\"result\":[]}");
                if (requests.Count == 1) response.Headers.TryAddWithoutValidation("Set-Cookie", "NTESSTUDYSI=renewed; Domain=.icourse163.org; Path=/; Secure; HttpOnly; SameSite=Lax");
                return response;
            }), (_, _, renewed, _) => { renewalCount += renewed.Count; return Task.CompletedTask; });
        var fields = Fields();
        Check((await reader.ReadAsync(MoocPlatform.CourseRpc, fields, default)).Outcome == "ok", "RPC form request works");
        Check((await reader.ReadAsync(MoocPlatform.CourseRpc, fields, default)).Outcome == "ok", "RPC renewal request works");
        Check(requests[0].Method == HttpMethod.Post && requests[0].Body.Contains("psize=8", StringComparison.Ordinal) && requests[0].Csrf == "fixture-csrf", "official form and profile csrf");
        Check(requests[1].Csrf == "renewed" && requests[1].Cookies.Contains("NTESSTUDYSI=renewed", StringComparison.Ordinal) && renewalCount == 1, "response renewal used and written back");
        string? browserCookieHeader = null;
        var pageView = Cookie("Hm_lvt_fixture", "1792775943,1792843420");
        using var browserReader = new MoocHttpReader((_, _) => Task.FromResult<IReadOnlyList<MoocSessionCookie>>([csrf, pageView]),
            new Handler((request, _) => { browserCookieHeader = request.Headers.GetValues("Cookie").Single(); return Task.FromResult(Json("{\"code\":0,\"result\":[]}")); }));
        Check((await browserReader.ReadAsync(MoocPlatform.CourseRpc, fields, default)).Outcome == "ok"
            && browserCookieHeader == "NTESSTUDYSI=fixture-csrf; Hm_lvt_fixture=1792775943,1792843420",
            "real browser comma-valued analytics cookie is transmitted without CookieContainer failure");
        Check((await reader.ReadAsync("/web/j/quizBean.submitQuiz.rpc", fields, default)).Outcome == "unsupported_request" && requests.Count == 2, "submission RPC denied before network");
        Check((await reader.ReadAsync(MoocPlatform.CourseRpc, fields.Append(new KeyValuePair<string, string>("answer", "1")).ToDictionary(), default)).Outcome == "unsupported_request", "unknown request fields denied");
        using var emptyReader = new MoocHttpReader((_, _) => Task.FromResult<IReadOnlyList<MoocSessionCookie>>([]), new Handler((_, _) => throw new Exception("unexpected network")));
        Check((await emptyReader.ReadAsync(MoocPlatform.CourseRpc, fields, default)).Outcome == "csrf_missing", "missing csrf not fake token");
        foreach (var (json, expected) in new[] { ("{\"code\":-2}", "login_required"), ("{\"code\":-1002}", "csrf_rejected"), ("{\"code\":-1007}", "rpc_error"), ("<html>Login</html>", "invalid_json") })
        {
            using var failing = new MoocHttpReader((_, _) => Task.FromResult<IReadOnlyList<MoocSessionCookie>>([csrf]), new Handler((_, _) => Task.FromResult(Json(json))));
            var result = await failing.ReadAsync(MoocPlatform.CourseRpc, fields, default);
            Check(result.Outcome == expected && result.Json.Length == 0, "failed response not retained or empty success");
        }
        var limitedCalls = 0;
        using var limited = new MoocHttpReader((_, _) => Task.FromResult<IReadOnlyList<MoocSessionCookie>>([csrf]), new Handler((_, _) => { limitedCalls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)); }));
        await limited.ReadAsync(MoocPlatform.CourseRpc, fields, default); await limited.ReadAsync(MoocPlatform.CourseRpc, fields, default);
        Check(limitedCalls == 1, "rate limit stops later reads");
        using var redirect = new MoocHttpReader((_, _) => Task.FromResult<IReadOnlyList<MoocSessionCookie>>([csrf]), new Handler((_, _) =>
        { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("https://evil.test/"); return Task.FromResult(response); }));
        Check((await redirect.ReadAsync(MoocPlatform.CourseRpc, fields, default)).Outcome == "unsupported_redirect", "redirect not followed with cookie");
    }

    private static void SessionChecks()
    {
        var now = DateTimeOffset.UtcNow;
        var parsed = MoocResponseCookies.Parse(new Uri("https://www.icourse163.org/web/j/test.rpc"),
            ["NTESSTUDYSI=renewed; Domain=.icourse163.org; Path=/; Secure; HttpOnly; Max-Age=3600; SameSite=Lax",
             "gone=; Domain=.icourse163.org; Path=/; Max-Age=0", "wrong=x; Domain=.chaoxing.com; Path=/"], now);
        Check(parsed.Count == 2 && parsed[0].ExpiresUtc == now.AddHours(1) && parsed[0].Secure && parsed[0].HttpOnly, "official expiry and flags parsed");
        Check(parsed[1].IsExpired(now), "deletion represented");
        var pageView = MoocResponseCookies.Parse(MoocPlatform.Home, ["Hm_lvt_fixture=1792775943,1792843420; Domain=.icourse163.org; Path=/; Secure"], now);
        Check(pageView.Count == 1 && pageView[0].Value == "1792775943,1792843420", "comma-valued response cookie retains browser representation");
        var cookie = Cookie("NTESSTUDYSI", "local-test-secret");
        Check(!MoocSessionStore.IsValid(cookie with { Value = "one; second=value" }), "browser cookie cannot inject another header pair");
        Check(MoocSessionStore.MissingForRestore([cookie], []).Count == 1, "session cookie can restore");
        Check(MoocSessionStore.MissingForRestore([cookie], [cookie with { Value = "new-login" }]).Count == 0, "newer account wins over archive");
        var analytics = cookie with { Name = "Hm_lpvt_fixture", Value = "old-page-view" };
        Check(MoocSessionStore.MissingForRestore([cookie, analytics], [analytics with { Value = "new-page-view" }]).Single() == cookie,
            "page-view timestamps cannot prevent restoring missing signed-in session");
        Check(MoocSessionStore.MissingForRestore([cookie, analytics], [cookie with { Value = "new-login" }, analytics with { Value = "new-page-view" }]).Count == 0,
            "analytics exclusion still protects a newer account");
        Check(!MoocSessionStore.IsValid(cookie with { Domain = ".icourse163.org.evil.test" }) && !MoocSessionStore.IsValid(cookie with { Domain = ".chaoxing.com" }), "session domains isolated");
        Check(!cookie.AppliesTo(new Uri("https://evil.test/")) && !cookie.AppliesTo(new Uri("http://www.icourse163.org/")), "cookie scope honored");
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(Path.GetTempPath(), "mooc-session-" + Guid.NewGuid().ToString("N"), "session.bin"); var store = new MoocSessionStore(path);
            store.Save([cookie, cookie with { Name = "expired", ExpiresUtc = now.AddSeconds(-1) }], now);
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("local-test-secret", StringComparison.Ordinal), "DPAPI archive hides secret");
            var loaded = store.Load(now);
            Check(loaded.Count == 1 && loaded[0] == cookie && loaded[0].ExpiresUtc is null, "session flag and cookie preserved");
            Check(store.Load(now.AddDays(31)).Count == 0 && store.LastLoadOutcome == "too_old", "old archive not reused");
            store.Clear(); Check(store.Load(now).Count == 0, "cleared archive not restored");
        }
        Check(!JsonSerializer.Serialize(MoocParser.Term(Term(), "9001", Course).Tasks).Contains("9001", StringComparison.Ordinal), "account and links absent in metadata diagnostic");
    }

    private static MoocSessionCookie Cookie(string name, string value) => new(name, value, ".icourse163.org", "/", true, true, null, "Lax");
    private static Dictionary<string, string> Fields() => new() { ["type"] = "30", ["p"] = "1", ["psize"] = "8", ["courseType"] = "1" };
    private static MoocResponse Ok(string json) => new("ok", 200, json);
    private static string Page(string[] courses, int index, int pages, int count) => "{\"code\":0,\"result\":{\"result\":[" + string.Join(',', courses)
        + "],\"pagination\":{\"pageIndex\":" + index + ",\"totlePageCount\":" + pages + ",\"totleCount\":" + count + "}}}";
    private static string Term(string termId = "2001")
    {
        var deadline = Now.AddHours(4).ToUnixTimeMilliseconds();
        return """
        {"code":0,"result":{"id":TERM,"chapters":[{"name":"第一章","homeworks":[{"name":"作业","test":{"id":3001,"type":10,"deadline":DEADLINE,"usedTryCount":2}}],
        "quizs":[{"name":"零分测验","test":{"id":3002,"type":1,"deadline":DEADLINE,"userScore":0,"totalScore":100}},
        {"name":"未做测验","test":{"id":3003,"type":1,"deadline":DEADLINE,"usedTryCount":0}}],
        "exam":{"id":4001,"name":"期中考试","type":1,"deadline":DEADLINE,"objectTest":{"id":4002,"examId":4001},"objectTestVo":{"id":4002,"usedTryCount":2},"subjectTest":{"id":4003,"examId":4001,"userSubmitStatus":1}}}],
        "exams":[{"id":4001,"name":"期中考试","type":1,"objectTest":{"id":4002,"usedTryCount":0}}]}}
        """.Replace("TERM", termId).Replace("DEADLINE", deadline.ToString());
    }
    private static string OneTask(string fields)
    {
        var extra = fields.Trim()[1..^1];
        return "{\"code\":0,\"result\":{\"id\":2001,\"chapters\":[{\"homeworks\":[{\"name\":\"未知任务\",\"test\":{\"id\":6001"
            + (extra.Length > 0 ? "," + extra : "") + "}}]}],\"exams\":[]}}";
    }
    private static string NewExamList() => """
        {"code":0,"result":[{"id":7001,"name":"新版待做考试","usedTryCount":0,"userScore":0,"totalScore":100,"deadline":DEADLINE},
        {"id":7002,"name":"新版已提交考试","usedTryCount":1,"userScore":0,"totalScore":100,"deadline":DEADLINE},
        {"id":7003,"name":"新版待核对考试","userScore":0,"deadline":DEADLINE}]}
        """.Replace("DEADLINE", Now.AddHours(4).ToUnixTimeMilliseconds().ToString());
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken); }
    private sealed class FakeReader(Func<string, IReadOnlyDictionary<string, string>, MoocResponse> respond) : IMoocMetadataReader
    {
        public List<(string Path, IReadOnlyDictionary<string, string> Fields)> Requests { get; } = [];
        public Task<MoocResponse> ReadAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Requests.Add((path, fields)); return Task.FromResult(respond(path, fields)); }
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("MOOC check failed: " + label); checks++; }
    private static void Reject(Action action, string code)
    {
        try { action(); throw new Exception("MOOC check failed: expected validation error"); }
        catch (TaskValidationException error) { Check(error.Code == code, "validation error code"); }
    }
}
