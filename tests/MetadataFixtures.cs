using System.Text.Json;
using System.Globalization;
using AccessProbe;

namespace PlatformMetadataTestData;

public static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(8));
    public const string Account = "synthetic-account-01";
    public static readonly byte[] Key = System.Text.Encoding.UTF8.GetBytes("0123456789abcdef");
    public static MetadataSessionCookie Cookie(string account = Account) => new("CASLOGC", Uri.EscapeDataString(JsonSerializer.Serialize(new { uuid = account })),
        ".zhihuishu.com", "/", true, false, null, "Lax");
    public static string Profile(string account = Account) => JsonSerializer.Serialize(new { code = 200, result = new { uuid = account, realName = "合成验收用户" } });
    public static object Course(string id = "101", string name = "合成翻转课") => new { courseId = id, courseName = name, teacherName = "合成教师" };
    public static string Catalog(int page = 1, int totalPages = 1, object[]? courses = null) => JsonSerializer.Serialize(new
    { status = 200, rt = new { pageList = courses ?? [Course()], pageNum = page, totalPages } });
    public static object Reminder(string id = "201", string course = "101", int type = 1, int paper = 0, int status = 4,
        long? start = null, long? end = null, int? examType = 0, string title = "合成作业") => new
    {
        id, courseId = course, type, paperType = paper, status, startTime = start ?? Now.AddHours(-2).ToUnixTimeMilliseconds(),
        endTime = end ?? Now.AddHours(8).ToUnixTimeMilliseconds(), examType, title,
        courseName = "接口原始课程标题", privateStudentId = "synthetic-must-not-persist", answer = "synthetic-answer-not-read"
    };
    public static string Reminders(params object[] rows) => JsonSerializer.Serialize(new { status = "200", rt = rows });
    public static string DefaultReminders() => Reminders(Reminder(), Reminder("202", type: 2, status: 2, title: "合成考试"),
        Reminder("203", paper: 1, status: 2, title: "状态待核对作业"));
    public static string Shared(params object[] rows) => JsonSerializer.Serialize(new { status = "200", rt = new { data = rows } });
    public const int FullTaskCount = 13;
    public const int SchoolExamTaskCount = 14;
    public const string SchoolId = "6001";
    public static string Ucampus() => "<!doctype html><meta charset='utf-8'><title>合成旧版 U 校园学生页</title><p>课程结构尚未核对</p>";
    public static object SharedCourse(string course = "101", string recruit = "501", string name = "合成共享课")
        => new { courseId = course, recruitId = recruit, courseName = name };
    public static MetadataCourse SharedCourseModel(string course = "101", string recruit = "501")
        => new(MetadataTaskNavigation.SharedCourseId(course, recruit), "合成共享课", "shared", "合成共享课开课期") { ApiCourseId = course, RecruitId = recruit };
    public static string SharedCatalog(object[]? rows = null, int? total = null)
        => JsonSerializer.Serialize(new { code = 200, result = new { courseOpenDtos = rows ?? Array.Empty<object>(), totalCount = total ?? rows?.Length ?? 0 } });
    public static string SharedProfile(string account = Account, bool loggedIn = true)
        => JsonSerializer.Serialize(new { status = 200, rt = new { loginstatus = loggedIn, uuid = account } });
    public static string SharedSchool(string school = SchoolId) => JsonSerializer.Serialize(new { status = 200, rt = new { schoolId = school } });
    public static string ExamType(int type = 1) => JsonSerializer.Serialize(new { status = 200, rt = type });
    public static object FlipTask(string id = "201", string title = "合成作业", int status = 3, long paper = 0,
        string course = "101", int dateStatus = 2, int submitOption = 1, long? end = null)
        => new { homeworkId = id, title, status, paperId = paper, courseId = course,
            startTime = Now.AddHours(-2).ToUnixTimeMilliseconds(), endTime = end ?? Now.AddHours(8).ToUnixTimeMilliseconds(),
            examDateStatus = dateStatus, submitOption, delay = 0, isHomeworkDelay = false,
            content = "synthetic-question-not-read", answer = "synthetic-answer-not-read" };
    public static string FlipList(object[]? rows = null, int? total = null)
        => JsonSerializer.Serialize(new { status = 200, rt = new { list = rows ?? Array.Empty<object>(), rowCount = total ?? rows?.Length ?? 0 } });
    public static object SharedTask(string id = "a+b/c==", string exam = "301", string title = "共享待提交作业", int state = 1,
        string course = "101", string recruit = "501", int meet = 0, int review = 0, int returned = 0,
        string? start = null, string? end = null)
        => new { id, examId = exam, examName = title, state, courseId = course, recruitId = recruit, meetCourseType = meet,
            reviewType = review, backStatus = returned,
            startTime = start ?? Now.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            endDate = end ?? Now.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            workCorrectEndTime = "", content = "synthetic-question-not-read", answer = "synthetic-answer-not-read" };
    public static string SharedHomework(object[]? rows = null, int next = -1)
        => JsonSerializer.Serialize(new { status = 200, rt = new { studentHomeworkList = rows ?? Array.Empty<object>(), next } });
    public static string SharedExams(params object[] rows) => JsonSerializer.Serialize(new { status = 200, rt = rows });
    public static string SchoolExam(object? row = null) => JsonSerializer.Serialize(new { status = 200, rt = row });
    public static object RoomExam(string id = "room-student==", string exam = "701", string title = "合成机房考试", int state = 1,
        string course = "101", string recruit = "501", int arrange = 1, string address = "合成教学楼 301 机房",
        string? start = null, string? end = null)
        => new { id, examId = exam, examName = title, state, courseId = course, recruitId = recruit, arrangeExam = arrange, roomAddress = address,
            startTime = start ?? Now.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            endDate = end ?? Now.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            answer = "synthetic-answer-not-read", examBaseComment = new { content = "synthetic-feedback-not-read" } };
    public static object OfflineExam(string id = "offline-student==", string exam = "801", string title = "合成线下考试", int state = 1,
        string course = "101", string recruit = "501")
        => new { id, examId = exam, examName = title, state, courseId = course, recruitId = recruit,
            startTime = "2026-10-28 01:00:00", endDate = "2026-10-29 23:00:00", answer = "synthetic-answer-not-read" };
    public static string Makeup(int value = 0) => JsonSerializer.Serialize(new { status = 200, rt = value });
    public const string SmartCourseId = "9007199254740993123";
    public static object SmartCourse(string course = SmartCourseId, string classId = "901", bool newFlag = true,
        string name = "合成智慧课程", string end = "2026-12-31 23:59:59")
        => new { courseId = course, classId = long.Parse(classId, CultureInfo.InvariantCulture), courseName = name,
            className = "合成班级", newFlag, type = 1, sourceType = 0, endTime = end };
    public static MetadataCourse SmartCourseModel(string course = SmartCourseId, string classId = "901")
        => new(MetadataTaskNavigation.SmartCourseId(course, classId), "合成智慧课程", "smart", "合成智慧课程班级")
            { ApiCourseId = course, Smart = new(classId, "pro") };
    public static string SmartCatalog(params object[] rows) => JsonSerializer.Serialize(new { status = "200", rt = rows });
    public static string AgentCatalog(object[]? rows = null, int page = 1, int? total = null) => JsonSerializer.Serialize(new
        { code = 200, success = true, data = new { list = rows ?? Array.Empty<object>(), page, size = 5,
            total = (total ?? rows?.Length ?? 0).ToString(CultureInfo.InvariantCulture), totalPage = ((total ?? rows?.Length ?? 0) + 4) / 5, last = false } });
    public static object SmartType(int type = 1, int count = 5, int unfinished = 1) => new { taskType = type, taskName = "合成类别", count, unfinished, agentCategory = (int?)null, taskCustomType = (int?)null };
    public static string SmartTypes(params object[] rows) => JsonSerializer.Serialize(new { code = 200, data = rows });
    public static object SmartTask(string id = "smart-pending", int type = 1, int examStatus = 7, int status = 2,
        string course = SmartCourseId, string classId = "901", int submitMode = 1, bool peer = false,
        string end = "2026-10-27 23:59:59", string title = "合成智慧作业") => new
        { id, taskType = type, examStatus, status, courseId = course, classId = long.Parse(classId, CultureInfo.InvariantCulture), submitMode,
            isPeerReview = peer, withoutQFlag = true, taskName = title, startTime = "2026-10-21 08:00:00", endTime = end,
            latestSubmitTime = "2026-10-29 23:59:59", peerReviewEndTime = "2026-10-30 23:59:59",
            taskDesc = "synthetic-question-not-read", answer = "synthetic-answer-not-read" };
    public static string SmartList(params object[] rows) => JsonSerializer.Serialize(new { code = 200, data = rows });
    public sealed class Reader : IPlatformMetadataReader
    {
        public Func<MetadataRequest, MetadataResponse?>? Response { get; set; }
        public List<MetadataRequest> Requests { get; } = [];
        public string AccountId { get; set; } = Account;
        public bool IncludeCourseLists { get; set; }
        public bool IncludeSchoolExams { get; set; }
        public Task<MetadataResponse> ReadAsync(MetadataRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Requests.Add(request);
            return Task.FromResult(Response?.Invoke(request) ?? DefaultResponse(request));
        }
        public MetadataResponse DefaultResponse(MetadataRequest request)
        {
            var course = request.Course; var recruit = course?.RecruitId ?? "501";
            return request.Operation switch
            {
                "profile" => new MetadataResponse("ok", 200, Profile(AccountId)) { AccountId = AccountId },
                "courses" => new("ok", 200, Catalog(request.Page)),
                "shared-courses" => new("ok", 200, SharedCatalog(IncludeCourseLists ? [SharedCourse(recruit: request.Bucket == 0 ? "501" : "502")] : [])),
                "smart-courses" => new("ok", 200, "{\"status\":200,\"rt\":[]}"),
                "agent-courses" => new("ok", 200, JsonSerializer.Serialize(new { code = 200, success = true,
                    data = new { list = Array.Empty<object>(), page = request.Page, size = 5, total = "0", totalPage = 0, last = false } })),
                "reminders" => new("ok", 200, DefaultReminders()),
                "shared-reminders" => new("ok", 200, Shared()),
                "flip-homework" => new("ok", 200, FlipList(IncludeCourseLists ? [FlipTask(), FlipTask("203", "退回作业", 2, 12), FlipTask("204", "待提交作业", 1, 12)] : [])),
                "flip-exam" => new("ok", 200, FlipList(IncludeCourseLists ? [FlipTask("202", "合成考试", 1), FlipTask("205", "已提交考试", 4, 31)] : [])),
                "shared-profile" or "shared-exam-profile" => new("ok", 200, SharedProfile(AccountId)),
                "shared-school" => new("ok", 200, SharedSchool()),
                "shared-exam-type" => new("ok", 200, ExamType(IncludeSchoolExams ? recruit == "501" ? 2 : 3 : 1)),
                "shared-homework" => new("ok", 200, SharedHomework(IncludeCourseLists ? [request.Bucket == 1
                    ? SharedTask(recruit: recruit) : SharedTask("submitted==", "302", "共享已提交作业", 3, recruit: recruit, meet: 1)] : [])),
                "shared-exams" => new("ok", 200, SharedExams(IncludeCourseLists ? [request.Bucket == 1
                    ? SharedTask("exam-pending", "401", "共享待提交考试", 1, recruit: recruit)
                    : SharedTask("exam-submitted", "402", "共享已提交考试", 4, recruit: recruit)] : [])),
                "shared-room-exam" => new("ok", 200, SchoolExam(IncludeSchoolExams && IncludeCourseLists
                    ? RoomExam(id: request.Bucket == 1 ? "room-pending" : "room-completed", exam: request.Bucket == 1 ? "701" : "702",
                        state: request.Bucket == 1 ? 1 : 4, recruit: recruit) : null)),
                "shared-offline-exam" => new("ok", 200, SchoolExam(IncludeSchoolExams && IncludeCourseLists && request.Bucket == 1 ? OfflineExam(recruit: recruit) : null)),
                "shared-makeup" => new("ok", 200, Makeup(IncludeSchoolExams && recruit == "501" ? 1 : 0)),
                _ => new("unsupported_request", null)
            };
        }
    }
}
