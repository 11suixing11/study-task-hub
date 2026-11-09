using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessProbe;
using U = PlatformMetadataTestData.UcampusFixtures;

internal static partial class MetadataPlatformChecks
{
    private static async Task UcampusChecks()
    {
        var platform = MetadataPlatform.Ucampus; var course = U.BoundCourse();
        Check(MetadataPlatformParser.UcampusAccount([U.Cookie()]) == U.Account, "legacy cookie account bound");
        Check(MetadataPlatformParser.UcampusAccount([U.Cookie(), U.Cookie("other")]) is null, "ambiguous legacy identity refused");
        Check(MetadataPlatformParser.UcampusProfile(U.Profile(), U.Account) == "ok", "legacy account envelope without invented status");
        Check(MetadataPlatformParser.UcampusProfile(U.Profile("other"), U.Account) == "account_mismatch", "legacy response cannot cross account");
        var catalog = MetadataPlatformParser.UcampusCatalog(U.Catalog());
        Check(catalog is { Outcome: "ok", Rows: 1 } && catalog.Courses.Single().Id == U.Course().Id, "official class cards parsed without promotion");
        Check(MetadataPlatformParser.UcampusCatalog(U.EmptyCatalog()) is { Outcome: "ok", Rows: 0 }, "explicit empty legacy course page");
        foreach (var html in new[] { "<div class='my_course_box'><div class='my_course_list'></div></div>", "<!doctype html><title>官网介绍</title>", "<div>my_course_item</div>" })
            Check(MetadataPlatformParser.UcampusCatalog(html).Outcome != "ok", "unrecognized legacy page cannot be empty catalog");
        Check(MetadataPlatformParser.UcampusCatalog(U.Catalog(U.Card() + U.Card(name: "冲突课程"))).Courses.Count == 0, "conflicting legacy course excluded");
        Check(MetadataPlatformParser.UcampusCourse(U.CatalogPage().Replace("dbCourseId='1001'", "dbCourseId='1002'"), U.Course()).Outcome == "course_binding_mismatch", "content course differs from database course");
        Check(MetadataPlatformParser.UcampusCourse(U.CatalogPage(), U.Course()).Course!.Ucampus!.ContentId == "synthetic+content:pc", "content id retained separately from database id");
        Check(MetadataPlatformParser.UcampusHomeworkEntry(U.HomeworkShell().Replace("courseId=1001", "courseId=1002"), course).Outcome != "ok", "shell list course cannot drift");
        Check(MetadataPlatformParser.UcampusHomeworkEntry(U.HomeworkShell().Replace("type=1", "type=2"), course).Outcome != "ok", "unverified homework type contract refused");
        var batch = MetadataPlatformParser.UcampusHomework(U.DefaultList(), U.Account, course, 1);
        Check(batch is { Outcome: "ok", ListComplete: true, TotalRows: U.HomeworkTaskCount } && batch.Tasks.Count == U.HomeworkTaskCount, "all homework display states in one list");
        Check(batch.Tasks.Count(task => task.State == TaskState.Completed) == 1 && batch.Tasks.Count(task => task.State == TaskState.Returned) == 1, "legacy state zero minus-one two mapping");
        Check(batch.Tasks[1].PlatformStatusText.Contains("已结束", StringComparison.Ordinal) && batch.Tasks[1].State == TaskState.Completed, "ended display has independent submitted evidence");
        foreach (var row in batch.Tasks)
        {
            Check(MetadataTaskNavigation.Matches(row), "legacy specific task link matches account class and exercise");
            var entry = MetadataTaskNavigation.ParseUcampus(row.Link)!;
            Check(entry.CourseId == course.Id && entry.Account == U.Account && !row.Link!.Contains("sign=", StringComparison.Ordinal), "signatures not persisted");
            var signed = MetadataTaskNavigation.UcampusSignedAddress(entry, "synthetic-sign+value==", entry.Submitted ? "1" : null)!;
            Check(MetadataTaskNavigation.UcampusDestinationMatches(row.Link!, signed) && !signed.Contains("courseType%3D", StringComparison.Ordinal), "signature destination and stripped local context");
            foreach (var replacement in new[] { signed.Replace("exerciseId=" + entry.Homework, "exerciseId=9999"), signed.Replace("uexercise.unipus.cn", "evil.example"), signed + "&extra=1" })
                Check(!MetadataTaskNavigation.UcampusDestinationMatches(row.Link!, replacement), "foreign signed destination rejected");
        }
        foreach (var state in new[] { 1, 3, 99 })
            Check(MetadataPlatformParser.UcampusHomework(U.List([U.Row(state: state)]), U.Account, course, 1).Tasks.Single().State is null, "unknown completion cannot be guessed");
        var ended = MetadataPlatformParser.UcampusHomework(U.List([U.Row(state: 0, display: 2)]), U.Account, course, 1).Tasks.Single();
        Check(ended.State == TaskState.Pending, "ended does not imply completion");
        var peer = MetadataPlatformParser.UcampusHomework(U.List([U.Row(peer: 1, enabled: 1, state: 2)]), U.Account, course, 1).Tasks.Single();
        Check(peer.State is null && peer.DeadlineUtc is null && peer.Link is null, "peer review end is not submission deadline or whole task completed");
        var peerSubmission = MetadataPlatformParser.UcampusHomework(U.List([U.Row(peer: 1, enabled: 1, showEvaluation: 1)]), U.Account, course, 1).Tasks.Single();
        Check(peerSubmission.DeadlineUtc == new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime(), "official peer submission date chosen");
        var group = MetadataPlatformParser.UcampusHomework(U.List([U.Row(type: 3)]), U.Account, course, 1).Tasks.Single();
        Check(group.Link is null && group.State == TaskState.Pending, "group task retained with unverified entry");
        Check(MetadataPlatformParser.UcampusHomework(U.List([U.Row(classId: "9999")]), U.Account, course, 1) is { UnsupportedRows: 1, Tasks.Count: 0 }, "cross-class homework refused");
        Check(MetadataPlatformParser.UcampusHomework(U.List(), U.Account, course, 1) is { Outcome: "ok", TotalPages: 0, ListComplete: true }, "known legacy zero-page empty response");
        foreach (var json in new[] { "{}", U.List(total: 11), U.List([U.Row()], total: 2), U.List().Replace("\"pageSize\":10", "\"pageSize\":20") })
            Check(MetadataPlatformParser.UcampusHomework(json, U.Account, course, 1).Outcome != "ok", "unknown pagination cannot be empty success");
        var directory = Path.Combine(Path.GetTempPath(), "legacy-metadata-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var store = new TaskStore(Path.Combine(directory, "tasks.db")); var reader = new U.Reader();
        var pending = await new MetadataSynchronizer(platform, store, reader).RunAsync();
        Check(pending.Courses == 1 && pending.Tasks == 0 && reader.Requests.Select(request => request.Operation).SequenceEqual(new[] { "profile", "courses" }),
            "unconfirmed Ucampus discovers courses before homework and exam requests");
        var initial = store.LoadCourseRecording(platform.Id);
        store.SaveCourseRecording(platform.Id, true, [], true, initial.Revision, PlatformMetadataTestData.Fixtures.Now);
        reader.Requests.Clear();
        var summary = await new MetadataSynchronizer(platform, store, reader).RunAsync();
        Check(summary is { Courses: 1, SelectedCourses: 1, Tasks: U.TaskCount, FullTaskListsCovered: true }, "legacy sync imports unified metadata");
        Check(store.LoadTasks().All(task => task.Source == TaskSource.Ucampus && task.CollectedNavigationKey.Length > 0), "legacy source statistics and protected link keys");
        var task = store.LoadTasks().Single(task => task.State == TaskState.Pending && task.PlatformTitle == "合成 U 校园待办");
        var edited = store.Edit(task.Id, task.Revision, new("人工标题", "ucampus", "人工课程", DeadlineKind.None, null, null,
            "https://example.invalid/manual", null, "", "", null), PlatformMetadataTestData.Fixtures.Now);
        store.SetState(edited.Id, edited.Revision, TaskState.Ignored, PlatformMetadataTestData.Fixtures.Now);
        await new MetadataSynchronizer(platform, store, reader).RunAsync();
        Check(store.Get(task.Id) is { Title: "人工标题", State: TaskState.Ignored, Link: "https://example.invalid/manual", DeadlineKind: DeadlineKind.None }, "legacy manual edits preserve priority");
        reader.Response = request => request.Operation == "ucampus-homework" ? new("ok", 200, U.List()) : null;
        await new MetadataSynchronizer(platform, store, reader).RunAsync();
        Check(store.LoadTasks().Count == U.TaskCount, "disappearing legacy tasks retain history");
        reader.Response = null; reader.AccountId = "synthetic-other-account";
        await new MetadataSynchronizer(platform, store, reader).RunAsync();
        Check(store.LoadTasks().Count == U.TaskCount * 2, "legacy same course and homework ids account-isolated");
        reader.AccountId = U.Account;
        var recording = store.LoadCourseRecording("ucampus"); store.SaveCourseRecording("ucampus", false, [], true, recording.Revision, PlatformMetadataTestData.Fixtures.Now);
        reader.Requests.Clear(); await new MetadataSynchronizer(platform, store, reader).RunAsync();
        Check(reader.Requests.Select(request => request.Operation).SequenceEqual(new[] { "profile", "courses" }), "unselected legacy course does not read homework");
        recording = store.LoadCourseRecording("ucampus"); store.SaveCourseRecording("ucampus", true, [], true, recording.Revision, PlatformMetadataTestData.Fixtures.Now);
        var tenRows = Enumerable.Range(1, 10).Select(number => U.Row((4100 + number).ToString())).ToArray();
        reader.Response = request => request.Operation == "ucampus-homework" ? new("ok", 200, request.Page == 1 ? U.List(tenRows, 1, 11) : U.List([U.Row("4111")], 2, 11)) : null;
        summary = await new MetadataSynchronizer(platform, store, reader).RunAsync();
        Check(summary is { FullTaskListsCovered: true } && summary.Tasks == 11 + U.ExamTaskCount, "legacy second page uses exact count");
        var before = store.LoadTasks().Count;
        reader.Response = request => request.Operation == "ucampus-homework" ? new("ok", 200, request.Page == 1 ? U.List(tenRows, 1, 11) : U.List([tenRows[0]], 2, 11)) : null;
        summary = await new MetadataSynchronizer(platform, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.Tasks == U.ExamTaskCount && store.LoadTasks().Count == before, "legacy overlapping pages discard staged homework batch while retaining exams");
        reader.Response = request => request.Operation == "ucampus-homework" ? new("ok", 200, U.List(tenRows, request.Page, 1001)) : null;
        summary = await new MetadataSynchronizer(platform, store, reader).RunAsync();
        Check(summary.Problems.Any(problem => problem.Code == "UC005") && summary.Tasks == U.ExamTaskCount, "legacy repeated pagination stops bounded reads");
        await UcampusExamChecks(course, directory);
        await UcampusHttpChecks(course, directory);
    }
    private static async Task UcampusHttpChecks(MetadataCourse course, string directory)
    {
        var requests = new List<string>(); var signature = "synthetic-sign+value=="; var confirmation = false; var wrongExamType = false;
        using var handler = new Handler((request, _) =>
        {
            var uri = request.RequestUri!; requests.Add(uri.AbsolutePath);
            Check(uri.IdnHost == "u.unipus.cn" && request.Method == HttpMethod.Get && request.Content is null && !request.Headers.Contains("Authorization"), "legacy transport metadata only GET");
            var body = uri.AbsolutePath switch
            {
                "/user/data/getToken" => U.Profile(), "/user/student/index" => U.Catalog(),
                "/user/student/mycourse/courseCatalog" => U.CatalogPage(),
                "/user/student/homework/index" => RainAddressPolicy.Query(uri)!.GetValueOrDefault("type") == "2" ? U.ExamShell() : U.HomeworkShell(),
                "/user/student/homework/list" => RainAddressPolicy.Query(uri)!.GetValueOrDefault("type") == "2" ? U.DefaultExamList() : U.DefaultList(),
                "/user/student/homework/chk" => JsonSerializer.Serialize(new { code = 0, rs = new { code = confirmation ? 2 : 0, sign = signature } }),
                "/user/student/homework/examDetail" => JsonSerializer.Serialize(new { code = 0, rs = new { sign = signature,
                    type = RainAddressPolicy.Query(uri)!.GetValueOrDefault("homeworkId") == "4005" && !wrongExamType ? 2 : 1 } }),
                _ => throw new Exception("Unexpected legacy route")
            };
            if (uri.AbsolutePath == "/user/student/homework/list")
            {
                var fields = RainAddressPolicy.Query(uri)!;
                Check(fields["courseId"] == "1001" && fields["school_id"] == "6001" && fields["newState"] == "" && fields["state"] == ""
                    && fields["start"] == "1", "legacy sends database course and page with no state filter");
                Check(fields["type"] == "1" ? !fields.ContainsKey("classId") && !fields.ContainsKey("eccId")
                    : fields["type"] == "2" && fields["classId"] == "2001" && fields["eccId"] == "3001", "legacy exam transport retains official class and ECC parameters");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request });
        });
        using var reader = new MetadataHttpReader(MetadataPlatform.Ucampus, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([U.Cookie()]), handler);
        foreach (var request in new MetadataRequest[] { new("profile"), new("courses"), new("ucampus-course") { Course = course },
            new("ucampus-homework-entry") { Course = course }, new("ucampus-homework") { Course = course },
            new("ucampus-exam-entry") { Course = course }, new("ucampus-exam") { Course = course } })
            Check((await reader.ReadAsync(request, CancellationToken.None)).Outcome == "ok", "legacy verified read operation");
        foreach (var task in MetadataPlatformParser.UcampusHomework(U.DefaultList(), U.Account, course, 1).Tasks
            .Concat(MetadataPlatformParser.UcampusExam(U.DefaultExamList(), U.Account, course, 1).Tasks))
        {
            var resolved = await reader.ResolveUcampusAsync(task.Link!, CancellationToken.None);
            Check(resolved.Outcome == "ready" && MetadataTaskNavigation.UcampusDestinationMatches(task.Link!, resolved.Address!), "click-time legacy signature resolved without answer reads");
        }
        wrongExamType = true;
        var completedExam = MetadataPlatformParser.UcampusExam(U.DefaultExamList(), U.Account, course, 1).Tasks.Single(task => task.State == TaskState.Completed);
        Check((await reader.ResolveUcampusAsync(completedExam.Link!, CancellationToken.None)).Outcome == "unrecognized_entry", "homework signature type cannot open a submitted exam");
        wrongExamType = false;
        confirmation = true;
        Check((await reader.ResolveUcampusAsync(MetadataTaskNavigation.Ucampus(course, U.Account, "4001", "5001", false)!, CancellationToken.None)).Outcome
            == "official_confirmation_required", "official destructive redo confirmation not bypassed");
        var count = requests.Count;
        foreach (var operation in new[] { "submit", "startLearn", "joinClass", "answer", "paper", "ucampus-homework", "ucampus-exam" })
            Check((await reader.ReadAsync(new(operation), CancellationToken.None)).Outcome == "unsupported_request", "unbound or mutation operation never sent");
        Check(requests.Count == count && requests.All(path => !path.StartsWith("/uexercise/", StringComparison.Ordinal)), "legacy entry content never fetched");
        var diagnostic = new MetadataLoginDiagnostics(MetadataPlatform.Zhihuishu, Path.Combine(directory, "login-diagnostics.json"));
        diagnostic.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Blocked, "https://evil.example/private-identity?pwd=synthetic-secret#token=private");
        diagnostic.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Started, "https://passport.zhihuishu.com/login?pwd=synthetic-secret&service=account-private");
        diagnostic.Record(MetadataLoginStage.Resource, MetadataLoginResult.ScriptUnavailable, "https://login.zhihuishu.com/assets/private-secret.js?token=secret", 200);
        diagnostic.ObserveSession([PlatformMetadataTestData.Fixtures.Cookie()], true);
        var stored = File.ReadAllText(diagnostic.Path);
        Check(!stored.Contains("synthetic-secret", StringComparison.Ordinal) && !stored.Contains("private", StringComparison.Ordinal)
            && !stored.Contains(PlatformMetadataTestData.Fixtures.Account, StringComparison.Ordinal) && !stored.Contains("pwd=", StringComparison.Ordinal), "login diagnostics never persist URL values account or tokens");
        Check(diagnostic.Snapshot() is { BlockedNavigations: 1, FailedResources: 1, AccountPresent: true, StudentTokenPresent: true }, "login failure stages and credential-presence counters");
        for (var index = 0; index < 30; index++) diagnostic.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.LoginRequired);
        Check(diagnostic.Snapshot() is { Events.Count: 24, PageFailure.Result: MetadataLoginResult.ScriptUnavailable }
            && diagnostic.Describe().Contains("登录脚本未能加载", StringComparison.Ordinal), "bounded diagnostics retain failure across repeated session checks");
        diagnostic.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
        Check(diagnostic.Snapshot().PageFailure is null && !diagnostic.Describe().Contains("登录脚本未能加载", StringComparison.Ordinal),
            "retry clears previous attempt failure without resetting cumulative counters");
        var legacyDiagnostic = new MetadataLoginDiagnostics(MetadataPlatform.Ucampus, Path.Combine(directory, "ucampus-login-diagnostics.json"));
        legacyDiagnostic.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
        legacyDiagnostic.Record(MetadataLoginStage.Popup, MetadataLoginResult.Blocked, "https://evil.example/private?pwd=synthetic-secret");
        legacyDiagnostic.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.LoginRequired);
        Check(legacyDiagnostic.Snapshot().PageFailure is { Stage: MetadataLoginStage.Popup, Result: MetadataLoginResult.Blocked },
            "unconfirmed Ucampus login retains the blocked login popup failure");
        legacyDiagnostic.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.MetadataReady);
        legacyDiagnostic.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, MetadataPlatform.Ucampus.Login.AbsoluteUri);
        Check(legacyDiagnostic.Snapshot().PageFailure is { Stage: MetadataLoginStage.Popup, Result: MetadataLoginResult.Blocked },
            "existing account metadata cannot erase a blocked popup while still on the login page");
        legacyDiagnostic.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, "https://u.unipus.cn/user/student/index");
        Check(legacyDiagnostic.Snapshot().PageFailure is null && !legacyDiagnostic.Describe().Contains("跳转尚未适配", StringComparison.Ordinal),
            "confirmed Ucampus login replaces the earlier popup warning with the verified session result");
        Check(legacyDiagnostic.Snapshot().BlockedNavigations == 1
            && legacyDiagnostic.Snapshot().Events.Any(item => item is { Stage: MetadataLoginStage.Popup, Result: MetadataLoginResult.Blocked }),
            "successful Ucampus login retains the blocked popup as historical evidence");
        Check(legacyDiagnostic.SessionConfirmed, "Ucampus session confirmation requires verified account and course metadata");
        legacyDiagnostic.Record(MetadataLoginStage.Popup, MetadataLoginResult.PopupSuppressed, "https://evil.example/private?pwd=synthetic-secret");
        Check(legacyDiagnostic.SessionConfirmed && legacyDiagnostic.Snapshot() is { PageFailure: null, BlockedNavigations: 2 }
            && legacyDiagnostic.Describe().Contains("登录已确认", StringComparison.Ordinal),
            "suppressed post-login popup does not replace verified login with a failure");
        Check(!File.ReadAllText(legacyDiagnostic.Path).Contains("synthetic-secret", StringComparison.Ordinal),
            "suppressed popup diagnostics do not persist navigation query values");
        legacyDiagnostic.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.LoginRequired);
        Check(!legacyDiagnostic.SessionConfirmed, "a rejected session removes post-login popup handling eligibility");
        legacyDiagnostic.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Blocked, "https://evil.example/private");
        legacyDiagnostic.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.MetadataReady);
        Check(legacyDiagnostic.Snapshot().PageFailure is { Stage: MetadataLoginStage.MainNavigation, Result: MetadataLoginResult.Blocked },
            "existing account metadata cannot erase a failed main login navigation");
        legacyDiagnostic.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
        Check(!legacyDiagnostic.SessionConfirmed && legacyDiagnostic.Snapshot().PageFailure is null,
            "a fresh login attempt resets account confirmation and visible navigation failure");
        const string student = "https://u.unipus.cn/user/student/index";
        legacyDiagnostic.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, student);
        legacyDiagnostic.Record(MetadataLoginStage.Popup, MetadataLoginResult.Blocked);
        legacyDiagnostic.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.LoginRequired);
        Check(legacyDiagnostic.Snapshot().PageFailure is { Stage: MetadataLoginStage.Popup }, "student document without verified session retains popup failure");
        legacyDiagnostic.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.MetadataReady);
        Check(legacyDiagnostic.Snapshot().PageFailure is null && legacyDiagnostic.Describe().Contains("账号和课程读取已确认", StringComparison.Ordinal),
            "diagnostic owner recovers student popup without a connection-window subscriber");
        legacyDiagnostic.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
        legacyDiagnostic.Record(MetadataLoginStage.Popup, MetadataLoginResult.Blocked);
        legacyDiagnostic.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.MetadataReady);
        Check(legacyDiagnostic.Snapshot().PageFailure is { Stage: MetadataLoginStage.Popup }, "new attempt cannot reuse the previous student document");
        foreach (var failureStage in new[] { MetadataLoginStage.MainNavigation, MetadataLoginStage.FrameNavigation, MetadataLoginStage.Resource,
            MetadataLoginStage.Certificate, MetadataLoginStage.Renderer })
        {
            legacyDiagnostic.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
            legacyDiagnostic.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, student);
            legacyDiagnostic.Record(failureStage, MetadataLoginResult.NetworkFailed);
            legacyDiagnostic.Record(MetadataLoginStage.Popup, MetadataLoginResult.Blocked);
            legacyDiagnostic.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.MetadataReady);
            Check(legacyDiagnostic.Snapshot().PageFailure is { Result: MetadataLoginResult.NetworkFailed } current && current.Stage == failureStage,
                "later popup and background success preserve " + failureStage + " failure");
        }
    }
}
