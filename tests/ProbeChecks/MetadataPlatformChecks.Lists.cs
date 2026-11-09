using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessProbe;
using PlatformMetadataTestData;

internal static partial class MetadataPlatformChecks
{
    private static void ListParser()
    {
        var flip = MetadataPlatformParser.ZhihuishuCatalog(Fixtures.Catalog(), 1).Courses.Single();
        var shared = Fixtures.SharedCourseModel();
        var otherTerm = Fixtures.SharedCourseModel(recruit: "502");
        Check(shared.Id != otherTerm.Id && shared.Id != flip.Id, "shared course key includes recruit and differs from flip");
        var catalog = MetadataPlatformParser.ZhihuishuSharedCatalog(Fixtures.SharedCatalog([Fixtures.SharedCourse()]), 1, 0);
        Check(catalog is { Outcome: "ok", TotalRows: 1, TotalPages: 1 } && catalog.Courses.Single().Id == shared.Id, "shared catalog keeps verified API and recruit ids");
        Check(MetadataPlatformParser.ZhihuishuSharedCatalog(Fixtures.SharedCatalog(), 1, 1) is { Outcome: "ok", Courses.Count: 0, TotalPages: 0 }, "explicit completed-course empty list");
        var firstPage = Enumerable.Range(1, 5).Select(id => Fixtures.SharedCourse(recruit: id.ToString())).ToArray();
        Check(MetadataPlatformParser.ZhihuishuSharedCatalog(Fixtures.SharedCatalog(firstPage, 6), 1, 0).TotalPages == 2, "shared catalog page size is official five");
        Check(MetadataPlatformParser.ZhihuishuSharedCatalog(Fixtures.SharedCatalog([Fixtures.SharedCourse()], 6), 2, 0).Outcome == "ok", "shared last catalog page verified");
        foreach (var body in new[] { "{}", "{\"code\":200,\"result\":{}}", Fixtures.SharedCatalog([Fixtures.SharedCourse()], 6), Fixtures.SharedCatalog([Fixtures.SharedCourse(course: "bad/path")]) })
            Check(MetadataPlatformParser.ZhihuishuSharedCatalog(body, 1, 0).Outcome != "ok", "shared unknown or truncated catalog cannot be empty success");
        Check(MetadataPlatformParser.ZhihuishuSharedCatalog(Fixtures.SharedCatalog([Fixtures.SharedCourse(), Fixtures.SharedCourse(name: "冲突")]), 1, 0).Courses.Count == 0, "shared conflicting names excluded");
        Check(MetadataPlatformParser.ZhihuishuSharedProfile(Fixtures.SharedProfile(), Fixtures.Account) == "ok", "shared profile requires loginstatus and matching uuid");
        Check(MetadataPlatformParser.ZhihuishuSharedProfile(Fixtures.SharedProfile("other"), Fixtures.Account) == "account_mismatch", "shared cross-account session refused");
        Check(MetadataPlatformParser.ZhihuishuSharedProfile(Fixtures.SharedProfile(loggedIn: false), Fixtures.Account) == "login_required", "shared loginstatus false expires session");
        Check(MetadataPlatformParser.ZhihuishuSharedSchool(Fixtures.SharedSchool(), "shared-school") == Fixtures.SchoolId, "official school field");
        for (var type = 1; type <= 3; type++) Check(MetadataPlatformParser.ZhihuishuSharedExamType(Fixtures.ExamType(type)) == type, "official online room offline exam types");
        foreach (var body in new[] { "{\"status\":200,\"rt\":\"1\"}", Fixtures.ExamType(0), "{\"status\":200,\"rt\":{\"type\":1}}" })
            Check(MetadataPlatformParser.ZhihuishuSharedExamType(body) is null, "unknown exam type not guessed");
        for (var status = 1; status <= 5; status++) foreach (var kind in new[] { "homework", "exam" }) foreach (var paper in new long[] { 0, 21 })
        {
            var batch = MetadataPlatformParser.ZhihuishuFlipTasks(Fixtures.FlipList([Fixtures.FlipTask(status: status, paper: paper)]), Fixtures.Account, flip, kind, 1);
            var task = batch.Tasks.Single();
            Check(batch.ListComplete && batch.TotalRows == 1 && task.State == (status == 1 ? TaskState.Pending : status == 2 ? TaskState.Returned : TaskState.Completed), "flip official status mapping");
            Check(task.Link is not null && MetadataTaskNavigation.Matches(task), "flip pending and submitted task routes preserve identity");
            Check(task.DeadlineUtc == Fixtures.Now.AddHours(8).ToUniversalTime() && (kind == "exam" || task.BeginUtc is null), "flip homework has only confirmed endTime");
        }
        foreach (var dateStatus in new[] { 0, 1, 3 })
            Check(MetadataPlatformParser.ZhihuishuFlipTasks(Fixtures.FlipList([Fixtures.FlipTask(status: 1, dateStatus: dateStatus)]), Fixtures.Account, flip, "exam", 1).Tasks.Single().Link is null,
                "flip exam cannot enter outside official active date status");
        Check(MetadataPlatformParser.ZhihuishuFlipTasks(Fixtures.FlipList([Fixtures.FlipTask(submitOption: 2)]), Fixtures.Account, flip, "exam", 1).Tasks.Single().Link is null, "offline flip exam has no answering route");
        foreach (var body in new[] { "{\"status\":200,\"rt\":{}}", "{\"status\":200,\"rt\":{\"list\":null,\"rowCount\":0}}", Fixtures.FlipList([Fixtures.FlipTask()], 0) })
            Check(MetadataPlatformParser.ZhihuishuFlipTasks(body, Fixtures.Account, flip, "homework", 1).Outcome != "ok", "flip unknown list is not empty success");
        Check(MetadataPlatformParser.ZhihuishuFlipTasks(Fixtures.FlipList(), Fixtures.Account, flip, "homework", 1) is { Outcome: "ok", ListComplete: true }, "explicit empty flip task list");
        Check(MetadataPlatformParser.ZhihuishuFlipTasks(Fixtures.FlipList([Fixtures.FlipTask(), Fixtures.FlipTask(title: "冲突")]), Fixtures.Account, flip, "homework", 1).Tasks.Count == 0, "flip conflicting task removed");
        Check(MetadataPlatformParser.ZhihuishuFlipTasks(Fixtures.FlipList([Fixtures.FlipTask(course: "999")]), Fixtures.Account, flip, "homework", 1).UnsupportedRows == 1, "flip cross-course row excluded");
        for (var bucket = 1; bucket <= 2; bucket++) for (var status = 0; status <= 6; status++)
        {
            var batch = MetadataPlatformParser.ZhihuishuSharedTasks(Fixtures.SharedHomework([Fixtures.SharedTask(state: status)]), Fixtures.Account, shared, "shared-homework", bucket, Fixtures.SchoolId);
            var task = batch.Tasks.Single();
            TaskState? expected = bucket == 1 && status is 1 or 2 ? TaskState.Pending : bucket == 2 && status is 3 or 4 ? TaskState.Completed : null;
            Check(task.State == expected && (task.Link is null) == (expected is null), "shared state must agree with submission bucket");
            Check(task.Link is null || MetadataTaskNavigation.Matches(task), "shared encoded student task route roundtrips");
        }
        var returned = MetadataPlatformParser.ZhihuishuSharedTasks(Fixtures.SharedHomework([Fixtures.SharedTask(returned: 1)]), Fixtures.Account, shared, "shared-homework", 1, Fixtures.SchoolId).Tasks.Single();
        Check(returned.State == TaskState.Returned, "shared backStatus explicitly returns homework");
        var peer = MetadataPlatformParser.ZhihuishuSharedTasks(Fixtures.SharedHomework([Fixtures.SharedTask(review: 1, state: 3)]), Fixtures.Account, shared, "shared-homework", 2, Fixtures.SchoolId).Tasks.Single();
        Check(peer.State is null && peer.Link is null && peer.PlatformStatusText.Contains("互评", StringComparison.Ordinal), "peer-review state and entry need review");
        var more = MetadataPlatformParser.ZhihuishuSharedTasks(Fixtures.SharedHomework([Fixtures.SharedTask()], 0), Fixtures.Account, shared, "shared-homework", 1, Fixtures.SchoolId);
        Check(more.Tasks.Count == 1 && !more.ListComplete && more.Issues.Contains("shared_homework_more_pages"), "shared additional pages report partial verified first batch");
        foreach (var body in new[] { "{\"status\":200,\"rt\":{}}", "{\"status\":200,\"rt\":{\"studentHomeworkList\":[],\"next\":null}}", Fixtures.SharedHomework(next: -2) })
            Check(MetadataPlatformParser.ZhihuishuSharedTasks(body, Fixtures.Account, shared, "shared-homework", 1, Fixtures.SchoolId).Outcome != "ok", "missing shared cursor rejected");
        foreach (var bad in new[] { Fixtures.SharedTask(course: "999"), Fixtures.SharedTask(recruit: "999"), Fixtures.SharedTask(id: "a?b"), Fixtures.SharedTask(title: "") })
            Check(MetadataPlatformParser.ZhihuishuSharedTasks(Fixtures.SharedHomework([bad]), Fixtures.Account, shared, "shared-homework", 1, Fixtures.SchoolId).UnsupportedRows == 1, "shared cross-course or malformed row rejected");
        foreach (var bucket in new[] { 1, 2 })
        {
            var exam = MetadataPlatformParser.ZhihuishuSharedTasks(Fixtures.SharedExams(Fixtures.SharedTask(state: bucket == 1 ? 1 : 4)), Fixtures.Account, shared, "shared-exams", bucket, Fixtures.SchoolId).Tasks.Single();
            Check(exam.State == (bucket == 1 ? TaskState.Pending : TaskState.Completed) && exam.Link is null, "exam metadata retained without bypassing official verification");
        }
        Check(MetadataPlatformParser.ZhihuishuSharedTasks("{\"status\":200,\"rt\":{}}", Fixtures.Account, shared, "shared-exams", 1, Fixtures.SchoolId) is { Outcome: "ok", ListComplete: true }, "official exam empty object contract");
        Check(MetadataPlatformParser.ZhihuishuSharedTasks("{\"status\":200,\"rt\":{\"other\":[]}}", Fixtures.Account, shared, "shared-exams", 1, Fixtures.SchoolId).Outcome != "ok", "nonempty unknown exam object rejected");
        var timed = MetadataPlatformParser.ZhihuishuSharedTasks(Fixtures.SharedHomework([Fixtures.SharedTask()]), Fixtures.Account, shared, "shared-homework", 1, Fixtures.SchoolId).Tasks.Single();
        Check(timed.DeadlineUtc == Fixtures.Now.AddHours(8).ToUniversalTime(), "shared Chinese wall time converted as UTC plus eight");
        var inverted = MetadataPlatformParser.ZhihuishuSharedTasks(Fixtures.SharedHomework([Fixtures.SharedTask(start: "2026-10-29 10:00:00", end: "2026-10-28 10:00:00")]), Fixtures.Account, shared, "shared-homework", 1, Fixtures.SchoolId).Tasks.Single();
        Check(inverted.DeadlineUtc is null && inverted.BeginUtc is null, "shared inverted interval needs review");
        foreach (var suffix in new[] { "?redirect=https://evil.example", "?meetCourseType=9", "/extra", "&state=3" })
            Check(MetadataTaskNavigation.Key("zhihuishu", timed.Link + suffix) is null, "unverified shared fragment query rejected");
        Check(MetadataTaskNavigation.Key("zhihuishu", timed.Link!.Replace("onlineexamh5new.zhihuishu.com", "onlineexamh5new.zhihuishu.com.evil.example", StringComparison.Ordinal)) is null, "spoofed shared task host rejected");
        Check(!JsonSerializer.Serialize(timed).Contains(Fixtures.Account, StringComparison.Ordinal) && !JsonSerializer.Serialize(timed).Contains("synthetic-answer-not-read", StringComparison.Ordinal), "shared metadata excludes identities and answer body");
        Check(MetadataPlatformParser.EnvelopeOutcome(Zhs, "shared-homework", "{\"status\":200,\"code\":-1,\"rt\":{}}") == "course_access_denied", "course access denial is separate from login expiry");
        SchoolExamParser();
    }

    private static async Task ListSync()
    {
        var root = Path.Combine(Path.GetTempPath(), "metadata-lists-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = FixtureTaskStore.Create(Path.Combine(root, "tasks.db")); var reader = new Fixtures.Reader { IncludeCourseLists = true };
        var summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.Tasks == Fixtures.FullTaskCount && summary.Courses == 3 && summary.FlipCourses == 1 && summary.SharedCourses == 2
            && summary.CoursesRead == 3 && summary.FullTaskListsCovered, "full shared and flip lists merge to unified task store");
        Check(summary.TasksWithDeadline == 13 && summary.TasksWithLink == 9 && summary.StatusNeedsReview == 0, "shared exam entry limitations reflected in counts");
        Check(store.LoadTasks().Count(task => task.State == TaskState.Completed) == 6, "submitted tasks use official list state");
        Check(store.LoadCourseRecording(Zhs.Id).Courses.Count == 3 && store.LoadTasks().Select(task => task.RecordingCourseKey).Distinct().Count() == 3, "shared two terms and flip select independently");
        Check(store.LoadTasks().Select(task => task.SourceKey).Distinct().Count() == 13, "shared semester account task keys are unique");
        var completed = store.LoadTasks().Single(task => task.PlatformTitle == "合成作业");
        Check(completed.State == TaskState.Completed && completed.AutomaticMetadataVersion == 2, "course list replaces reminder state for same historical source id");
        reader.Response = request => request.Operation == "flip-homework" ? new("ok", 200, "{\"status\":200,\"rt\":{}}") : null;
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && store.Get(completed.Id)!.State == TaskState.Completed, "reminder cannot regress submitted task when course list unavailable");
        reader.Response = request => request.Operation == "shared-homework" ? new("ok", 200, Fixtures.SharedHomework([request.Bucket == 1
            ? Fixtures.SharedTask(recruit: request.Course!.RecruitId) : Fixtures.SharedTask("submitted==", "302", "共享已提交作业", 3, recruit: request.Course!.RecruitId, meet: 1)], request.Bucket == 1 ? 1 : -1)) : null;
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.Problems.Any(problem => problem.Code == "ZHS006") && store.LoadTasks().Count == 13,
            "shared cursor leaves partial coverage and verified first-batch tasks");
        reader.Response = request => request.Operation == "shared-exam-type" ? new("ok", 200, Fixtures.ExamType(2)) : null;
        reader.Requests.Clear(); summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.FullTaskListsCovered && reader.Requests.Count(request => request.Operation == "shared-room-exam") == 4
            && reader.Requests.Count(request => request.Operation == "shared-makeup") == 2 && reader.Requests.All(request => request.Operation != "shared-exams"), "room empty lists and explicit no makeup follow official branch");
        reader.Response = request => request.Operation == "shared-profile" ? new("ok", 200, Fixtures.SharedProfile("other")) : null;
        reader.Requests.Clear(); summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.Problems.Any(problem => problem.Code == "ZHS003") && reader.Requests.All(request => request.Operation is not ("shared-homework" or "shared-exams")), "cross-account shared profile blocks task reads");
        reader.Response = request => request.Operation == "shared-homework" ? new("ok", 200, Fixtures.SharedHomework([Fixtures.SharedTask("conflict", "999", state: request.Bucket == 1 ? 1 : 3, recruit: request.Course!.RecruitId)])) : null;
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && store.LoadTasks().Count == 13, "same shared task in both buckets rejected before storage");
        reader.Response = request => request.Operation == "flip-homework" ? new("ok", 200, Fixtures.FlipList([Fixtures.FlipTask("999", "分页任务")], 21)) : null;
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.Problems.Any(problem => problem.Code == "ZHS005") && store.LoadTasks().Count == 13, "repeated flip pages staged without partial overwrite");
        reader.Response = request => request.Operation == "flip-homework" ? new("ok", 200, Fixtures.FlipList(request.Page == 1
            ? Enumerable.Range(1, 20).Select(id => Fixtures.FlipTask("page-" + id, "第一页 " + id)).ToArray()
            : [Fixtures.FlipTask("page-21", "第二页")], 21)) : request.Operation == "reminders" ? new("ok", 200, Fixtures.Reminders()) : null;
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.FullTaskListsCovered && store.LoadTasks().Count(task => task.PlatformTitle.StartsWith("第一页", StringComparison.Ordinal) || task.PlatformTitle == "第二页") == 21, "full flip two-page list imports beyond homepage reminders");
        // Change rowCount on the next page, without ever accepting staged task rows.
        reader.Response = request => request.Operation == "flip-homework" ? new("ok", 200, Fixtures.FlipList([Fixtures.FlipTask("truncated-" + request.Page, "缺页任务")], request.Page == 1 ? 21 : 22)) : null;
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && store.LoadTasks().All(task => task.PlatformTitle != "缺页任务"), "changed flip total refuses staged rows");
        reader.Response = null; var recording = store.LoadCourseRecording(Zhs.Id);
        var selectedKey = CourseRecordingRules.MetadataKey(Zhs.Id, Fixtures.Account, Fixtures.SharedCourseModel().Id, "shared");
        store.SaveCourseRecording(Zhs.Id, false, [selectedKey], true, recording.Revision, Fixtures.Now); reader.Requests.Clear();
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.SelectedCourses == 1 && summary.Tasks == 4 && reader.Requests.All(request => request.Operation is not ("flip-homework" or "flip-exam")
            && (request.Course is null || request.Course.RecruitId == "501")), "course selection only requests chosen shared term");
        recording = store.LoadCourseRecording(Zhs.Id); store.SaveCourseRecording(Zhs.Id, true, [], true, recording.Revision, Fixtures.Now);
        reader.AccountId = "synthetic-account-02"; summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.Added == 13 && store.LoadTasks().Select(task => task.SourceKey).Distinct().Count() == store.LoadTasks().Count, "new account cannot merge identical shared student task ids");
        reader.Requests.Clear(); await new MetadataSynchronizer(Zhs, store, reader).RunAsync(catalogOnly: true);
        Check(reader.Requests.All(request => request.Operation is "profile" or "courses" or "shared-courses" or "smart-courses" or "agent-courses"), "catalog refresh never reads tasks or verification endpoints");
        Check(summary.Problems.All(problem => !problem.Message.Contains("???", StringComparison.Ordinal)), "synchronizer messages remain readable Chinese");
        Check(!File.ReadAllText(store.DatabasePath).Contains("synthetic-answer-not-read", StringComparison.Ordinal), "full-list metadata never saves answer body");
        Directory.Delete(root, true);
        await SchoolExamSync();
    }

    private static Dictionary<string, string> Form(string text) => text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
    private static JsonDocument Decode(string secret, byte[] key)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return JsonDocument.Parse(aes.DecryptCbc(Convert.FromBase64String(secret), Encoding.UTF8.GetBytes("1g3qqdh4jvbskb9x")));
    }
}
