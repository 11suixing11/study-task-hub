using System.Text.Json;
using System.Net;
using AccessProbe;

namespace PlatformMetadataTestData;

public static class UcampusFixtures
{
    public const string Account = "synthetic-legacy-account";
    public const int HomeworkTaskCount = 4;
    public const int ExamTaskCount = 3;
    public const int TaskCount = HomeworkTaskCount + ExamTaskCount;
    public static MetadataSessionCookie Cookie(string account = Account) => new("ucamUserId", account, ".unipus.cn", "/", true, false, null, "Lax");
    public static string Profile(string account = Account) => JsonSerializer.Serialize(new { openId = account, token = "synthetic-unused-token" });
    public static MetadataCourse Course(string id = "1001", string classId = "2001")
    {
        var context = new UcampusCourseContext(classId, "3001", "", "0");
        return new(MetadataTaskNavigation.UcampusCourseId(id, context), "合成旧版班课", "legacy", "旧版班课 · 全部状态作业、测试列表") { ApiCourseId = id, Ucampus = context };
    }
    public static string Card(MetadataCourse? model = null, string? name = null)
    {
        var course = model ?? Course();
        return "<div class='class-content'><span class='class-name'>合成班级</span><div class='my_course_item ite' tutorialid='synthetic-content' seriestemplate='pc' hastch='1' data-type='pc'>"
            + "<span class='hideurl'>" + WebUtility.HtmlEncode(MetadataPlatformParser.UcampusCatalogAddress(course)) + "</span><div class='my_course_name' title='"
            + WebUtility.HtmlEncode(name ?? course.Name) + "'>" + WebUtility.HtmlEncode(name ?? course.Name) + "</div></div></div>";
    }
    public static string Catalog(string? cards = null) => "<!doctype html><title>合成旧版 U 校园学生页</title><div class='my_course_box'><div class='my_course_list'>"
        + (cards ?? Card()) + "</div></div><div class='my_course_item'><div class='my_course_name'>宣传课程</div></div><a href='/user/student/logout'>退出</a>";
    public static string EmptyCatalog() => Catalog("<p>暂无课程</p>");
    public static string CatalogPage(MetadataCourse? model = null)
    {
        var course = model ?? Course(); var context = course.Ucampus!;
        return "<!doctype html><title>合成课程目录</title><a href='/user/student/homework/index?school_id=" + context.SchoolHint
            + "&amp;courseId=" + course.ApiCourseId + "&amp;classId=" + context.ClassId + "&amp;eccId=" + context.EccId + "&amp;activationStatus=1'>作业</a>"
            + "<a href='/user/student/homework/index?school_id=" + context.SchoolHint + "&amp;courseId=" + course.ApiCourseId
            + "&amp;classId=" + context.ClassId + "&amp;eccId=" + context.EccId + "&amp;activationStatus=1&amp;type=2'>测试</a>"
            + "<script>var courseId='synthetic+content:pc';var dbCourseId='" + course.ApiCourseId + "';var classId='" + context.ClassId
            + "';var version='default';var automaticJoinClassUrl='/user/student/mycourse/automaticJoinClass';</script>";
    }
    public static string HomeworkShell(MetadataCourse? model = null)
    {
        var course = model ?? Course();
        return "<!doctype html><title>合成作业列表</title><script>function pageFirst(state){$.getJSON(\"/user/student/homework/list?length=10&school_id=6001&courseId="
            + course.ApiCourseId + "&type=1&newState=\"+state+\"&time=\"+Date.now(),function(data){});}</script>"
            + "<script type='text/html' id='table'>list[i].homework_id;list[i].class_id;list[i].state;list[i].newState</script>";
    }
    public static MetadataCourse BoundCourse()
    {
        var course = Course();
        var bound = MetadataPlatformParser.UcampusCourse(CatalogPage(course), course).Course!;
        bound = MetadataPlatformParser.UcampusHomeworkEntry(HomeworkShell(course), bound).Course!;
        return MetadataPlatformParser.UcampusExamEntry(ExamShell(course), bound).Course!;
    }
    public static string ExamShell(MetadataCourse? model = null)
    {
        var course = model ?? Course();
        return "<!doctype html><title>合成测试列表</title><script>function pageFirst(state){$.getJSON(\"/user/student/homework/list?length=10&school_id=6001&courseId="
            + course.ApiCourseId + "&eccId=" + course.Ucampus!.EccId + "&classId=" + course.Ucampus.ClassId
            + "&type=2&newState=\"+state,function(data){});}</script>";
    }
    public static object Row(string id = "4001", string title = "合成 U 校园待办", int state = 0, int display = 4, int type = 1,
        string classId = "2001", string student = "5001", int peer = 0, int enabled = 0, int showEvaluation = 0, string? start = null, string? end = null)
        => new { homework_id = id, class_id = classId, stu_id = student, title, state, newState = display, type,
            start_time = start ?? "2026-10-28 08:00:00", end_time = end ?? "2026-11-01 23:59:00", peer_evaluation_flag = peer,
            peer_evaluation_enable = enabled, show_evaluation_time = showEvaluation, peer_evaluation_start_time = "2026-10-31 12:00:00",
            id = "synthetic-record", home_class_id = "synthetic-group", group_id = "synthetic-group",
            content = "synthetic-question-not-read", answer = "synthetic-answer-not-read" };
    public static string List(object[]? rows = null, int page = 1, int? total = null) => JsonSerializer.Serialize(new
    {
        code = 0, msg = "合成成功", rs = new { totalRow = total ?? rows?.Length ?? 0, pageNumber = page,
            totalPage = ((total ?? rows?.Length ?? 0) + 9) / 10, pageSize = 10,
            firstPage = page == 1, lastPage = page == ((total ?? rows?.Length ?? 0) + 9) / 10, list = rows ?? Array.Empty<object>() }
    });
    public static string DefaultList() => List([Row(), Row("4002", "合成 U 校园已提交", 2, 2), Row("4003", "合成 U 校园重做", -1, 3),
        Row("4004", "合成 U 校园进行中", 0, 3)]);
    public static string DefaultExamList() => List([Row("4001", "合成 U 校园待测", 0, 4, 2),
        Row("4005", "合成 U 校园已提交测试", 2, 2, 2), Row("4006", "合成 U 校园测试重做", -1, 3, 2)]);
    public sealed class Reader : IPlatformMetadataReader
    {
        public string AccountId { get; set; } = Account;
        public Func<MetadataRequest, MetadataResponse?>? Response { get; set; }
        public List<MetadataRequest> Requests { get; } = [];
        public Task<MetadataResponse> ReadAsync(MetadataRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Requests.Add(request);
            return Task.FromResult(Response?.Invoke(request) ?? request.Operation switch
            {
                "profile" => new MetadataResponse("ok", 200, Profile(AccountId)) { AccountId = AccountId },
                "courses" => new("ok", 200, Catalog()),
                "ucampus-course" => new("ok", 200, CatalogPage(request.Course)),
                "ucampus-homework-entry" => new("ok", 200, HomeworkShell(request.Course)),
                "ucampus-homework" => new("ok", 200, DefaultList()),
                "ucampus-exam-entry" => new("ok", 200, ExamShell(request.Course)),
                "ucampus-exam" => new("ok", 200, DefaultExamList()),
                "ucampus-content-info" => new("ok", 200, EmptyContentInfo()),
                "ucampus-content-catalog" => new("ok", 200, EmptyContentCatalog()),
                "ucampus-content-progress" => new("ok", 200, EmptyContentProgress(AccountId)),
                _ => throw new InvalidOperationException("Unexpected legacy metadata request.")
            });
        }
    }
    public static string EmptyContentInfo() => JsonSerializer.Serialize(new { code = 0, publish_version = 100, course = JsonSerializer.Serialize(new { type = "0", units = Array.Empty<object>() }) });
    public static string EmptyContentCatalog() => JsonSerializer.Serialize(new { code = 0, rt = new { tutorialId = "synthetic+content:pc", publish_version = 100, toc = Array.Empty<object>() } });
    public static string EmptyContentProgress(string account = Account) => JsonSerializer.Serialize(new { code = 0, rt = new { tutorialId = "synthetic+content:pc", open_id = account, publish_version = "100", units = new { } } });

    public static object LearningWindow(DateTimeOffset? begin, DateTimeOffset? deadline, bool required = true)
        => new { start_time = begin?.ToUnixTimeSeconds(), end_time = deadline?.ToUnixTimeSeconds(), required };
    public static object LearningUnitNode(string id) => new { id, url = id, role = "unit", name = "Unit " + id[1..], caption = "合成单元",
        children = new object[] { new { url = id + "n1", role = "node", children = new object[] {
            new { url = id + "n2", role = "node", children = new object[] {
                new { id = id + "g1", url = id + "g1", role = "group", tab_type = "task", children = Array.Empty<object>() },
                new { id = id + "g2", url = id + "g2", role = "link", tab_type = "ut", children = Array.Empty<object>() },
                new { id = id + "g3", url = id + "g3", role = "group", tab_type = "task", children = Array.Empty<object>() }
            } } } } } };
    public static string LearningContentInfo(string[]? ids = null, string type = "0") => JsonSerializer.Serialize(new { code = 0, publish_version = 100,
        course = JsonSerializer.Serialize(new { type, units = (ids ?? ["u1", "u2", "u3"]).Select(LearningUnitNode).ToArray() }) });
    public static string LearningCatalog(string[]? ids = null) => JsonSerializer.Serialize(new { code = 0,
        rt = new { tutorialId = "synthetic+content:pc", publish_version = 100, toc = (ids ?? ["u1", "u2", "u3"]).Select(LearningUnitNode).ToArray() } });
    public static string LearningCourseProgress(DateTimeOffset now, string[]? ids = null, string account = Account) => JsonSerializer.Serialize(new { code = 0,
        rt = new { tutorialId = "synthetic+content:pc", open_id = account, publish_version = "100",
            units = (ids ?? ["u1", "u2", "u3"]).ToDictionary(id => id, id => new { state = new { pass = 0, pass2 = 0, perm = 1, required = true },
                strategies = LearningWindow(now.AddDays(-1), id == "u1" ? now.AddHours(6) : id == "u2" ? now.AddHours(1) : now.AddHours(-1), required: false) }) } });
    public static string LearningProgress(string id, DateTimeOffset now, string account = Account, int? firstPass = null, int? secondPass = null,
        bool required = true, DateTimeOffset? earlyDeadline = null, bool? unlimited = null)
    {
        var deadline = unlimited == true ? (DateTimeOffset?)null : id == "u1" ? now.AddHours(6) : id == "u2" ? now.AddHours(1) : now.AddHours(-1);
        var begin = unlimited == true ? (DateTimeOffset?)null : now.AddDays(-1);
        object Row(bool needed, int pass, string tab, DateTimeOffset? end) => new { state = new { pass, pass2 = 1, perm = 1 }, strategies = LearningWindow(begin, end, needed), tab_type = tab };
        return JsonSerializer.Serialize(new { code = 0, rt = new { tutorialId = "synthetic+content:pc", open_id = account, unit_id = id, publish_version = "100",
            micros = new Dictionary<string, object> { [id + "/" + id + "n1"] = new { state = new { pass = 0, pass2 = 0, perm = 1 }, strategies = LearningWindow(begin, deadline, required) } },
            leafs = new Dictionary<string, object> { [id + "g1"] = Row(required, firstPass ?? (id == "u1" || id == "u3" ? 1 : 0), "task", earlyDeadline ?? deadline),
                [id + "g2"] = Row(required, secondPass ?? (id == "u3" ? 1 : 2), "ut", deadline), [id + "g3"] = Row(false, 0, "task", deadline) } } });
    }
}
