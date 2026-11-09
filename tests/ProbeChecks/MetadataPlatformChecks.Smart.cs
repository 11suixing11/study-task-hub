using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessProbe;
using PlatformMetadataTestData;

internal static partial class MetadataPlatformChecks
{
    private const string EmptyFlipCatalog = "{\"status\":200,\"rt\":{\"pageNum\":0,\"pageSize\":20,\"totalCount\":0,\"totalPages\":0,\"pageList\":[]}}";
    private const string EmptySharedCatalog = "{\"code\":200,\"result\":{\"totalCount\":0,\"courseOpenDtos\":null}}";
    private static void SmartParser()
    {
        Check(ZhihuishuMetadataCrypto.KeyFromBlock(Block("{\"module\":6,\"cKey\":\"0123456789abcdef\"}"), 6).SequenceEqual(Fixtures.Key), "smart module-six key restores exact bytes");
        Check(Convert.FromBase64String(ZhihuishuMetadataCrypto.HandshakeParameter(6)).Length == 128, "smart handshake uses official RSA module six");
        Reject(() => ZhihuishuMetadataCrypto.KeyFromBlock(Block("{\"module\":13,\"cKey\":\"0123456789abcdef\"}"), 6), "homepage key cannot satisfy smart module");
        Check(MetadataPlatformParser.ZhihuishuCatalog(EmptyFlipCatalog, 1) is { Outcome: "ok", TotalRows: 0, TotalPages: 0 }, "official zero-page empty flip response accepted");
        foreach (var json in new[] { EmptyFlipCatalog.Replace("\"totalCount\":0", "\"totalCount\":1"), EmptyFlipCatalog.Replace("\"pageSize\":20", "\"pageSize\":5"),
            EmptyFlipCatalog.Replace("\"pageNum\":0", "\"pageNum\":2"), EmptyFlipCatalog.Replace("\"pageList\":[]", "\"pageList\":null") })
            Check(MetadataPlatformParser.ZhihuishuCatalog(json, 1).Outcome != "ok", "contradictory empty flip contract rejected");
        Check(MetadataPlatformParser.ZhihuishuCatalog(EmptyFlipCatalog, 2).Outcome != "ok", "zero-page flip cannot truncate later pages");
        foreach (var bucket in new[] { 0, 1 }) Check(MetadataPlatformParser.ZhihuishuSharedCatalog(EmptySharedCatalog, 1, bucket).Outcome == "ok", "explicit null zero-total shared catalog accepted");
        foreach (var json in new[] { EmptySharedCatalog.Replace("\"totalCount\":0", "\"totalCount\":1"), "{\"code\":200,\"result\":{\"totalCount\":0}}", "{\"code\":200,\"result\":null}" })
            Check(MetadataPlatformParser.ZhihuishuSharedCatalog(json, 1, 0).Outcome != "ok", "missing shared array and positive-total null rejected");
        Check(MetadataPlatformParser.ZhihuishuSharedCatalog(EmptySharedCatalog, 2, 0).Outcome != "ok", "null shared response cannot truncate subsequent page");
        var catalog = MetadataPlatformParser.ZhihuishuSmartCatalog(Fixtures.SmartCatalog(Fixtures.SmartCourse(), Fixtures.SmartCourse(classId: "902")), 3);
        Check(catalog is { Outcome: "ok", TotalRows: 2 } && catalog.Courses.Select(course => course.Id).Distinct().Count() == 2, "same wisdom course in two classes remains independent");
        Check(catalog.Courses.All(course => course.ApiCourseId == Fixtures.SmartCourseId && MetadataTaskNavigation.IsSmartCourse(course)), "large smart course identifier remains exact");
        var numericCatalog = "{\"status\":200,\"rt\":[{\"courseId\":9007199254740993123,\"classId\":9007199254740993999,\"courseName\":\"大整数课程\",\"newFlag\":true}]}";
        var numeric = MetadataPlatformParser.ZhihuishuSmartCatalog(numericCatalog, 3).Courses.Single();
        Check(numeric.ApiCourseId == "9007199254740993123" && numeric.Smart!.ClassId == "9007199254740993999", "JSON numeric ids avoid floating-point rounding");
        foreach (var bucket in new[] { 3, 4 }) Check(MetadataPlatformParser.ZhihuishuSmartCatalog(Fixtures.SmartCatalog(), bucket) is { Outcome: "ok", TotalRows: 0 }, "both empty smart status lists are explicit");
        foreach (var json in new[] { "{\"status\":200}", "{\"status\":200,\"rt\":null}", "{\"status\":200,\"rt\":{}}" })
            Check(MetadataPlatformParser.ZhihuishuSmartCatalog(json, 3).Outcome != "ok", "unknown wisdom catalog cannot be empty success");
        var badRow = Fixtures.SmartCatalog(Fixtures.SmartCourse(classId: "901"), new { courseId = "2", courseName = "缺班级", newFlag = true });
        Check(MetadataPlatformParser.ZhihuishuSmartCatalog(badRow, 3) is { Outcome: "partial_catalog", UnsupportedRows: 1 }, "missing class identity reports partial catalog");
        var conflicting = MetadataPlatformParser.ZhihuishuSmartCatalog(Fixtures.SmartCatalog(Fixtures.SmartCourse(), Fixtures.SmartCourse(name: "冲突标题")), 3);
        Check(conflicting.Courses.Count == 0 && conflicting.Outcome == "partial_catalog", "conflicting smart identity removed");
        foreach (var row in new[] { Fixtures.SmartCourse(newFlag: false), Fixtures.SmartCourse(end: "2025-09-08 00:00:00"), Fixtures.SmartCourse(end: "unknown") })
        {
            var course = MetadataPlatformParser.ZhihuishuSmartCatalog(Fixtures.SmartCatalog(row), 4).Courses.Single();
            Check(!MetadataTaskNavigation.IsSmartCourse(course) && course.Detail.Contains("暂未覆盖", StringComparison.Ordinal), "legacy or ambiguous wisdom version is visible without fabricated tasks");
        }
        Check(MetadataPlatformParser.ZhihuishuAgentCatalog(Fixtures.AgentCatalog(), 1) is { Outcome: "ok", TotalRows: 0, TotalPages: 0 }, "empty agent directory recognized");
        var zeroAgent = "{\"code\":200,\"success\":true,\"data\":{\"page\":0,\"size\":0,\"total\":\"0\",\"totalPage\":0,\"list\":[],\"last\":false}}";
        Check(MetadataPlatformParser.ZhihuishuAgentCatalog(zeroAgent, 1) is { Outcome: "ok", TotalRows: 0 }, "official zeroed agent paging is explicit empty success");
        foreach (var json in new[] { zeroAgent.Replace("\"total\":\"0\"", "\"total\":\"1\""), zeroAgent.Replace("\"totalPage\":0", "\"totalPage\":1"),
            zeroAgent.Replace("\"list\":[]", "\"list\":null"), zeroAgent.Replace("\"success\":true", "\"success\":false"), zeroAgent.Replace("\"size\":0", "\"size\":5") })
            Check(MetadataPlatformParser.ZhihuishuAgentCatalog(json, 1).Outcome != "ok", "zeroed agent paging cannot hide missing rows or contradictory counts");
        Check(MetadataPlatformParser.ZhihuishuAgentCatalog(zeroAgent, 2).Outcome != "ok", "zeroed AI catalog does not truncate later pages");
        Check(MetadataPlatformParser.ZhihuishuAgentCatalog(Fixtures.AgentCatalog([new { courseId = "agent-1", courseName = "合成智能体课", termName = "合成学期" }]), 1).Courses.Single().Kind == "agent", "agent directory course discoverable before task support");
        foreach (var json in new[] { Fixtures.AgentCatalog().Replace("\"totalPage\":0", "\"totalPage\":1"), Fixtures.AgentCatalog().Replace("\"success\":true", "\"success\":false"), "{\"code\":200,\"success\":true,\"data\":{}}" })
            Check(MetadataPlatformParser.ZhihuishuAgentCatalog(json, 1).Outcome != "ok", "unknown or inconsistent agent paging rejected");
        Check(MetadataPlatformParser.ZhihuishuSmartTypes(Fixtures.SmartTypes(Fixtures.SmartType(), Fixtures.SmartType(0, 6, 2))) is { Outcome: "ok", Types.Count: 2 }, "smart type discovery separates homework from resource tasks");
        foreach (var json in new[] { "{\"code\":200,\"data\":null}", Fixtures.SmartTypes(Fixtures.SmartType(count: 1, unfinished: 2)), Fixtures.SmartTypes(Fixtures.SmartType(), Fixtures.SmartType()) })
            Check(MetadataPlatformParser.ZhihuishuSmartTypes(json).Outcome != "ok", "unknown type or contradictory counts rejected");
        var model = Fixtures.SmartCourseModel();
        var pending = MetadataPlatformParser.ZhihuishuSmartTasks(Fixtures.SmartList(Fixtures.SmartTask()), Fixtures.Account, model, "homework", 0).Tasks.Single();
        Check(pending.State == TaskState.Pending && pending.PlatformStatusText.Contains("逾期未提交", StringComparison.Ordinal), "expired unsubmitted homework remains pending");
        Check(pending.DeadlineUtc == new DateTimeOffset(2026, 10, 27, 23, 59, 59, TimeSpan.FromHours(8)).ToUniversalTime()
            && pending.TimeText.Contains("latestSubmitTime=2026-10-29", StringComparison.Ordinal) && pending.TimeText.Contains("peerReviewEndTime=2026-10-30", StringComparison.Ordinal), "original deadline and independent later windows retained");
        foreach (var status in new[] { 5, 6 }) Check(MetadataPlatformParser.ZhihuishuSmartTasks(Fixtures.SmartList(Fixtures.SmartTask(examStatus: status, status: 1)), Fixtures.Account, model, "homework", 1).Tasks.Single().State == TaskState.Completed, "completed bucket and explicit exam status confirm completed work");
        foreach (var row in new[] { Fixtures.SmartTask(examStatus: 5, status: 0), Fixtures.SmartTask(examStatus: 6, status: 0) })
            Check(MetadataPlatformParser.ZhihuishuSmartTasks(Fixtures.SmartList(row), Fixtures.Account, model, "homework", 1).Tasks.Single().State is null, "completed status with unstarted time window remains reviewable");
        Check(MetadataPlatformParser.ZhihuishuSmartTasks(Fixtures.SmartList(Fixtures.SmartTask(status: 1)), Fixtures.Account, model, "homework", 0).Tasks.Single().State is null,
            "expired unsubmitted status with ongoing time window remains reviewable");
        foreach (var row in new[] { Fixtures.SmartTask(examStatus: 6), Fixtures.SmartTask(examStatus: 13), Fixtures.SmartTask(peer: true), Fixtures.SmartTask(submitMode: 2), Fixtures.SmartTask(status: 19), Fixtures.SmartTask(examStatus: 99) })
            Check(MetadataPlatformParser.ZhihuishuSmartTasks(Fixtures.SmartList(row), Fixtures.Account, model, "homework", 0).Tasks.Single().State is null, "contradictory group peer or unknown state remains reviewable");
        foreach (var row in new[] { Fixtures.SmartTask(course: "other-course"), Fixtures.SmartTask(classId: "902"), Fixtures.SmartTask(type: 2), Fixtures.SmartTask(id: "../wrong") })
            Check(MetadataPlatformParser.ZhihuishuSmartTasks(Fixtures.SmartList(row), Fixtures.Account, model, "homework", 0) is { Tasks.Count: 0, UnsupportedRows: 1 }, "foreign course class kind or malformed task identity excluded");
        var invalidTime = MetadataPlatformParser.ZhihuishuSmartTasks(Fixtures.SmartList(Fixtures.SmartTask(end: "2026-10-20 00:00:00")), Fixtures.Account, model, "homework", 0).Tasks.Single();
        Check(invalidTime.DeadlineUtc is null && invalidTime.BeginUtc is null, "inverted smart time window remains untrusted");
        Check(MetadataPlatformParser.ZhihuishuSmartTasks("{\"code\":200,\"data\":null}", Fixtures.Account, model, "homework", 0).Outcome != "ok", "null task list is not success");
        Check(MetadataTaskNavigation.Matches(pending) && MetadataTaskNavigation.Key(Zhs.Id, pending.Link) is not null, "smart official category list bound to exact task");
        foreach (var link in new[] { pending.Link!.Replace("&taskId=smart-pending", "&taskId=other"), pending.Link!.Replace("/901?", "/902?"), pending.Link!.Replace("taskType=1", "taskType=2"), pending.Link + "&extra=1", pending.Link.Replace(".com/", ".com.evil.example/") })
            Check(!MetadataTaskNavigation.Matches(pending with { Link = link }), "altered smart course class task kind or foreign origin cannot bind");
        Check(MetadataTaskNavigation.Key(Zhs.Id, pending.Link) != MetadataTaskNavigation.Key(Zhs.Id, pending.Link!.Replace("smart-pending", "second-task")), "different tasks on same official list have distinct navigation keys");
        Check(TaskStore.MetadataTaskKey(pending) != TaskStore.MetadataTaskKey(pending with { AccountId = "other-account" })
            && TaskStore.MetadataTaskKey(pending) != TaskStore.MetadataTaskKey(pending with { CourseId = Fixtures.SmartCourseModel(classId: "902").Id }), "smart task keys isolate account and class");
    }

    private static void SmartLoginDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "smart-login-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var diagnostics = new MetadataLoginDiagnostics(Zhs, Path.Combine(root, "login.json"));
        diagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.LoadingTimeout, Zhs.Home.AbsoluteUri, navigationId: 101);
        diagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, Zhs.Home.AbsoluteUri, navigationId: 102);
        Check(diagnostics.Snapshot().PageFailure is not null, "different navigation cannot erase old timeout");
        diagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, Zhs.Home.AbsoluteUri, navigationId: 101);
        Check(diagnostics.Snapshot().PageFailure is null, "same delayed navigation success clears its timeout");
        foreach (var stage in new[] { MetadataLoginStage.Resource, MetadataLoginStage.FrameNavigation, MetadataLoginStage.Certificate, MetadataLoginStage.Renderer })
        {
            diagnostics.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
            diagnostics.Record(stage, MetadataLoginResult.NetworkFailed);
            diagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.LoadingTimeout, navigationId: 103);
            diagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, navigationId: 103);
            diagnostics.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.MetadataReady);
            Check(diagnostics.Snapshot().PageFailure?.Stage == stage, "navigation timeout and metadata success preserve concrete page failure");
        }
        diagnostics.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
        diagnostics.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.AccountReady);
        Check(diagnostics.Snapshot().AccountConfirmed && !diagnostics.SessionConfirmed
            && diagnostics.Describe().Contains("登录已确认，课程目录", StringComparison.Ordinal), "verified account with catalog error no longer described as page loaded or logged out");
        Check(!diagnostics.Describe().Contains("尚未取得", StringComparison.Ordinal), "optional student token cannot imply missing login");
        diagnostics.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.LoginRequired);
        Check(!diagnostics.Snapshot().AccountConfirmed, "expired login clears current account confirmation");
    }

    private static MetadataResponse? SmartResponse(MetadataRequest request) => request.Operation switch
    {
        "courses" => new("ok", 200, EmptyFlipCatalog),
        "shared-courses" => new("ok", 200, EmptySharedCatalog),
        "smart-courses" => new("ok", 200, Fixtures.SmartCatalog(request.Bucket == 3
            ? [Fixtures.SmartCourse(), Fixtures.SmartCourse(classId: "902")]
            : [Fixtures.SmartCourse(course: "smart-completed", classId: "903")])),
        "smart-task-types" => new("ok", 200, Fixtures.SmartTypes(Fixtures.SmartType(1, 5, 1), Fixtures.SmartType(2, 1, 1), Fixtures.SmartType(0, 6, 2))),
        "smart-homework" => new("ok", 200, Fixtures.SmartList(request.Bucket == 0 ? [Fixtures.SmartTask(classId: request.Course!.Smart!.ClassId)]
            : Enumerable.Range(1, 4).Select(index => Fixtures.SmartTask("smart-done-" + index, examStatus: 6, status: 1, classId: request.Course!.Smart!.ClassId)).ToArray())),
        "smart-exam" => new("ok", 200, Fixtures.SmartList(request.Bucket == 0 ? [Fixtures.SmartTask("smart-exam", type: 2, examStatus: 1, status: 1, classId: request.Course!.Smart!.ClassId)] : [])),
        _ => null
    };

    private static async Task SmartSync()
    {
        var root = Path.Combine(Path.GetTempPath(), "smart-sync-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new TaskStore(Path.Combine(root, "tasks.db")); var reader = new Fixtures.Reader { Response = SmartResponse };
        var summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary is { AccountVerified: true, CourseListCovered: true, SmartCourses: 3, Tasks: 0, LoginRequired: false }
            && store.LoadCourseRecording(Zhs.Id).Courses.Count == 3, "wisdom-only account no longer fails on explicit empty legacy catalogs");
        Check(reader.Requests.All(request => request.Course is null && request.Operation is "profile" or "courses" or "shared-courses" or "smart-courses" or "agent-courses"), "selection pending reads all catalogs and no tasks");
        var recording = store.LoadCourseRecording(Zhs.Id);
        var selectedKey = CourseRecordingRules.MetadataKey(Zhs.Id, Fixtures.Account, Fixtures.SmartCourseModel().Id, "smart");
        store.SaveCourseRecording(Zhs.Id, false, [selectedKey], true, recording.Revision, Fixtures.Now); reader.Requests.Clear();
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary is { SelectedCourses: 1, CoursesRead: 1, Tasks: 6, Added: 6, FullTaskListsCovered: true, SmartOtherTasks: 6, LoginRequired: false }, "selected new智慧course imports five homework and one exam with resource gap counted");
        Check(summary.Problems.Any(problem => problem.Code == "ZHS006" && problem.Message.Contains("其他", StringComparison.Ordinal)), "additional resource tasks explicitly uncovered");
        Check(reader.Requests.Where(request => request.Course is not null).All(request => request.Course!.Smart?.ClassId == "901")
            && reader.Requests.All(request => request.Operation is not ("reminders" or "shared-reminders" or "shared-profile")), "only selected smart class reads task metadata without obsolete reminder calls");
        Check(store.LoadTasks().Count(task => task.State == TaskState.Completed) == 4 && store.LoadTasks().Count(task => task.State == TaskState.Pending) == 2, "completed and expired unsubmitted smart work reach unified state correctly");
        var original = store.LoadTasks().Single(task => task.PlatformStatusText.Contains("逾期未提交", StringComparison.Ordinal));
        var edited = store.Edit(original.Id, original.Revision, new("人工智慧作业", Zhs.Id, original.CourseName, DeadlineKind.Known,
            null, Fixtures.Now.AddDays(3), original.Link, null, "人工分数", "人工备注", null), Fixtures.Now);
        store.SetState(edited.Id, edited.Revision, TaskState.Completed, Fixtures.Now);
        await new MetadataSynchronizer(Zhs, store, reader).RunAsync(); var current = store.Get(original.Id)!;
        Check(current.Title == "人工智慧作业" && current.DeadlineUtc == Fixtures.Now.AddDays(3) && current.State == TaskState.Completed, "smart resync preserves user title deadline and completion");
        var revision = store.LoadTasks().ToDictionary(task => task.Id, task => task.Revision);
        reader.Response = request => request.Operation == "smart-homework" && request.Bucket == 1 ? new("ok", 200, Fixtures.SmartList()) : SmartResponse(request);
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && store.LoadTasks().All(task => revision[task.Id] == task.Revision), "missing completed smart bucket prevents partial overwrite");
        reader.Response = request => request.Operation == "smart-homework" && request.Bucket == 1 ? new("login_required", 401) : SmartResponse(request);
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.LoginRequired && !summary.AccountVerified && store.LoadTasks().Count == 6, "expired smart task response preserves existing work and rejects current session");
        reader.Response = request => request.Operation == "smart-homework" && request.Bucket == 1
            ? new("ok", 200, Fixtures.SmartList(Fixtures.SmartTask(examStatus: 6, status: 1), Fixtures.SmartTask("done2", examStatus: 6, status: 1), Fixtures.SmartTask("done3", examStatus: 6, status: 1), Fixtures.SmartTask("done4", examStatus: 6, status: 1))) : SmartResponse(request);
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && store.LoadTasks().Count == 6 && store.LoadTasks().All(task => revision[task.Id] == task.Revision), "same smart task in both completion buckets rejected");
        reader.Response = request => request.Operation == "smart-courses" ? new("ok", 200, Fixtures.SmartCatalog(request.Bucket == 3 ? [Fixtures.SmartCourse(newFlag: false)] : [])) : SmartResponse(request);
        reader.Requests.Clear(); summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.CoursesRead == 0 && summary.Problems.Any(problem => problem.Code == "ZHS006") && reader.Requests.All(request => !request.Operation.StartsWith("smart-task", StringComparison.Ordinal)
            && request.Operation is not ("smart-homework" or "smart-exam")), "selected older wisdom version reports task gap and never uses new task API");
        reader.Response = request => request.Operation == "agent-courses" ? new("course_credentials_unavailable", null) : SmartResponse(request);
        reader.Requests.Clear(); summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync(catalogOnly: true);
        Check(summary.AccountVerified && !summary.LoginRequired && !summary.CourseListCovered && summary.SmartCourses == 3, "agent-only credential issue preserves confirmed smart login and discovered courses");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.DatabasePath)).Contains("synthetic-answer-not-read", StringComparison.Ordinal)
            && !Encoding.UTF8.GetString(File.ReadAllBytes(store.DatabasePath)).Contains("synthetic-question-not-read", StringComparison.Ordinal), "smart task questions and answers never enter database");
    }

    private static async Task SmartHttp()
    {
        var smartKey = Encoding.UTF8.GetBytes("smartkey01234567"); var requests = 0; var cookieWrites = 0;
        var cookies = new[] { Fixtures.Cookie(), Fixtures.Cookie() with { Name = "jt-cas", Value = "synthetic-agent-token" },
            Fixtures.Cookie() with { Name = "polymas-cookie", Domain = ".polymas.com", Value = "synthetic-not-exported" } };
        using var handler = new Handler(async (request, cancellation) =>
        {
            requests++; var uri = request.RequestUri!; Check(request.Method == HttpMethod.Post, "new smart and AI metadata use only explicit read POSTs");
            var json = await request.Content!.ReadAsStringAsync(cancellation);
            if (uri.IdnHost == "onlineservice-api.zhihuishu.com")
            {
                Check(uri.AbsolutePath == "/gateway/t/v1/student/queryStudentAICourseList" && request.Content.Headers.ContentType!.MediaType == "application/x-www-form-urlencoded", "wisdom catalog exact official form route");
                var form = Form(json); using var parameters = Decode(form["secretStr"], Fixtures.Key);
                Check(form.Count == 2 && form.ContainsKey("date") && parameters.RootElement.EnumerateObject().Count() == 1 && parameters.RootElement.GetProperty("status").GetInt32() is 3 or 4, "wisdom catalog encrypted module thirteen status without pagination");
                Check(request.Headers.GetValues("Authorization").Single() == "synthetic-home-token" && !request.Headers.Contains("XQJZXHIZ"), "homepage token remains limited to homepage API");
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(Fixtures.SmartCatalog()) };
            }
            using var envelope = JsonDocument.Parse(json);
            Check(request.Content.Headers.ContentType!.MediaType == "application/json", "new task and agent bodies are JSON rather than forms");
            if (uri.IdnHost == "cloudapi.polymas.com")
            {
                Check(uri.AbsolutePath == "/student-course/student/index/teacherQueryStudentCourse" && !request.Headers.Contains("Cookie")
                    && request.Headers.GetValues("Authorization").Single() == "synthetic-agent-token" && !request.Headers.Contains("XQJZXHIZ"), "AI directory exact route uses visible jt-cas without exporting unrelated cookies");
                var query = Form(uri.Query); var p = envelope.RootElement;
                Check(query.GetValueOrDefault("uuid") == Fixtures.Account && query.ContainsKey("date") && p.EnumerateObject().Count() == 7
                    && p.GetProperty("status").GetInt32() == 2 && p.GetProperty("size").GetInt32() == 5 && !p.GetProperty("allCourse").GetBoolean(), "AI directory official parameters and account binding");
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(Fixtures.AgentCatalog(page: p.GetProperty("page").GetInt32())) };
            }
            Check(uri.IdnHost == "kg-run-student.zhihuishu.com" && uri.Query.Length == 0 && !request.Headers.Contains("Authorization")
                && request.Headers.Referrer?.AbsoluteUri == "https://ai-smart-course-student-pro.zhihuishu.com/", "smart task exact origin referrer and independent cookie credentials");
            Check(envelope.RootElement.EnumerateObject().Count() == 2 && envelope.RootElement.GetProperty("date").TryGetInt64(out _), "smart JSON encrypted parameters and numeric date");
            using var marker = Decode(request.Headers.GetValues("XQJZXHIZ").Single(), smartKey);
            Check(marker.RootElement.ValueKind == JsonValueKind.String && marker.RootElement.GetString() == "", "module-six encrypted empty map UID matches official header");
            using var decoded = Decode(envelope.RootElement.GetProperty("secretStr").GetString()!, smartKey); var parametersForTask = decoded.RootElement;
            Check(parametersForTask.GetProperty("courseId").GetString() == Fixtures.SmartCourseId, "raw selected course id used rather than hashed catalog id");
            var types = uri.AbsolutePath == "/student/gateway/t/home/query-course-task-type";
            Check(types ? parametersForTask.EnumerateObject().Count() == 2 && parametersForTask.GetProperty("classId").GetString() == "901"
                : uri.AbsolutePath == "/student/gateway/t/task/taskList" && parametersForTask.EnumerateObject().Count() == 4
                    && parametersForTask.GetProperty("taskType").GetInt32() is 1 or 2 && parametersForTask.GetProperty("status").GetInt32() is 0 or 1
                    && parametersForTask.GetProperty("taskName").GetString() == "", "smart type discovery and list official parameter contracts");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(types ? Fixtures.SmartTypes() : Fixtures.SmartList()) };
        });
        using var reader = new MetadataHttpReader(Zhs, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(cookies), handler,
            (_, _, _, _) => { cookieWrites++; return Task.CompletedTask; }, _ => Task.FromResult<string?>("synthetic-home-token"),
            testMetadataKey: Fixtures.Key, testSmartMetadataKey: smartKey);
        var model = Fixtures.SmartCourseModel();
        foreach (var request in new MetadataRequest[] { new("smart-courses", Bucket: 3), new("smart-courses", Bucket: 4), new("agent-courses"),
            new("smart-task-types") { Course = model }, new("smart-homework", Bucket: 0) { Course = model }, new("smart-homework", Bucket: 1) { Course = model },
            new("smart-exam", Bucket: 0) { Course = model }, new("smart-exam", Bucket: 1) { Course = model } })
            Check((await reader.ReadAsync(request, CancellationToken.None)).Outcome == "ok", "verified new metadata read through synthetic transport");
        Check(cookieWrites == requests, "new successful responses use session renewal pipeline");
        var before = requests;
        foreach (var request in new MetadataRequest[] { new("smart-courses", 2, 3), new("smart-courses", Bucket: 2), new("smart-courses", Bucket: 3) { Course = model },
            new("smart-homework", Bucket: 2) { Course = model }, new("smart-exam", 2) { Course = model }, new("smart-task-types", Bucket: 1) { Course = model },
            new("smart-homework") { Course = model with { Smart = new("902", "pro") } }, new("smart-homework") { Course = model with { Smart = new("901", "legacy-ai") } },
            new("startPullPaper") { Course = model }, new("openExam") { Course = model }, new("missExamUserAddPaper") { Course = model }, new("agent-courses") { Course = model } })
            Check((await reader.ReadAsync(request, CancellationToken.None)).Outcome == "unsupported_request", "unknown versions pagination and writing operations cannot reach network");
        Check(requests == before, "all forbidden new operations rejected before network");
        foreach (var current in new IReadOnlyList<MetadataSessionCookie>[] { [Fixtures.Cookie()], [Fixtures.Cookie(), cookies[1] with { HttpOnly = true }],
            [Fixtures.Cookie(), cookies[1], cookies[1] with { Path = "/onlinestuh5", Value = "conflicting-token" }] })
        {
            using var isolated = new MetadataHttpReader(Zhs, (_, _) => Task.FromResult(current), new Handler((_, _) => throw new Exception("Ambiguous AI credential cannot send HTTP.")), testMetadataKey: Fixtures.Key);
            Check((await isolated.ReadAsync(new("agent-courses"), CancellationToken.None)).Outcome == "course_credentials_unavailable", "missing HttpOnly or ambiguous jt-cas is catalog credential error rather than expired account");
        }
    }
}
