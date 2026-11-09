using System.Globalization;
using System.Text.Json;

namespace AccessProbe;

public static partial class MetadataPlatformParser
{
    public const int SharedCoursePageSize = 5;
    public const int FlipTaskPageSize = 20;
    public static MetadataCatalog ZhihuishuSharedCatalog(string json, int page, int bucket)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "shared-courses", json);
        if (outcome != "ok") return new(outcome, [], page, 0, 0, 0, 0);
        using var document = Document(json); var result = Property(document!.RootElement, "result");
        var list = Property(result, "courseOpenDtos"); var total = Number(result, "totalCount");
        // null is an explicit empty catalog only in the first, zero-total official response.
        if (page == 1 && bucket is 0 or 1 && total == 0 && list.ValueKind == JsonValueKind.Null)
            return new("ok", [], page, 0, 0, 0, 0) { TotalRows = 0 };
        if (page < 1 || bucket is not (0 or 1) || total is null or < 0 or > 50000 || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() > SharedCoursePageSize || total == 0 && list.GetArrayLength() > 0)
            return new("unrecognized_catalog", [], page, 0, 0, 0, 0);
        var totalPages = (total.Value + SharedCoursePageSize - 1) / SharedCoursePageSize;
        if (totalPages > 0 && page > totalPages) return new("unrecognized_catalog", [], page, totalPages, 0, 0, 0);
        if (list.GetArrayLength() != Math.Min(SharedCoursePageSize, Math.Max(0, total.Value - (page - 1) * SharedCoursePageSize)))
            return new("unrecognized_catalog", [], page, totalPages, list.GetArrayLength(), 0, 0);
        var courses = new Dictionary<string, MetadataCourse>(StringComparer.Ordinal); var invalid = 0; var duplicates = 0;
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in list.EnumerateArray())
        {
            var courseId = Text(row, "courseId"); var recruit = Text(row, "recruitId"); var name = Text(row, "courseName").Trim();
            if (!MetadataTaskNavigation.IsId(courseId) || !MetadataTaskNavigation.IsId(recruit) || !ValidText(name, 200)) { invalid++; continue; }
            var id = MetadataTaskNavigation.SharedCourseId(courseId, recruit);
            var course = new MetadataCourse(id, name, "shared", "共享课 · " + (bucket == 0 ? "在学" : "已完成") + " · 开课期 " + recruit)
                { ApiCourseId = courseId, RecruitId = recruit };
            if (conflicts.Contains(id)) { invalid++; continue; }
            if (courses.TryGetValue(id, out var previous))
            { duplicates++; if (previous != course) { courses.Remove(id); conflicts.Add(id); invalid++; } }
            else courses[id] = course;
        }
        return new(invalid > 0 ? "partial_catalog" : "ok", courses.Values.ToArray(), page, totalPages, list.GetArrayLength(), invalid, duplicates) { TotalRows = total };
    }
    public static string? ZhihuishuSharedSchool(string json, string operation, string? account = null)
    {
        if (EnvelopeOutcome(MetadataPlatform.Zhihuishu, operation, json) != "ok") return null;
        using var document = Document(json); var rt = Property(document!.RootElement, "rt");
        var uuid = Text(rt, "uuid");
        if (account is not null && uuid != account) return null;
        var school = Text(rt, "schoolId"); return MetadataTaskNavigation.IsId(school) ? school : null;
    }
    public static string ZhihuishuSharedProfile(string json, string account)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "shared-profile", json); if (outcome != "ok") return outcome;
        using var document = Document(json); var rt = Property(document!.RootElement, "rt");
        if (rt.ValueKind != JsonValueKind.Object || Property(rt, "loginstatus").ValueKind != JsonValueKind.True) return "unrecognized_user";
        return Text(rt, "uuid") == account ? "ok" : "account_mismatch";
    }
    public static int? ZhihuishuSharedExamType(string json)
    {
        if (EnvelopeOutcome(MetadataPlatform.Zhihuishu, "shared-exam-type", json) != "ok") return null;
        using var document = Document(json); var rt = Property(document!.RootElement, "rt");
        return rt.ValueKind == JsonValueKind.Number && rt.TryGetInt32(out var number) && number is >= 1 and <= 3 ? number : null;
    }
    public static bool? ZhihuishuHasMakeupExam(string json)
    {
        if (EnvelopeOutcome(MetadataPlatform.Zhihuishu, "shared-makeup", json) != "ok") return null;
        using var document = Document(json); var rt = Property(document!.RootElement, "rt");
        return rt.ValueKind == JsonValueKind.Number && rt.TryGetInt32(out var number) && number is 0 or 1 ? number == 1 : null;
    }
    public static MetadataTaskBatch ZhihuishuFlipTasks(string json, string account, MetadataCourse course, string kind, int page)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "flip-" + kind, json);
        if (outcome != "ok") return new(outcome, [], 0, 0, []);
        using var document = Document(json); var rt = Property(document!.RootElement, "rt"); var list = Property(rt, "list"); var total = Number(rt, "rowCount");
        if (course.Kind != "flip" || !MetadataTaskNavigation.IsId(account) || !MetadataTaskNavigation.IsId(course.Id) || course.Id != course.ApiCourseId
            || kind is not ("homework" or "exam") || page < 1 || total is null or < 0 or > 200000
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > FlipTaskPageSize || total == 0 && list.GetArrayLength() > 0)
            return new("unrecognized_tasks", [], 0, 0, []);
        var totalPages = (total.Value + FlipTaskPageSize - 1) / FlipTaskPageSize;
        if (totalPages > 0 && page > totalPages) return new("unrecognized_tasks", [], 0, 0, []);
        var issues = new HashSet<string>(StringComparer.Ordinal); var tasks = new List<PlatformCollectedTask>(); var invalid = 0;
        foreach (var row in list.EnumerateArray())
        {
            var id = Text(row, "homeworkId"); var title = Text(row, "title").Trim(); var rowCourse = Text(row, "courseId");
            if (!MetadataTaskNavigation.IsId(id) || !ValidText(title, 300) || rowCourse.Length > 0 && rowCourse != course.ApiCourseId)
            { invalid++; continue; }
            var status = Number(row, "status"); var paper = Long(row, "paperId"); var dateStatus = Number(row, "examDateStatus");
            TaskState? state = status switch { 1 => TaskState.Pending, 2 => TaskState.Returned, 3 or 4 or 5 => TaskState.Completed, _ => null };
            var text = status switch { 1 => "未提交", 2 => "被打回", 3 => "已提交，待批阅或批阅中", 4 or 5 => "已批阅", _ => "状态待核对" };
            var begin = kind == "exam" ? Time(Property(row, "startTime")) : null; var deadline = Time(Property(row, "endTime"));
            CheckTimes(ref begin, ref deadline, issues);
            var link = status is >= 1 and <= 5 && paper is >= 0
                ? MetadataTaskNavigation.FlipList(course, id, kind, status.Value, paper.Value, dateStatus) : null;
            if (kind == "exam" && Number(row, "submitOption") != 1) { link = null; issues.Add("offline_exam_entry_unverified"); }
            if (state is null) issues.Add("state_unverified"); if (link is null) issues.Add("entry_unavailable");
            var time = (kind == "exam" ? "startTime=" + TimeEvidence(row, "startTime") + "; " : "") + "endTime=" + TimeEvidence(row, "endTime")
                + "; delay=" + TimeEvidence(row, "delay") + (kind == "homework" ? "; isHomeworkDelay=" + TimeEvidence(row, "isHomeworkDelay") : "");
            var explanation = "来自官网课程内" + (kind == "homework" ? "作业" : "考试") + "全部列表；status=3、4、5 表示已提交或已批阅。截止时间不作为已提交依据。";
            tasks.Add(new("zhihuishu", account, course.Id, "flip", id, kind, title, course.Name, state,
                text + "（status=" + (status?.ToString(CultureInfo.InvariantCulture) ?? "未提供") + "）", state is null ? "unknown" : "explicit",
                explanation, begin, deadline, time, link) { MetadataVersion = 2 });
        }
        var batch = UniqueTasks(tasks, invalid, issues);
        return batch with { TotalRows = total.Value, TotalPages = totalPages, Rows = list.GetArrayLength(), ListComplete = page == totalPages || totalPages == 0 };
    }
    public static MetadataTaskBatch ZhihuishuSharedTasks(string json, string account, MetadataCourse course, string operation, int bucket, string school)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, operation, json);
        if (outcome != "ok") return new(outcome, [], 0, 0, []);
        using var document = Document(json); var rt = Property(document!.RootElement, "rt"); var homework = operation == "shared-homework";
        var list = homework ? Property(rt, "studentHomeworkList") : rt;
        if (operation is not ("shared-homework" or "shared-exams") || course.Kind != "shared"
            || !MetadataTaskNavigation.IsId(course.ApiCourseId) || !MetadataTaskNavigation.IsId(course.RecruitId)
            || course.Id != MetadataTaskNavigation.SharedCourseId(course.ApiCourseId, course.RecruitId)
            || bucket is not (1 or 2) || !MetadataTaskNavigation.IsId(account)) return new("unrecognized_tasks", [], 0, 0, []);
        // The official final exam client explicitly accepts an empty object as no exams.
        if (!homework && list.ValueKind == JsonValueKind.Object && !list.EnumerateObject().Any())
            return new("ok", [], 0, 0, []) { ListComplete = true };
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > (homework ? 100 : 10000)) return new("unrecognized_tasks", [], 0, 0, []);
        var next = homework ? SignedNumber(rt, "next") : -1;
        if (homework && (next is null or < -1)) return new("unrecognized_tasks", [], 0, 0, []);
        var issues = new HashSet<string>(StringComparer.Ordinal); var tasks = new List<PlatformCollectedTask>(); var invalid = 0;
        if (next != -1) issues.Add("shared_homework_more_pages");
        foreach (var row in list.EnumerateArray())
        {
            var studentTask = Text(row, "id"); var exam = Text(row, "examId"); var title = Text(row, "examName").Trim();
            var rowCourse = Text(row, "courseId"); var recruit = Text(row, "recruitId");
            if (!ValidText(studentTask, 512) || !studentTask.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=' or '_' or '-')
                || !MetadataTaskNavigation.IsId(exam) || !ValidText(title, 300) || rowCourse.Length > 0 && rowCourse != course.ApiCourseId
                || recruit.Length > 0 && recruit != course.RecruitId) { invalid++; continue; }
            var status = Number(row, "state"); var returned = homework && Number(row, "backStatus") == 1;
            TaskState? state = returned && bucket == 1 ? TaskState.Returned : status switch
            { 1 or 2 when bucket == 1 => TaskState.Pending, 5 when !homework && bucket == 1 => TaskState.Returned,
                3 or 4 when bucket == 2 => TaskState.Completed, _ => null };
            var statusText = state == TaskState.Returned ? "退回重做" : state == TaskState.Pending ? "未提交或进行中"
                : state == TaskState.Completed ? status == 3 ? "已提交，待批阅" : "已提交，已批阅" : "状态与提交列表待核对";
            var begin = ChinaTextTime(Property(row, "startTime")); var deadline = ChinaTextTime(Property(row, "endDate"));
            CheckTimes(ref begin, ref deadline, issues);
            var meet = Number(row, "meetCourseType"); var review = Number(row, "reviewType");
            var link = homework && meet is 0 or 1 && state is not null
                ? MetadataTaskNavigation.Shared(course, studentTask, exam, school, meet.Value, bucket == 2, status) : null;
            // The official final-exam entry obtains a different encrypted student id after identity,
            // location and captcha checks. A list id must never be substituted into that route.
            if (!homework && bucket == 1) issues.Add("exam_verification_entry_unavailable");
            if (review == 1) { link = null; state = null; statusText = "互评任务待核对"; issues.Add("peer_review_unsupported"); }
            if (state is null) issues.Add("state_unverified"); if (link is null) issues.Add("entry_unavailable");
            var explanation = "来自官网" + (bucket == 1 ? "未提交" : "已提交") + "列表；保留原始 state、backStatus 和时间。";
            if (!homework && bucket == 1) explanation += "需从官网课程考试列表完成身份、地点或验证码核验后进入，当前不生成直接答题入口。";
            if (!homework && bucket == 2) explanation += "官网禁止直接查看已提交考试，未生成答题入口。";
            tasks.Add(new("zhihuishu", account, course.Id, "shared", MetadataTaskNavigation.SharedTaskId(studentTask, exam), homework ? "homework" : "exam",
                title, course.Name, state, statusText + "（state=" + (status?.ToString(CultureInfo.InvariantCulture) ?? "未提供")
                    + "; flag=" + bucket + "; backStatus=" + Text(row, "backStatus") + "）", state is null ? "unknown" : "explicit",
                explanation, begin, deadline, "startTime=" + TimeEvidence(row, "startTime") + "; endDate=" + TimeEvidence(row, "endDate")
                    + "; workCorrectEndTime=" + TimeEvidence(row, "workCorrectEndTime"), link) { MetadataVersion = 2, ExamMode = homework ? "" : "online" });
        }
        return UniqueTasks(tasks, invalid, issues) with { Rows = list.GetArrayLength(), ListComplete = next == -1 };
    }
    public static MetadataTaskBatch ZhihuishuSchoolExam(string json, string account, MetadataCourse course, string operation, int bucket)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, operation, json);
        if (outcome != "ok") return new(outcome, [], 0, 0, []);
        if (operation is not ("shared-room-exam" or "shared-offline-exam") || course.Kind != "shared"
            || !MetadataTaskNavigation.IsId(course.ApiCourseId) || !MetadataTaskNavigation.IsId(course.RecruitId)
            || course.Id != MetadataTaskNavigation.SharedCourseId(course.ApiCourseId, course.RecruitId)
            || bucket is not (1 or 2) || !MetadataTaskNavigation.IsId(account)) return new("unrecognized_tasks", [], 0, 0, []);
        using var document = Document(json); var row = Property(document!.RootElement, "rt");
        // These two official endpoints return a single object, null, or an empty object.
        // Missing rt, arrays, and unrelated objects must never become an empty-list success.
        if (row.ValueKind == JsonValueKind.Null || row.ValueKind == JsonValueKind.Object && !row.EnumerateObject().Any())
            return new("ok", [], 0, 0, []) { ListComplete = true };
        if (row.ValueKind != JsonValueKind.Object) return new("unrecognized_tasks", [], 0, 0, []);
        var title = Text(row, "examName").Trim(); var rowCourse = Text(row, "courseId"); var recruit = Text(row, "recruitId"); var uuid = Text(row, "uuid");
        if (!ValidText(title, 300) || rowCourse.Length > 0 && rowCourse != course.ApiCourseId || recruit.Length > 0 && recruit != course.RecruitId
            || uuid.Length > 0 && uuid != account) return new("partial_tasks", [], 1, 0, ["exam_attribution_unverified"]) { Rows = 1 };
        var studentTask = Text(row, "id"); var exam = Text(row, "examId");
        if (!ValidText(studentTask, 512) || !studentTask.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=' or '_' or '-')
            || !MetadataTaskNavigation.IsId(exam))
            return new("partial_tasks", [], 1, 0, ["exam_identity_unverified"]) { Rows = 1, UnidentifiedExamRows = 1 };
        var room = operation == "shared-room-exam"; var status = Number(row, "state"); var arrange = Number(row, "arrangeExam");
        TaskState? state = room ? status switch
        { 1 or 2 when bucket == 1 => TaskState.Pending, 5 when bucket == 1 => TaskState.Returned, 3 or 4 when bucket == 2 => TaskState.Completed, _ => null } : null;
        var label = room ? "机房考试" : "线下考试";
        var statusText = state == TaskState.Pending ? "未提交或进行中" : state == TaskState.Returned ? "退回重做"
            : state == TaskState.Completed ? status == 3 ? "已提交，待批阅" : "已提交，已批阅" : "状态与学校安排待核对";
        var issues = new HashSet<string>(StringComparer.Ordinal) { "entry_unavailable" };
        DateTimeOffset? begin = room ? ChinaTextTime(Property(row, "startTime")) : null;
        DateTimeOffset? deadline = room ? ChinaTextTime(Property(row, "endDate")) : null;
        CheckTimes(ref begin, ref deadline, issues);
        if (state is null) issues.Add("state_unverified");
        var explanation = "来自官网" + label + (bucket == 1 ? "未提交" : "已提交") + "列表；不生成直接答题入口。";
        var timeText = room ? "startTime=" + TimeEvidence(row, "startTime") + "; endDate=" + TimeEvidence(row, "endDate")
            : "以学校安排为准；官网此列表未确认具体日期。";
        if (room)
        {
            explanation += "按 state 与提交列表确认状态，日期不作为已完成依据。";
            timeText += "; arrangeExam=" + Text(row, "arrangeExam");
            var address = Text(row, "roomAddress").Trim();
            if (arrange == 1 && ValidText(address, 300)) { timeText += "; 考试地点=" + address; explanation += "具体场次和地点仍请核对学校安排。"; }
            else
            {
                timeText += "; 考试地点以学校安排为准。";
                explanation += "具体场次和地点以学校安排为准。";
                if (arrange != 0) issues.Add("exam_arrangement_unverified");
            }
        }
        else explanation += "官网只明确提示以学校安排为准，未据此推定已提交或截止时间；请手动核对。";
        var task = new PlatformCollectedTask("zhihuishu", account, course.Id, "shared", MetadataTaskNavigation.SharedTaskId(studentTask, exam), "exam",
            title, course.Name, state, label + " · " + statusText + "（state=" + (status?.ToString(CultureInfo.InvariantCulture) ?? "未提供") + "; flag=" + bucket + "）",
            state is null ? "unknown" : "explicit", explanation, begin, deadline, timeText, null) { MetadataVersion = 2, ExamMode = room ? "room" : "offline" };
        return UniqueTasks([task], 0, issues) with { Rows = 1, ListComplete = true };
    }
    private static MetadataTaskBatch UniqueTasks(IEnumerable<PlatformCollectedTask> rows, int invalid, HashSet<string> issues)
    {
        var tasks = new Dictionary<string, PlatformCollectedTask>(StringComparer.Ordinal); var conflicts = new HashSet<string>(StringComparer.Ordinal); var duplicates = 0;
        foreach (var task in rows)
        {
            var key = task.CourseKind + "\n" + task.CourseId + "\n" + task.Kind + "\n" + task.RemoteId;
            if (conflicts.Contains(key)) { invalid++; continue; }
            if (tasks.TryGetValue(key, out var previous))
            { duplicates++; if (previous != task) { tasks.Remove(key); conflicts.Add(key); invalid++; issues.Add("conflicting_task"); } }
            else tasks[key] = task;
        }
        return new(invalid > 0 || issues.Count > 0 ? "partial_tasks" : "ok", tasks.Values.ToArray(), invalid, duplicates, issues.ToArray());
    }
    private static void CheckTimes(ref DateTimeOffset? begin, ref DateTimeOffset? deadline, HashSet<string> issues)
    {
        if (begin is not null && deadline is not null && begin > deadline) { begin = deadline = null; issues.Add("invalid_time_order"); }
        if (deadline is null) issues.Add("deadline_unverified");
    }
    private static int? SignedNumber(JsonElement row, string field) => int.TryParse(Text(row, field), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static long? Long(JsonElement row, string field) => long.TryParse(Text(row, field), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static DateTimeOffset? ChinaTextTime(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String || !DateTime.TryParseExact(element.GetString(),
            new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            || value.Year is < 2000 or > 2100) return null;
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUniversalTime();
    }
}
