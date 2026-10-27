using System.Net;
using System.Text;
using System.Text.Json;
using AccessProbe;

internal static class ChaoxingSyncChecks
{
    private static int checks;
    private const string EntryA = "https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid=101&clazzid=201&cpi=301";
    private const string EntryB = "https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid=102&clazzid=202&cpi=302";
    private const string TaskA = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=201&cpi=301&workId=401&answerId=501&enc=SYNTHETIC%2BONLY";
    private const string TaskB = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=102&classId=202&cpi=302&workId=402&enc=SYNTHETIC%2BONLY";
    private const string Detail = "<div class='detailsHead'><h2 class='mark_title'>合成任务</h2><div class='infoHead'><p class='rightBord'>作答时间:<em>10-14 19:04</em>至<em>10-25 23:59</em></p></div></div>";
    private const string SubmittedPrompt = "<div class='workHead'></div><div class='workCen'><div class='subSucess'>提交成功</div>"
        + "<p class='dpy'>教师限制查看</p><p class='subTime'>提交时间：2026-10-20 12:00</p></div>";
    private const string Signature = "<input id='workEnc' value='WORK+SIGNATURE'><input id='openc' value='OPEN&amp;VALUE'><input id='enc' value='GENERAL+ENC'>";

    private static string CatalogHtml => "<div class='course-list'>"
        + $"<div class='course'><a href='{WebUtility.HtmlEncode(EntryA)}'><span class='course-name' title='合成课程 A &amp; 分析'>A</span></a></div>"
        + $"<div class='course'><a href='{WebUtility.HtmlEncode(EntryB)}'><span class='course-name' title='合成课程 B'>B</span></a></div></div>";
    private static string ListHtml(string title, string target) => $"<ul id='workList'><li data='{WebUtility.HtmlEncode(target)}' onclick='goTask(this);'><div class='right-content'><p class='overHidden2'>{title}</p><p class='status'>已完成</p></div><div class='time'>剩余48小时</div></li></ul>";

    public static async Task RunAsync()
    {
        var catalogAddress = new Uri(ChaoxingSyncParser.Catalog + "?courseType=1&courseFid=901&courseTeacherid=0&courseOrderBy=0");
        Check(ChaoxingSyncParser.ReadCatalogAddress("<script>var fid = '901';</script>") == catalogAddress);
        Check(ChaoxingSyncParser.ReadCatalogAddress("<input id='fid' value='901'>") == catalogAddress);
        Check(ChaoxingSyncParser.ReadCatalogAddress("<script>const fid = '901';</script><input id='fid' value='902'>") is null);
        Check(ChaoxingSyncParser.ReadCatalogAddress("<html>unknown</html>") is null);
        Check(ChaoxingSyncParser.ReadCatalogAddress("<input id='fid' value='injected'>") is null);
        var catalog = ChaoxingSyncParser.ParseCatalog(CatalogHtml, catalogAddress);
        Check(catalog.Courses.Count == 2 && catalog.Courses[0].Name == "合成课程 A & 分析");
        Check(catalog.Courses[1].CourseId == "102" && catalog.Courses[1].Cpi == "302");
        Check(ChaoxingSyncParser.ParseCatalog(CatalogHtml + CatalogHtml, catalogAddress).Courses.Count == 2);
        Check(ChaoxingSyncParser.ParseCatalog("<div class='course-list'>暂无课程</div>", catalogAddress).Outcome == "empty");
        Check(ChaoxingSyncParser.ParseCatalog("<html>unknown</html>", catalogAddress).Outcome == "schema_unrecognized");
        Check(ChaoxingSyncParser.ParseCatalog(new string('a', ChaoxingSyncParser.MaximumBytes + 1), catalogAddress).Outcome == "response_too_large");
        Check(!JsonSerializer.Serialize(catalog.Courses[0]).Contains("CourseId", StringComparison.Ordinal));
        Check(!JsonSerializer.Serialize(catalog.Courses[0]).Contains("stucoursemiddle", StringComparison.Ordinal));
        Check(catalog.Courses[0].ToString() == nameof(ChaoxingCourse));
        var workAddress = ChaoxingSyncParser.ReadWorkListAddress(Signature, catalog.Courses[0]);
        Check(workAddress is not null && workAddress.AbsoluteUri.Contains("enc=WORK%2BSIGNATURE", StringComparison.Ordinal));
        Check(workAddress!.AbsoluteUri.Contains("openc=OPEN%26VALUE", StringComparison.Ordinal)
            && workAddress.AbsoluteUri.Contains("stuenc=GENERAL%2BENC", StringComparison.Ordinal));
        Check(ChaoxingSyncParser.ReadWorkListAddress("<input id='enc' value='wrong-signature'>", catalog.Courses[0]) is null);
        Check(ChaoxingSyncParser.ReadWorkListAddress(Signature + "<input id='courseid' value='999'>", catalog.Courses[0]) is null);
        Check(ChaoxingSyncParser.ReadWorkListAddress(Signature + "<input id='workEnc' value='OTHER'>", catalog.Courses[0]) is null);
        Check(ChaoxingSyncParser.IsReadAddress(workAddress));
        Check(!ChaoxingSyncParser.IsReadAddress(new("https://mooc1.chaoxing.com.evil.example/mooc2/work/list")));
        Check(!ChaoxingSyncParser.IsReadAddress(new("https://mooc1.chaoxing.com/work/submit")));
        Check(!ChaoxingSyncParser.IsReadAddress(new("http://mooc1.chaoxing.com/mooc2/work/list")));
        Check(!ChaoxingSyncParser.IsReadAddress(new("https://user@mooc1.chaoxing.com/mooc2/work/list")));
        Check(ChaoxingSyncParser.IsLoginPage("<title>用户登录</title><input type='password'>"));
        Check(!ChaoxingSyncParser.IsLoginPage(CatalogHtml));
        Check(ChaoxingSyncParser.IsExplicitEmptyWorkList("<div class='null'>暂无作业</div>"));
        Check(!ChaoxingSyncParser.IsExplicitEmptyWorkList("<html>unknown</html>"));
        Check(ChaoxingSyncParser.IsExplicitEmptyWorkList("<div class='main task-list'><div class='has-content'><div class='null-data'> 暂无作业 </div></div></div>"));
        Check(!ChaoxingSyncParser.IsExplicitEmptyWorkList("<div class='null-data'>暂无作业</div>"));
        Check(!ChaoxingSyncParser.IsExplicitEmptyWorkList("<div class='task-list'><div class='has-content'><div class='null-data'>暂无作业</div></div>" + ListHtml("任务 A", TaskA) + "</div>"));
        var pageTwo = catalogAddress.AbsoluteUri + "&page=2";
        var pages = ChaoxingSyncParser.ParseCatalog(CatalogHtml + $"<div data-total-pages='2'><a href='{WebUtility.HtmlEncode(pageTwo)}'>2</a></div>", catalogAddress);
        Check(pages.ExpectedPages == 2 && pages.NextPages.Count == 1 && !pages.UnresolvedPaging);
        Check(ChaoxingSyncParser.ParseCatalog(CatalogHtml + "<div class='pagination'><a onclick='goPage(2)' href='javascript:;'>下一页</a></div>", catalogAddress).UnresolvedPaging);
        Check(ChaoxingSyncParser.ParseCatalog(CatalogHtml + $"<a href='{WebUtility.HtmlEncode(pageTwo.Replace("courseFid=901", "courseFid=999", StringComparison.Ordinal))}'>other account</a>", catalogAddress).NextPages.Count == 0);

        var root = Path.Combine(Path.GetTempPath(), "aggregate-sync-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new TaskStore(Path.Combine(root, "tasks.db"));
        var reader = new FixtureReader();
        var progress = new CapturedProgress();
        var summary = await new ChaoxingSynchronizer(store, reader, TimeSpan.Zero).RunAsync(progress, CancellationToken.None);
        Check(!summary.LoginRequired && summary.Courses == 2 && summary.CoursesRead == 2 && summary.Problems.Count == 0);
        Check(summary.Added == 2 && summary.Updated == 0 && summary.DetailsSaved == 2);
        Check(store.LoadTasks().Count == 2 && store.LoadTasks().All(task => task.DetailTimeText.Contains("10-25 23:59", StringComparison.Ordinal)));
        Check(store.LoadTasks().All(task => task.DeadlineUtc is not null && task.DeadlineEvidence == DeadlineEvidence.InferredYear && task.State == TaskState.Completed));
        Check(store.LoadTasks().All(task => task.DetailCheckedAtUtc is not null));
        Check(progress.Values.Any(value => value.Courses == 2 && value.CoursesRead == 1));
        Check(reader.Addresses.Count == 8 && reader.Addresses.All(ChaoxingSyncParser.IsReadAddress));
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "tasks.db"))).Contains("SYNTHETIC%2BONLY", StringComparison.Ordinal));
        var saved = store.LoadTasks().Single(task => task.CourseName.Contains("A &", StringComparison.Ordinal));
        saved = store.Edit(saved.Id, saved.Revision, new("我的名称", "chaoxing", "我的课程", DeadlineKind.Known, null,
            DateTimeOffset.UtcNow.AddDays(3), saved.Link, null, "", "用户备注", null), DateTimeOffset.UtcNow);
        store.SetState(saved.Id, saved.Revision, TaskState.Completed, DateTimeOffset.UtcNow);
        reader.Addresses.Clear();
        var repeated = await new ChaoxingSynchronizer(store, reader, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
        var preserved = store.Get(saved.Id)!;
        Check(repeated.Added == 0 && repeated.Updated == 2 && repeated.DetailsSaved == 0);
        Check(preserved.Title == "我的名称" && preserved.CourseName == "我的课程" && preserved.State == TaskState.Completed);
        Check(preserved.DeadlineUtc is not null && preserved.Notes == "用户备注" && preserved.DetailTimeText.Contains("作答时间", StringComparison.Ordinal));
        Check(reader.Addresses.Count == 6); // Same-day details are cached; list signatures still refresh.

        var deniedReader = new FixtureReader { DenySecondCourse = true };
        var partial = await new ChaoxingSynchronizer(store, deniedReader, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
        Check(partial.CoursesRead == 1 && partial.Problems.Single().Code == "SYNC004");
        Check(store.LoadTasks().Count == 2 && store.Get(saved.Id)!.State == TaskState.Completed);
        var loggedOut = await new ChaoxingSynchronizer(store, new FixtureReader { LoggedOut = true }, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
        Check(loggedOut.LoginRequired && loggedOut.Problems.Single().Code == "SYNC001" && loggedOut.Added == 0);
        var noSignature = await new ChaoxingSynchronizer(store, new FixtureReader { MissingSignature = true }, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
        Check(noSignature.CoursesRead == 0 && noSignature.Problems.All(problem => problem.Code == "SYNC005"));
        var unknown = await new ChaoxingSynchronizer(store, new FixtureReader { UnknownList = true }, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
        Check(unknown.CoursesRead == 0 && unknown.Problems.All(problem => problem.Code == "SYNC006"));
        var crossCourseStore = new TaskStore(Path.Combine(root, "cross-course.db"));
        var cross = await new ChaoxingSynchronizer(crossCourseStore, new FixtureReader { WrongCourseLink = true }, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
        Check(cross.Added == 1 && cross.Problems.Any(problem => problem.Code == "SYNC006"));
        var deferredPaging = await new ChaoxingSynchronizer(store, new FixtureReader { UnknownPaging = true }, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
        Check(!deferredPaging.CourseListCovered && deferredPaging.Problems.Any(problem => problem.Code == "SYNC007"));
        var emptyCourseStore = new TaskStore(Path.Combine(root, "empty-course.db"));
        var emptyCourse = await new ChaoxingSynchronizer(emptyCourseStore, new FixtureReader { EmptyFirstCourse = true }, TimeSpan.Zero)
            .RunAsync(null, CancellationToken.None);
        Check(emptyCourse.CoursesRead == 2 && emptyCourse.Added == 1 && emptyCourse.Problems.Count == 0);
        foreach (var mode in new[] { "view", "prompt", "action", "wrong_work", "wrong_answer", "unknown" })
        {
            var detailStore = new TaskStore(Path.Combine(root, "detail-" + mode + ".db"));
            var detailReader = new FixtureReader { DetailMode = mode };
            var detailSummary = await new ChaoxingSynchronizer(detailStore, detailReader, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
            Check(detailSummary.CoursesRead == 2 && detailSummary.Added == 2 && detailStore.LoadTasks().Count == 2);
            if (mode == "view")
                Check(detailSummary.DetailsSaved == 2 && detailSummary.Problems.Count == 0
                    && detailStore.LoadTasks().All(task => task.Link?.Contains("/work/task?", StringComparison.Ordinal) == true));
            else if (mode == "prompt")
                Check(detailSummary.DetailsSaved == 0 && detailSummary.DetailsUnavailable == 2 && detailSummary.Problems.Count == 0
                    && detailStore.LoadTasks().All(task => task.DetailCheckedAtUtc is not null && task.DetailTimeText.Length == 0 && task.DeadlineEvidence == DeadlineEvidence.RemainingTime));
            else if (mode == "action")
                Check(detailSummary.DetailsSaved == 0 && detailSummary.DetailsRequireAction == 2 && detailSummary.Problems.Count == 0
                    && detailStore.LoadTasks().All(task => task.DetailCheckedAtUtc is not null && task.State == TaskState.Completed));
            else if (mode == "wrong_answer")
                Check(detailSummary.DetailsSaved == 1 && detailSummary.Problems.Single().Code == "SYNC006"
                    && detailStore.LoadTasks().Single(task => task.CourseName.Contains("A &", StringComparison.Ordinal)).DetailTimeText.Length == 0);
            else
                Check(detailSummary.DetailsSaved == 0 && detailSummary.Problems.Count > 0 && detailSummary.Problems.All(problem => problem.Code == "SYNC006")
                    && detailStore.LoadTasks().All(task => task.DetailTimeText.Length == 0));
            if (mode is "prompt" or "action")
            {
                detailReader.Addresses.Clear();
                await new ChaoxingSynchronizer(detailStore, detailReader, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
                Check(detailReader.Addresses.Count == 6);
                detailStore.RecordSyncResult(detailSummary, "completed", DateTimeOffset.UtcNow);
                Check(detailStore.LoadSettings().LastSyncSummary is { } recorded
                    && recorded.DetailsUnavailable == detailSummary.DetailsUnavailable && recorded.DetailsRequireAction == detailSummary.DetailsRequireAction);
            }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(async () => await new ChaoxingSynchronizer(store, reader, TimeSpan.Zero).RunAsync(null, cancelled.Token));
        await Cancelled(() => Task.Run(() => store.ImportWorks([new("canceled", "", "", WorkListParser.ParseTaskLocation(TaskA))], "ignored", DateTimeOffset.UtcNow, cancelled.Token)));
        Check(store.LoadTasks().Count == 2);
        store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = false });
        store.RecordSyncResult(summary, "completed", DateTimeOffset.UtcNow);
        var reopened = new TaskStore(Path.Combine(root, "tasks.db"));
        Check(!reopened.LoadSettings().AutomaticSyncEnabled && reopened.LoadSettings().LastSyncSummary?.CoursesRead == 2);
        Check(reopened.LoadSettings().LastSyncAtUtc is not null && reopened.LoadTasks().Count == 2);

        var http = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<script>var fid='901';</script>") });
        using (var httpReader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), http))
        {
            Check((await httpReader.ReadAsync(new(ChaoxingSyncParser.Portal), CancellationToken.None)).Outcome == "ok");
            Check(http.Methods.Single() == HttpMethod.Get);
            Check((await httpReader.ReadAsync(new("https://evil.example/base"), CancellationToken.None)).Outcome == "unsupported_redirect");
            Check(http.Methods.Count == 1);
        }
        await HttpOutcome(HttpStatusCode.Unauthorized, "login_required");
        await HttpOutcome(HttpStatusCode.Forbidden, "forbidden");
        await HttpOutcome(HttpStatusCode.TooManyRequests, "rate_limited");
        using (var login = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]),
            new CaptureHandler(_ => new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://passport2.chaoxing.com/login") } })))
            Check((await login.ReadAsync(new(ChaoxingSyncParser.Portal), CancellationToken.None)).Outcome == "login_required");
        using (var external = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]),
            new CaptureHandler(_ => new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/base") } })))
            Check((await external.ReadAsync(new(ChaoxingSyncParser.Portal), CancellationToken.None)).Outcome == "unsupported_redirect");
        using (var loginHtml = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]),
            new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<form id='loginForm'></form>") })))
            Check((await loginHtml.ReadAsync(new(ChaoxingSyncParser.Portal), CancellationToken.None)).Outcome == "login_required");
        using (var oversized = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]),
            new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('a', ChaoxingSyncParser.MaximumBytes + 1)) })))
            Check((await oversized.ReadAsync(new(ChaoxingSyncParser.Portal), CancellationToken.None)).Outcome == "response_too_large");
        const string modernEntry = "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/stu?courseid=101&clazzid=201&cpi=301&enc=SYNTHETIC%2BVALUE&t=123&pageHeader=1&v=2&hideHead=0";
        var modernHandler = new CaptureHandler(request => request.RequestUri!.AbsolutePath == "/visit/stucoursemiddle"
            ? new(HttpStatusCode.Found) { Headers = { Location = new Uri(modernEntry) } }
            : new(HttpStatusCode.OK) { Content = new StringContent(Signature) });
        var redirectEvidence = new List<ChaoxingRedirectObservation>();
        using (var modernReader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), modernHandler, redirectEvidence.Add))
        {
            var modern = await modernReader.ReadAsync(new(EntryA), CancellationToken.None);
            Check(modern.Outcome == "ok" && modern.StatusCode == 200 && modernHandler.Methods.Count == 2);
            Check(modern.Address.OriginalString == modernEntry);
            Check(ChaoxingSyncParser.ReadWorkListAddress(modern.Html, catalog.Courses[0]) is not null);
            Check(redirectEvidence.Single().TargetPath == "/mooc2-ans/mycourse/stu");
            var evidence = JsonSerializer.Serialize(redirectEvidence);
            Check(!evidence.Contains("SYNTHETIC", StringComparison.Ordinal) && !evidence.Contains("101", StringComparison.Ordinal));
        }
        Check(ChaoxingSyncParser.IsReadAddress(new(modernEntry)));
        Check(ChaoxingSyncParser.IsCourseRedirect(new(EntryA), new(modernEntry)));
        foreach (var wrongTarget in new[] { modernEntry.Replace("courseid=101", "courseid=999", StringComparison.Ordinal),
            modernEntry.Replace("clazzid=201", "clazzid=999", StringComparison.Ordinal),
            modernEntry.Replace("cpi=301", "cpi=999", StringComparison.Ordinal),
            modernEntry.Replace("&cpi=301", "", StringComparison.Ordinal) })
        {
            var wrongHandler = new CaptureHandler(_ => new(HttpStatusCode.Found) { Headers = { Location = new Uri(wrongTarget) } });
            using var wrongReader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), wrongHandler);
            Check((await wrongReader.ReadAsync(new(EntryA), CancellationToken.None)).Outcome == "unsupported_redirect"
                && wrongHandler.Methods.Count == 1);
        }
        var loopHandler = new CaptureHandler(request => new(HttpStatusCode.Found) { Headers = { Location = request.RequestUri } });
        using (var loopReader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), loopHandler))
            Check((await loopReader.ReadAsync(new(EntryA), CancellationToken.None)).Outcome == "unsupported_redirect"
                && loopHandler.Methods.Count == 6);
        foreach (var path in new[] { "prompt", "view" })
        {
            var target = TaskA.Replace("/work/task?", "/work/" + path + "?", StringComparison.Ordinal)
                .Replace("SYNTHETIC%2BONLY", "REFRESHED%2BVALUE%26ONLY", StringComparison.Ordinal);
            var evidence = new List<ChaoxingRedirectObservation>();
            var taskHandler = new CaptureHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/task", StringComparison.Ordinal)
                ? new(HttpStatusCode.Found) { Headers = { Location = new Uri(target) } }
                : new(HttpStatusCode.OK) { Content = new StringContent(path == "view" ? Detail : SubmittedPrompt) });
            using var taskReader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), taskHandler, evidence.Add);
            var page = await taskReader.ReadAsync(new(TaskA), CancellationToken.None);
            Check(page.Outcome == "ok" && taskHandler.Methods.Count == 2 && taskHandler.Methods.All(method => method == HttpMethod.Get));
            Check(page.Address.OriginalString == target && TaskStore.ObservedWorkKey(WorkListParser.ParseDetailLocation(page.Address.OriginalString))
                == TaskStore.ObservedWorkKey(WorkListParser.ParseTaskLocation(TaskA)));
            Check(WorkListParser.ParseTaskLocation(target) is null && ChaoxingSyncParser.IsReadAddress(new(target)));
            Check(evidence.Single().TargetPath == "/mooc-ans/mooc2/work/" + path
                && !JsonSerializer.Serialize(evidence).Contains("REFRESHED", StringComparison.Ordinal));
        }
        var viewTarget = TaskA.Replace("/work/task?", "/work/view?", StringComparison.Ordinal);
        foreach (var target in new[] { viewTarget.Replace("courseId=101", "courseId=999", StringComparison.Ordinal),
            viewTarget.Replace("classId=201", "classId=999", StringComparison.Ordinal),
            viewTarget.Replace("cpi=301", "cpi=999", StringComparison.Ordinal),
            viewTarget.Replace("workId=401", "workId=999", StringComparison.Ordinal),
            viewTarget.Replace("answerId=501", "answerId=999", StringComparison.Ordinal),
            viewTarget.Replace("&answerId=501", "", StringComparison.Ordinal),
            viewTarget.Replace("&cpi=301", "", StringComparison.Ordinal),
            viewTarget + "&courseId=101", viewTarget.Replace("enc=SYNTHETIC%2BONLY", "enc=", StringComparison.Ordinal),
            viewTarget.Replace("/work/view?", "/work/submit?", StringComparison.Ordinal),
            viewTarget.Replace("mooc1.chaoxing.com", "evil.example", StringComparison.Ordinal),
            TaskA.Replace("/work/task?", "/work/dowork?", StringComparison.Ordinal).Replace("workId=401", "workId=999", StringComparison.Ordinal) })
        {
            var wrongHandler = new CaptureHandler(_ => new(HttpStatusCode.Found) { Headers = { Location = new Uri(target) } });
            using var wrongReader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), wrongHandler);
            Check((await wrongReader.ReadAsync(new(TaskA), CancellationToken.None)).Outcome == "unsupported_redirect" && wrongHandler.Methods.Count == 1);
        }
        var actionTarget = TaskA.Replace("/work/task?", "/work/dowork?", StringComparison.Ordinal) + "&standardEnc=SYNTHETIC";
        var actionHandler = new CaptureHandler(_ => new(HttpStatusCode.Found) { Headers = { Location = new Uri(actionTarget) } });
        using (var actionReader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), actionHandler))
        {
            Check((await actionReader.ReadAsync(new(TaskA), CancellationToken.None)).Outcome == "detail_requires_user_action" && actionHandler.Methods.Count == 1);
            Check((await actionReader.ReadAsync(new(actionTarget), CancellationToken.None)).Outcome == "unsupported_redirect" && actionHandler.Methods.Count == 1);
        }
        Check(!ChaoxingSyncParser.IsReadAddress(new(actionTarget)) && WorkListParser.ParseDetailLocation(actionTarget) is null);
        var taskLoop = new CaptureHandler(request => new(HttpStatusCode.Found) { Headers = { Location = request.RequestUri } });
        using (var taskLoopReader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), taskLoop))
            Check((await taskLoopReader.ReadAsync(new(TaskA), CancellationToken.None)).Outcome == "unsupported_redirect" && taskLoop.Methods.Count == 6);
        Console.WriteLine($"PASS: {checks} automatic course discovery, background import, session expiry, cancellation and read-only HTTP checks (synthetic data only).");
    }

    private static void Check(bool condition)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("Automatic sync synthetic check failed: " + checks);
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Cancellation was expected."); }
        catch (OperationCanceledException) { checks++; }
    }
    private static async Task HttpOutcome(HttpStatusCode status, string outcome)
    {
        using var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]),
            new CaptureHandler(_ => new(status) { Content = new StringContent("") }));
        Check((await reader.ReadAsync(new(ChaoxingSyncParser.Portal), CancellationToken.None)).Outcome == outcome);
    }
    private sealed class CapturedProgress : IProgress<ChaoxingSyncProgress>
    {
        public List<ChaoxingSyncProgress> Values { get; } = [];
        public void Report(ChaoxingSyncProgress value) => Values.Add(value);
    }
    private sealed class FixtureReader : IChaoxingPageReader
    {
        public List<Uri> Addresses { get; } = [];
        public bool LoggedOut { get; init; }
        public bool DenySecondCourse { get; init; }
        public bool MissingSignature { get; init; }
        public bool UnknownList { get; init; }
        public bool WrongCourseLink { get; init; }
        public bool UnknownPaging { get; init; }
        public bool EmptyFirstCourse { get; init; }
        public string DetailMode { get; init; } = "task";
        public Task<ChaoxingPage> ReadAsync(Uri address, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Addresses.Add(address);
            string html;
            if (address.AbsolutePath == "/base")
                return Task.FromResult(new ChaoxingPage(LoggedOut ? "login_required" : "ok", 200, address, "<script>var fid='901';</script>"));
            if (address.AbsolutePath.Contains("courselistdata", StringComparison.Ordinal))
                html = CatalogHtml + (UnknownPaging ? "<div class='pagination'><a href='javascript:;' onclick='goPage(2)'>2</a></div>" : "");
            else if (address.AbsolutePath == "/visit/stucoursemiddle")
            {
                if (DenySecondCourse && address.Query.Contains("courseid=102", StringComparison.Ordinal)) return Task.FromResult(new ChaoxingPage("forbidden", 403, address, ""));
                html = MissingSignature ? "<input id='enc' value='incorrect'>" : Signature;
            }
            else if (address.AbsolutePath == "/mooc2/work/list")
                html = EmptyFirstCourse && address.Query.Contains("courseId=101", StringComparison.Ordinal)
                    ? "<div class='main task-list'><div class='has-content'><div class='null-data'>暂无作业</div></div></div>"
                    : UnknownList ? "<html>unknown</html>" : address.Query.Contains("courseId=101", StringComparison.Ordinal)
                    ? ListHtml("任务 A", WrongCourseLink ? TaskB : TaskA) : ListHtml("任务 B", TaskB);
            else if (address.AbsolutePath == "/mooc-ans/mooc2/work/task")
            {
                if (DetailMode == "action") return Task.FromResult(new ChaoxingPage("detail_requires_user_action", 302, address, ""));
                if (DetailMode is "view" or "prompt" or "wrong_work" or "wrong_answer")
                {
                    var target = address.OriginalString.Replace("/work/task?", "/work/" + (DetailMode == "prompt" ? "prompt" : "view") + "?", StringComparison.Ordinal);
                    if (DetailMode == "wrong_work") target = target.Replace("workId=", "workId=9", StringComparison.Ordinal);
                    if (DetailMode == "wrong_answer") target = target.Replace("answerId=501", "answerId=999", StringComparison.Ordinal);
                    return Task.FromResult(new ChaoxingPage("ok", 200, new(target), DetailMode == "prompt" ? SubmittedPrompt : Detail));
                }
                html = DetailMode == "unknown" ? "<html>unknown</html>" : Detail;
            }
            else throw new InvalidOperationException("Unexpected synthetic route.");
            return Task.FromResult(new ChaoxingPage("ok", 200, address, html));
        }
    }
    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Methods.Add(request.Method);
            return Task.FromResult(respond(request));
        }
    }
}
