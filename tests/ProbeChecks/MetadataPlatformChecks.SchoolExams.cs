using System.Text.Json;
using AccessProbe;
using PlatformMetadataTestData;

internal static partial class MetadataPlatformChecks
{
    private static void SchoolExamParser()
    {
        var course = Fixtures.SharedCourseModel();
        foreach (var operation in new[] { "shared-room-exam", "shared-offline-exam" }) foreach (var bucket in new[] { 1, 2 })
        {
            foreach (var body in new[] { Fixtures.SchoolExam(), "{\"status\":200,\"rt\":{}}" })
                Check(MetadataPlatformParser.ZhihuishuSchoolExam(body, Fixtures.Account, course, operation, bucket) is { Outcome: "ok", ListComplete: true, Tasks.Count: 0 },
                    "official school exam null and empty object are explicit empty lists");
            foreach (var body in new[] { "{\"status\":200}", "{\"status\":200,\"rt\":[]}", "{\"status\":200,\"rt\":false}", "{\"status\":200,\"rt\":{\"other\":[]}}", "<html>login</html>" })
                Check(MetadataPlatformParser.ZhihuishuSchoolExam(body, Fixtures.Account, course, operation, bucket).Outcome != "ok",
                    "school exam unknown shape and login HTML cannot mean no exams");
            for (var status = 0; status <= 6; status++)
            {
                var room = operation == "shared-room-exam";
                var batch = MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(room ? Fixtures.RoomExam(state: status) : Fixtures.OfflineExam(state: status)),
                    Fixtures.Account, course, operation, bucket);
                var task = batch.Tasks.Single();
                TaskState? expected = !room ? null : bucket == 1 && status is 1 or 2 ? TaskState.Pending : bucket == 1 && status == 5 ? TaskState.Returned
                    : bucket == 2 && status is 3 or 4 ? TaskState.Completed : null;
                Check(task.State == expected && task.StateEvidence == (expected is null ? "unknown" : "explicit"), "school exam state requires confirmed mode and submission bucket");
                Check(task.Link is null && task.MetadataVersion == 2 && task.ExamMode == (room ? "room" : "offline"), "school exam retains provenance without answering entry");
                Check(room ? task.DeadlineUtc == Fixtures.Now.AddHours(8).ToUniversalTime()
                    : task.BeginUtc is null && task.DeadlineUtc is null && task.TimeText.Contains("以学校安排为准", StringComparison.Ordinal),
                    "only confirmed room dates are parsed; offline dates are not guessed");
                Check(batch.ListComplete && batch.Rows == 1 && batch.UnsupportedRows == 0, "school exam single object list traversal differs from missing state or link");
                Check(!JsonSerializer.Serialize(task).Contains(Fixtures.Account, StringComparison.Ordinal)
                    && !JsonSerializer.Serialize(task).Contains("synthetic-answer-not-read", StringComparison.Ordinal)
                    && !JsonSerializer.Serialize(task).Contains("synthetic-feedback-not-read", StringComparison.Ordinal), "school exam metadata omits account answer and feedback bodies");
            }
        }
        foreach (var row in new[] { Fixtures.RoomExam(course: "999"), Fixtures.RoomExam(recruit: "999"), Fixtures.RoomExam(title: ""),
            new { id = "student", examId = "701", examName = "归属冲突", uuid = "other-account" } })
        {
            var batch = MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(row), Fixtures.Account, course, "shared-room-exam", 1);
            Check(batch.Tasks.Count == 0 && batch.UnsupportedRows == 1 && batch.UnidentifiedExamRows == 0, "school exam course term account and name conflicts are excluded");
        }
        foreach (var row in new[] { Fixtures.OfflineExam(id: ""), Fixtures.OfflineExam(exam: ""), Fixtures.OfflineExam(id: "bad?student"),
            new { examName = "仅有学校安排", state = 1 } })
        {
            var batch = MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(row), Fixtures.Account, course, "shared-offline-exam", 1);
            Check(batch.Tasks.Count == 0 && !batch.ListComplete && batch.UnidentifiedExamRows == 1 && batch.Issues.Contains("exam_identity_unverified"),
                "unidentified offline arrangement remains partial without fabricating a task id");
        }
        foreach (var model in new[] { course with { Id = "mismatched" }, course with { RecruitId = "502" }, course with { Kind = "flip" } })
            Check(MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(), Fixtures.Account, model, "shared-room-exam", 1).Outcome != "ok",
                "school exam validates course context even for empty response");
        foreach (var bucket in new[] { 0, 3 })
            Check(MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(), Fixtures.Account, course, "shared-room-exam", bucket).Outcome != "ok", "school exam bucket must be official submission tab");
        var assigned = MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(Fixtures.RoomExam()), Fixtures.Account, course, "shared-room-exam", 1).Tasks.Single();
        Check(assigned.TimeText.Contains("合成教学楼 301 机房", StringComparison.Ordinal), "school assigned room address retained as plain metadata");
        var schoolArranges = MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(Fixtures.RoomExam(arrange: 0, address: "不可使用的地点")),
            Fixtures.Account, course, "shared-room-exam", 1).Tasks.Single();
        Check(schoolArranges.TimeText.Contains("以学校安排为准", StringComparison.Ordinal) && !schoolArranges.TimeText.Contains("不可使用的地点", StringComparison.Ordinal),
            "unassigned room does not expose an inactive address");
        var inverted = MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(Fixtures.RoomExam(start: "2026-10-29 10:00:00", end: "2026-10-28 10:00:00")),
            Fixtures.Account, course, "shared-room-exam", 1).Tasks.Single();
        Check(inverted.BeginUtc is null && inverted.DeadlineUtc is null && inverted.State == TaskState.Pending, "invalid room interval cannot change submission state");
        var expired = MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(Fixtures.RoomExam(start: "2026-09-01 10:00:00", end: "2026-09-02 10:00:00")),
            Fixtures.Account, course, "shared-room-exam", 1).Tasks.Single();
        Check(expired.State == TaskState.Pending, "past room date never implies submitted");
        Check(MetadataPlatformParser.ZhihuishuHasMakeupExam(Fixtures.Makeup(0)) == false && MetadataPlatformParser.ZhihuishuHasMakeupExam(Fixtures.Makeup(1)) == true,
            "makeup discriminator accepts official zero and one");
        foreach (var body in new[] { Fixtures.Makeup(2), "{\"status\":200,\"rt\":\"0\"}", "{\"status\":200,\"rt\":null}", "{\"status\":200}",
            "{\"status\":200,\"rt\":false}", "{\"status\":403,\"rt\":0}" })
            Check(MetadataPlatformParser.ZhihuishuHasMakeupExam(body) is null, "unknown or expired makeup flag never means no makeup exam");
        var otherTerm = course with { Id = MetadataTaskNavigation.SharedCourseId(course.ApiCourseId, "502"), RecruitId = "502" };
        var sameExam = MetadataPlatformParser.ZhihuishuSchoolExam(Fixtures.SchoolExam(Fixtures.RoomExam(recruit: "502")), Fixtures.Account, otherTerm, "shared-room-exam", 1).Tasks.Single();
        Check(TaskStore.MetadataTaskKey(assigned) != TaskStore.MetadataTaskKey(sameExam)
            && TaskStore.MetadataTaskKey(assigned) != TaskStore.MetadataTaskKey(assigned with { AccountId = "other-account" }), "school exam keys isolate term and account");
    }

    private static async Task SchoolExamSync()
    {
        var root = Path.Combine(Path.GetTempPath(), "metadata-school-exams-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = FixtureTaskStore.Create(Path.Combine(root, "tasks.db")); var reader = new Fixtures.Reader { IncludeCourseLists = true };
        reader.Response = request => request.Operation switch
        {
            "shared-exam-type" => new("ok", 200, Fixtures.ExamType(request.Course!.RecruitId == "501" ? 2 : 3)),
            "shared-room-exam" => new("ok", 200, Fixtures.SchoolExam(Fixtures.RoomExam(id: request.Bucket == 1 ? "room-pending" : "room-completed",
                exam: request.Bucket == 1 ? "701" : "702", state: request.Bucket == 1 ? 1 : 4))),
            "shared-offline-exam" => new("ok", 200, Fixtures.SchoolExam(request.Bucket == 1 ? Fixtures.OfflineExam(recruit: "502") : null)),
            "shared-makeup" => new("ok", 200, Fixtures.Makeup(1)),
            _ => null
        };
        var validResponses = reader.Response!;
        var summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.Tasks == 14 && summary.RoomExamTasks == 2 && summary.OfflineExamTasks == 1 && summary.MakeupExamTasks == 2 && summary.UnidentifiedExamRows == 0,
            "room offline and online makeup integrate with existing homework and flip tasks");
        Check(summary.FullTaskListsCovered && summary.CoursesRead == 3 && summary.StatusNeedsReview == 1 && summary.TasksWithDeadline == 13 && summary.TasksWithLink == 9,
            "school lists traversal and unknown offline state are independently reported");
        Check(store.LoadTasks().Single(task => task.PlatformTitle == "合成线下考试") is { State: TaskState.NeedsReview, DeadlineKind: DeadlineKind.Unknown, Link: null },
            "offline arrangement enters unified list as review item without guessed date or entry");
        Check(reader.Requests.Where(request => request.Operation == "shared-makeup").All(request => request.Course!.RecruitId == "501")
            && reader.Requests.Where(request => request.Operation == "shared-exams").All(request => request.Course!.RecruitId == "501"), "offline course never requests room makeup branch");
        var room = store.LoadTasks().Single(task => task.PlatformTitle == "合成机房考试" && task.State == TaskState.Pending);
        var roomRevision = room.Revision;
        reader.Response = request => request.Operation == "shared-exams" ? request.Bucket == 1
            ? new("ok", 200, Fixtures.SharedExams(Fixtures.SharedTask("room-pending", "701", "标识冲突", 1))) : reader.DefaultResponse(request) : validResponses(request);
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.Problems.Any(problem => problem.Code == "ZHS003") && store.Get(room.Id)!.Revision == roomRevision
            && store.LoadTasks().All(task => task.PlatformTitle != "标识冲突"), "room makeup identity collision rejects staged exam changes");
        reader.Response = request => request.Operation == "shared-makeup" ? new("ok", 200, "{\"status\":200,\"rt\":null}") : validResponses(request);
        reader.Requests.Clear(); summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.RoomExamTasks == 2 && summary.MakeupExamTasks == 0 && reader.Requests.All(request => request.Operation != "shared-exams")
            && summary.Problems.Any(problem => problem.Message.Contains("不能视为没有补考", StringComparison.Ordinal)), "unknown makeup flag keeps verified rooms and reports partial coverage");
        Check(store.LoadTasks().Count == 14, "makeup flag failure cannot remove historical exams");
        reader.Response = request => request.Operation == "shared-offline-exam" && request.Bucket == 1
            ? new("ok", 200, Fixtures.SchoolExam(new { examName = "学校未提供标识", state = 1 })) : validResponses(request);
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.UnidentifiedExamRows == 1 && summary.Problems.Any(problem => problem.Code == "ZHS006")
            && store.LoadTasks().All(task => task.PlatformTitle != "学校未提供标识"), "unidentified school arrangement appears in coverage diagnostics without fake task");
        reader.Response = request => request.Operation == "shared-room-exam"
            ? new("ok", 200, Fixtures.SchoolExam(Fixtures.RoomExam(state: request.Bucket == 1 ? 1 : 4))) : validResponses(request);
        roomRevision = store.Get(room.Id)!.Revision;
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.RoomExamTasks == 0 && summary.MakeupExamTasks == 0 && store.Get(room.Id)!.Revision == roomRevision,
            "same room task in both submission buckets is rejected before import or makeup read");
        reader.Response = request => request.Operation == "shared-room-exam" && request.Bucket == 2 ? new("login_required", 403) : validResponses(request);
        reader.Requests.Clear(); summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.LoginRequired && reader.Requests.All(request => request.Operation != "shared-makeup") && store.Get(room.Id)!.Revision == roomRevision,
            "school exam login expiry stops reads and preserves staged task state");
        reader.Response = validResponses; reader.Requests.Clear(); var recording = store.LoadCourseRecording(Zhs.Id);
        store.SaveCourseRecording(Zhs.Id, false, [CourseRecordingRules.MetadataKey(Zhs.Id, Fixtures.Account, Fixtures.SharedCourseModel(recruit: "502").Id, "shared")],
            true, recording.Revision, Fixtures.Now);
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.SelectedCourses == 1 && summary.Tasks == 3 && summary.OfflineExamTasks == 1 && summary.RoomExamTasks == 0
            && reader.Requests.Where(request => request.Course is not null).All(request => request.Course!.RecruitId == "502"), "school exam reads respect selected term");
        store.RecordMetadataSyncResult(summary, "partial", Fixtures.Now); var loaded = FixtureTaskStore.Create(store.DatabasePath);
        Check(loaded.LoadMetadataSettings(Zhs.Id).LastSummary is { OfflineExamTasks: 1, RoomExamTasks: 0 }, "school exam counters persist in saved summary");
        recording = store.LoadCourseRecording(Zhs.Id); store.SaveCourseRecording(Zhs.Id, true, [], true, recording.Revision, Fixtures.Now);
        var changed = false;
        reader.Response = request =>
        {
            if (!changed && request.Operation == "shared-room-exam")
            { changed = true; var current = store.LoadCourseRecording(Zhs.Id); store.SaveCourseRecording(Zhs.Id, false, [], true, current.Revision, Fixtures.Now); }
            return validResponses(request);
        };
        var priorRoomRevision = store.Get(room.Id)!.Revision;
        try { await new MetadataSynchronizer(Zhs, store, reader).RunAsync(); throw new InvalidOperationException("Expected course recording cancellation."); }
        catch (CourseRecordingChangedException) { Check(store.Get(room.Id)!.Revision == priorRoomRevision, "course selection change cancels school exam import at read boundary"); }
        Check(!File.ReadAllText(store.DatabasePath).Contains("synthetic-answer-not-read", StringComparison.Ordinal)
            && !File.ReadAllText(store.DatabasePath).Contains("synthetic-feedback-not-read", StringComparison.Ordinal), "school exam database never stores answer or feedback body");
        Directory.Delete(root, true);
    }
}
