using System.Net;
using System.Text;
using System.Text.Json;
using AccessProbe;
using Microsoft.Data.Sqlite;

internal static class CourseRecordingChecks
{
    private static int checks;
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "course-recording-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Storage(Path.Combine(root, "storage.db"));
        await Mooc(Path.Combine(root, "mooc.db"));
        foreach (var platform in RainPlatform.All) await Rain(Path.Combine(root, platform.Id + ".db"), platform);
        await Chaoxing(Path.Combine(root, "chaoxing.db"));
        Console.WriteLine($"PASS: {checks} course recording, platform isolation, catalog-only refresh, excluded-request, retained-task and reminder checks (synthetic only).");
    }

    private static void Storage(string path)
    {
        var store = new TaskStore(path); var keys = new Dictionary<string, string>();
        foreach (var site in PlatformCatalog.Sites)
        {
            var task = store.Create(new("手动任务", site.Id, "同名课程", DeadlineKind.Known, null, Now.AddHours(1), null, null, "", "备注", null), Now);
            var overview = store.LoadCourseRecordingOverview(site.Id);
            Check(overview.RecordAllCourses && overview.Courses.Count == 1, "every registered platform exposes its local courses");
            keys[site.Id] = overview.Courses[0].Key;
            store.SaveCourseRecording(site.Id, false, [], true, overview.Revision, Now);
            Check(store.LoadTasks().Any(row => row.Id == task.Id) && store.LoadRecordedTasks().All(row => row.Id != task.Id), "deselection hides but retains task");
            Check(store.LoadReminderHistory().All(item => item.Plan.TaskId != task.Id), "deselection cancels unsent plans");
            var current = store.LoadCourseRecording(site.Id);
            Reject(() => store.SaveCourseRecording(site.Id, false, ["wrong"], true, current.Revision, Now), "COURSE001");
            Reject(() => store.SaveCourseRecording(site.Id, false, [], true, 0, Now), "COURSE002");
            var restored = store.SaveCourseRecording(site.Id, false, [keys[site.Id]], true, current.Revision, Now);
            Check(store.LoadRecordedTasks().Any(row => row.Id == task.Id) && store.LoadReminderHistory().Any(item => item.Plan.TaskId == task.Id), "reselection restores task and timed reminder");
            store.SaveCourseRecording(site.Id, false, [], false, restored.Revision, Now);
            Check(store.LoadRecordedTasks().Any(row => row.Id == task.Id) && store.TakeDue(Now.AddMinutes(1)).All(item => item.Task.Id != task.Id), "retained display does not enable excluded reminders");
        }
        Check(keys.Values.Distinct().Count() == PlatformCatalog.Sites.Count, "same course names are isolated by platform");
        var reopened = new TaskStore(path);
        Check(PlatformCatalog.Sites.All(site => !reopened.LoadCourseRecording(site.Id).RecordAllCourses && !reopened.LoadCourseRecording(site.Id).HideUnselectedTasks), "selection mode and retention persist across reopen");
        var course = new MoocCourse("无任务课程", "1001", "2001", "SYNTHETIC", 0);
        var discovered = CourseRecordingRules.Mooc("9001", course);
        store.ObserveRecordingCourses("mooc", [discovered], true, Now);
        var first = store.LoadCourseRecording("mooc");
        Check(first.Courses.Any(row => row.Key == discovered.Course.Key) && !store.IsCourseRecorded("mooc", discovered.Course.Key), "explicit selection leaves new courses unchecked");
        var second = CourseRecordingRules.Mooc("9001", course with { TermId = "2002" });
        store.ObserveRecordingCourses("mooc", [second], false, Now);
        Check(store.LoadCourseRecording("mooc").Courses.Any(row => row.Key == discovered.Course.Key && row.Available), "partial catalogs do not erase prior courses");
        store.ObserveRecordingCourses("mooc", [second], true, Now);
        Check(store.LoadCourseRecording("mooc").Courses.Any(row => row.Key == discovered.Course.Key && !row.Available), "complete refresh retains removed course history");
        Check(discovered.Course.Key != second.Course.Key && discovered.Course.Key != CourseRecordingRules.Mooc("9002", course).Course.Key, "term and account isolate course identity");
        Check(CourseRecordingRules.RainKey("yuketang", "www.yuketang.cn", "77", "101") != CourseRecordingRules.RainKey("yuketang", "pro.yuketang.cn", "77", "101"), "rain host isolates course identity");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { store.ObserveRecordingCourses("mooc", [], true, Now, canceled.Token); throw new Exception("cancellation ignored"); }
        catch (OperationCanceledException) { checks++; }
    }

    private static string MoocCatalog(int count = 2) => JsonSerializer.Serialize(new
    {
        code = 0, result = new { pagination = new { totlePageCount = 1, totleCount = count, pageIndex = 1 },
            result = Enumerable.Range(1, count).Select(index => new { id = 1000 + index, name = "同名课程", mode = 0,
                termPanel = new { id = 2000 + index, courseId = 1000 + index }, schoolPanel = new { shortName = "SYNTHETIC" } }).ToArray() }
    });

    private static string MoocTerm(string termId) => JsonSerializer.Serialize(new
    {
        code = 0, result = new { mocTermDto = new { id = termId,
            quizs = new[] { new { id = int.Parse(termId) + 100, name = "待办测验", usedTryCount = 0, deadline = Now.AddHours(1).ToUnixTimeMilliseconds() } } } }
    });

    private static async Task Mooc(string path)
    {
        var store = new TaskStore(path); var reader = new MoocReader();
        var catalog = await new MoocSynchronizer(store, reader, "9001").RunAsync(catalogOnly: true);
        Check(catalog.Courses == 2 && catalog.CoursesRead == 0 && reader.Paths.Count == 1 && store.LoadTasks().Count == 0, "MOOC catalog-only never reads tasks");
        var overview = store.LoadCourseRecordingOverview("mooc");
        Check(overview.Courses.Count == 2 && overview.Courses.Select(course => course.Key).Distinct().Count() == 2, "MOOC same names remain distinct");
        var selected = CourseRecordingRules.MoocKey("9001", "1001", "2001");
        store.SaveCourseRecording("mooc", false, [selected], true, overview.Revision, Now); reader.Paths.Clear();
        var one = await new MoocSynchronizer(store, reader, "9001").RunAsync();
        Check(one.SelectedCourses == 1 && one.Courses == 2 && one.CoursesRead == 1 && one.Added == 1 && one.Problems.Count == 0, "MOOC sync covers selected courses");
        Check(reader.Paths.Count == 3 && reader.TermIds.All(id => id == "2001"), "MOOC skips every excluded term request");
        var task = store.LoadTasks().Single();
        task = store.Edit(task.Id, task.Revision, new("我的标题", "mooc", "自定义课程名", DeadlineKind.Known, null, task.DeadlineUtc, task.Link, null, "", "保留备注", null), Now);
        store.SetState(task.Id, task.Revision, TaskState.Ignored, Now);
        var before = store.LoadCourseRecording("mooc"); store.SaveCourseRecording("mooc", false, [], true, before.Revision, Now); reader.Paths.Clear();
        var none = await new MoocSynchronizer(store, reader, "9001").RunAsync();
        Check(none.SelectedCourses == 0 && none.Tasks == 0 && reader.Paths.Count == 1 && store.LoadRecordedTasks().Count == 0, "zero-selected MOOC only reads catalog");
        Check(store.LoadTasks().Single().Notes == "保留备注" && store.LoadTasks().Single().State == TaskState.Ignored, "excluded history retains manual state and metadata");
        before = store.LoadCourseRecording("mooc"); store.SaveCourseRecording("mooc", false, [selected], true, before.Revision, Now);
        var again = await new MoocSynchronizer(store, reader, "9001").RunAsync();
        Check(again.Added == 0 && again.Updated == 1 && store.LoadRecordedTasks().Single().CourseName == "自定义课程名", "reselect updates same task without changing manual course name");
        reader.Count = 3; await new MoocSynchronizer(store, reader, "9001").RunAsync(catalogOnly: true);
        Check(store.LoadCourseRecording("mooc").Courses.Count(course => course.Available) == 3 && store.LoadCourseRecording("mooc").SelectedCourseKeys.Count == 1, "new MOOC course is not silently selected");
        before = store.LoadCourseRecording("mooc"); store.SaveCourseRecording("mooc", true, [], true, before.Revision, Now);
        Check((await new MoocSynchronizer(store, reader, "9001").RunAsync()).SelectedCourses == 3, "all-course mode includes new course");
        reader.Count = 2; before = store.LoadCourseRecording("mooc");
        reader.BeforeTerm = () => store.SaveCourseRecording("mooc", false, [], true, before.Revision, Now);
        var oldCount = store.LoadTasks().Count;
        try { await new MoocSynchronizer(store, reader, "9001").RunAsync(); throw new Exception("late import accepted"); }
        catch (CourseRecordingChangedException) { Check(store.LoadTasks().Count == oldCount, "selection change rejects in-flight task response"); }
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE tasks SET payload=json_set(payload,'$.RecordingCourseKey','')"; command.ExecuteNonQuery();
        }
        var courses = Enumerable.Range(1, 2).Select(index => new MoocCourse("同名课程", (1000 + index).ToString(), (2000 + index).ToString(), "SYNTHETIC", 0)).ToArray();
        store.ObserveRecordingCourses("mooc", courses.Select(course => CourseRecordingRules.Mooc("9001", course)).ToArray(), true, Now);
        Check(store.LoadTasks().Count(task => task.RecordingCourseKey.Length > 0) == 2, "old MOOC tasks bind by identity despite edited names");
    }

    private static async Task Rain(string path, RainPlatform platform)
    {
        var store = new TaskStore(path); var reader = new RainReader(platform);
        var catalog = await new RainSynchronizer(store, reader, platform, platform.DefaultHost).RunAsync(catalogOnly: true);
        Check(catalog.Courses == 2 && reader.Paths.Count == 2 && store.LoadTasks().Count == 0, "rain catalog refresh only reads identity and courses");
        var selected = CourseRecordingRules.RainKey(platform.Id, platform.DefaultHost, "77", "101");
        var current = store.LoadCourseRecording(platform.Id); store.SaveCourseRecording(platform.Id, false, [selected], true, current.Revision, Now);
        reader.Paths.Clear();
        var summary = await new RainSynchronizer(store, reader, platform, platform.DefaultHost).RunAsync();
        Check(summary.SelectedCourses == 1 && summary.Courses == 2 && summary.CoursesRead == 1 && summary.Added == 1 && summary.Problems.Count == 0, "rain selected course is complete");
        Check(reader.Paths.All(path => !path.Contains("102", StringComparison.Ordinal)) && store.LoadTasks().Single().RecordingCourseKey == selected, "rain makes no excluded classroom or activity request");
        current = store.LoadCourseRecording(platform.Id); store.SaveCourseRecording(platform.Id, false, [], true, current.Revision, Now); reader.Paths.Clear();
        var none = await new RainSynchronizer(store, reader, platform, platform.DefaultHost).RunAsync();
        Check(none.SelectedCourses == 0 && reader.Paths.Count == 2 && store.LoadRecordedTasks().Count == 0 && store.LoadTasks().Count == 1, "rain empty selection retains hidden history");
        current = store.LoadCourseRecording(platform.Id); store.SaveCourseRecording(platform.Id, false, [selected], true, current.Revision, Now);
        Check((await new RainSynchronizer(store, reader, platform, platform.DefaultHost).RunAsync()).Updated == 1, "rain reselect never duplicates task");
    }

    private static async Task Chaoxing(string path)
    {
        var store = new TaskStore(path); var reader = new ChaoxingReader();
        var catalog = await new ChaoxingSynchronizer(store, reader, TimeSpan.Zero).RunAsync(null, CancellationToken.None, true);
        Check(catalog.Courses == 2 && reader.Paths.Count == 2 && store.LoadTasks().Count == 0, "chaoxing refresh does not request work signatures or lists");
        var selected = CourseRecordingRules.ChaoxingKey("101", "201", "301");
        var current = store.LoadCourseRecording("chaoxing"); store.SaveCourseRecording("chaoxing", false, [selected], true, current.Revision, Now); reader.Paths.Clear();
        var summary = await new ChaoxingSynchronizer(store, reader, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
        Check(summary.SelectedCourses == 1 && summary.Courses == 2 && summary.CoursesRead == 1 && summary.Added == 1 && summary.Problems.Count == 0, "chaoxing only selected course imports");
        Check(reader.Paths.All(address => !address.Contains("102", StringComparison.Ordinal)) && store.LoadTasks().Single().RecordingCourseKey == selected, "chaoxing excludes course and detail requests");
        current = store.LoadCourseRecording("chaoxing"); store.SaveCourseRecording("chaoxing", false, [], true, current.Revision, Now); reader.Paths.Clear();
        var none = await new ChaoxingSynchronizer(store, reader, TimeSpan.Zero).RunAsync(null, CancellationToken.None);
        Check(none.SelectedCourses == 0 && reader.Paths.Count == 2 && store.LoadTasks().Count == 1 && store.LoadRecordedTasks().Count == 0, "zero-selected chaoxing keeps history without collecting details");
        current = store.LoadCourseRecording("chaoxing"); store.SaveCourseRecording("chaoxing", false, [selected], true, current.Revision, Now);
        Check((await new ChaoxingSynchronizer(store, reader, TimeSpan.Zero).RunAsync(null, CancellationToken.None)).Updated == 1, "chaoxing reselect updates existing task");
    }

    private static void Check(bool passed, string label) { if (!passed) throw new InvalidOperationException("Course recording: " + label); checks++; }
    private static void Reject(Action action, string code)
    { try { action(); throw new Exception("rejection expected"); } catch (TaskValidationException error) { Check(error.Code == code, "specific selection error"); } }

    private sealed class MoocReader : IMoocMetadataReader
    {
        public int Count { get; set; } = 2;
        public List<string> Paths { get; } = [];
        public List<string> TermIds { get; } = [];
        public Action? BeforeTerm { get; set; }
        public Task<MoocResponse> ReadAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Paths.Add(path);
            if (path == MoocPlatform.CourseRpc) return Task.FromResult(new MoocResponse("ok", 200, MoocCatalog(Count)));
            TermIds.Add(fields["termId"]);
            if (path == MoocPlatform.LearnedTermRpc) { var callback = BeforeTerm; BeforeTerm = null; callback?.Invoke(); }
            return Task.FromResult(new MoocResponse("ok", 200, path == MoocPlatform.NewExamRpc ? "{\"code\":0,\"result\":[]}" : MoocTerm(fields["termId"])));
        }
    }

    private sealed class RainReader(RainPlatform platform) : IRainMetadataReader
    {
        public List<string> Paths { get; } = [];
        public Task<RainResponse> ReadAsync(Uri address, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Paths.Add(address.PathAndQuery);
            Check(address.IdnHost == platform.DefaultHost, "rain reader host follows platform");
            var json = address.AbsolutePath switch
            {
                "/v2/api/web/userinfo" => """{"errcode":0,"data":[{"user_id":77}]}""",
                "/v2/api/web/courses/list" => """{"errcode":0,"data":{"list":[{"id":101,"name":"一班","course_name":"同名课"},{"id":102,"name":"二班","course_name":"同名课"}]}}""",
                "/v2/api/web/classrooms/101" => """{"errcode":0,"data":{"id":101,"name":"一班","course_name":"同名课"}}""",
                "/mooc-api/v1/lms/learn/course/schedule" => """{"success":true,"data":{}}""",
                _ => """{"errcode":0,"data":{"activities":[{"type":4,"courseware_id":601,"title":"测验","status":0}],"has_more":false}}"""
            };
            return Task.FromResult(new RainResponse("ok", 200, json));
        }
    }

    private sealed class ChaoxingReader : IChaoxingPageReader
    {
        public List<string> Paths { get; } = [];
        public Task<ChaoxingPage> ReadAsync(Uri address, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Paths.Add(address.AbsoluteUri);
            string html;
            if (address.AbsolutePath == "/base") html = "<input id='fid' value='901'>";
            else if (address.AbsolutePath.Contains("courselist", StringComparison.Ordinal))
                html = "<div class='course-list'>" + string.Join("", Enumerable.Range(1, 2).Select(index =>
                    "<a href='https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid=" + (100 + index) + "&amp;clazzid=" + (200 + index) + "&amp;cpi=" + (300 + index)
                    + "'><span class='course-name'>同名课</span></a>")) + "</div>";
            else if (address.AbsolutePath.Contains("stucoursemiddle", StringComparison.Ordinal)) html = "<input id='workEnc' value='SYNTHETIC'>";
            else if (address.AbsolutePath.EndsWith("/list", StringComparison.Ordinal))
                html = "<ul id='workList'><li data='https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&amp;classId=201&amp;cpi=301&amp;workId=401&amp;enc=SYNTHETIC' onclick='goTask(this);'><div class='right-content'><p class='overHidden2'>任务</p><p class='status'>未交</p></div><div class='time'>剩余48小时</div></li></ul>";
            else return Task.FromResult(new ChaoxingPage("detail_requires_user_action", 200, address, ""));
            return Task.FromResult(new ChaoxingPage("ok", 200, address, html));
        }
    }
}
