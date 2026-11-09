using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessProbe;

namespace PlatformMetadataTestData;

internal static class PtaFixtures
{
    public const string Account = "10001";
    public const string OtherAccount = "10002";
    public static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.FromHours(8));
    public static readonly MetadataCourse Course = new("20001", "合成程序设计课程", "class", "教学课程 · 题目集");
    public static readonly MetadataCourse OtherCourse = new("20002", "合成未选课程", "class", "教学课程 · 题目集");
    public static readonly MetadataCourse HiddenCourse = new("20003", "合成隐藏教学班级", "class", "教学课程 · 题目集");
    public static string Profile(string account = Account) => JsonSerializer.Serialize(new { user = new { id = account, roles = new[] { "USER" } }, emailLogin = true });
    public const string Guest = "{\"emailLogin\":false,\"omsLogin\":false,\"studentUserLogin\":false,\"phoneLogin\":false}";
    public static object Group(MetadataCourse course) => new { id = course.Id, name = course.Name, type = "STUDENT_USER" };
    public static string Catalog(IEnumerable<MetadataCourse>? courses = null, int? total = null) => JsonSerializer.Serialize(new
    { userGroups = (courses ?? [Course, OtherCourse]).Select(Group).ToArray(), total = total ?? (courses ?? [Course, OtherCourse]).Count() });
    public static JsonObject Set(string id, string title = "合成教学题目集", string timeType = "FIXED_TIME", string type = "EXERCISE") => new()
    {
        ["id"] = id, ["name"] = title, ["timeType"] = timeType, ["type"] = type, ["feature"] = "NONE",
        ["startAt"] = Now.AddDays(-1).ToString("O"), ["endAt"] = Now.AddDays(1).ToString("O")
    };
    public static readonly string[] CourseSetIds = ["30001", "30002", "30003", "30004", "30005"];
    public static JsonObject[] CourseSets() => [
        Set("30001", "合成尚未开始题目集"), Set("30002", "合成正在答题题目集", "FIXED_DURATION"),
        Set("30003", "合成已结束答题题目集"), Set("30004", "合成已超时答卷题目集"),
        Set("30005", "合成一直可用题目集", "ALWAYS_AVAILABLE")
    ];
    public static string Sets(IEnumerable<JsonObject> sets, int? total = null)
    {
        var list = sets.ToArray(); return JsonSerializer.Serialize(new { problemSets = list, total = total ?? list.Length });
    }
    public static string Teaching(bool hidden = false)
    {
        var sets = new List<JsonObject>(CourseSets()) { Set("40001", "合成未选课程题目集") };
        var groups = sets.ToDictionary(set => set["id"]!.GetValue<string>(), set => new { items = new[] { Group(set["id"]!.GetValue<string>() == "40001" ? OtherCourse : Course) } });
        if (hidden) { sets.Add(Set("50001", "合成隐藏班级题目集")); groups["50001"] = new { items = new[] { Group(HiddenCourse) } }; }
        return JsonSerializer.Serialize(new { problemSets = sets, total = sets.Count, userGroupsByProblemSetId = groups });
    }
    public static string Exam(JsonObject set, string account = Account, string status = "READY", string? examStatus = null,
        bool? ended = null, DateTimeOffset? candidateEnd = null)
    {
        var id = set["id"]!.GetValue<string>(); var root = new JsonObject { ["problemSet"] = set.DeepClone(), ["status"] = status };
        if (examStatus is not null)
        {
            var exam = new JsonObject { ["id"] = "6" + id, ["userId"] = account, ["problemSetId"] = id, ["status"] = examStatus,
                ["startAt"] = Now.AddHours(-1).ToString("O"), ["endAt"] = (candidateEnd ?? Now.AddHours(1)).ToString("O"), ["score"] = 100, ["acceptCount"] = 9 };
            if (ended is not null) exam["ended"] = ended.Value;
            root["exam"] = exam;
        }
        return root.ToJsonString();
    }
    public static string ExamFor(string id, string account = Account)
    {
        var set = CourseSets().FirstOrDefault(set => set["id"]!.GetValue<string>() == id) ?? Set(id);
        return id switch
        {
            "30002" => Exam(set, account, "PROCESSING", "PROCESSING", false, Now.AddMinutes(40)),
            "30003" => Exam(set, account, "END", "END", true, Now.AddMinutes(-10)),
            "30004" => Exam(set, account, "END", "END", false),
            _ => Exam(set, account)
        };
    }
    public static MetadataSessionCookie Cookie(string value = "synthetic-pta-session") => new("PTASession", value, ".pintia.cn", "/", true, true, null, "Strict");
    public sealed class Reader : IPlatformMetadataReader
    {
        public readonly List<MetadataRequest> Requests = [];
        public Func<MetadataRequest, MetadataResponse?>? Override { get; set; }
        public bool Hidden { get; set; }
        public string CurrentAccount = Account;
        public Task<MetadataResponse> ReadAsync(MetadataRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Requests.Add(request); var custom = Override?.Invoke(request);
            return Task.FromResult(custom ?? Response(request));
        }
        private MetadataResponse Response(MetadataRequest request) => request.Operation switch
        {
            "profile" => new("ok", 200, Profile(CurrentAccount)) { AccountId = CurrentAccount },
            "pta-courses" => new("ok", 200, Catalog()),
            "pta-teaching-sets" => new("ok", 200, Teaching(Hidden)),
            "pta-course-sets" when request.Course?.Id == Course.Id => new("ok", 200, Sets(CourseSets())),
            "pta-course-sets" when request.Course?.Id == HiddenCourse.Id => new("course_access_denied", 403),
            "pta-course-sets" => new("ok", 200, Sets([Set("40001", "合成未选课程题目集")])),
            "pta-exam" => new("ok", 200, ExamFor(request.ProblemSetId, CurrentAccount)),
            _ => new("unsupported_request", null)
        };
    }
    public sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        public readonly List<HttpRequestMessage> Requests = [];
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeReply { get; set; }
        public string CurrentAccount = Account;
        public bool RejectLogin { get; set; }
        public bool RenewNext { get; set; }
        public bool RenewServerSession { get; set; }
        public bool Hidden { get; set; }
        public string Session = "synthetic-pta-session";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Requests.Add(request); var uri = request.RequestUri!; Paths.Add(uri.AbsolutePath);
            if (request.Method != HttpMethod.Get || request.Content is not null || uri.Scheme != "https"
                || uri.IdnHost is not ("pintia.cn" or "passport.pintia.cn")) throw new InvalidOperationException("PTA fixture rejects non-metadata request.");
            if (BeforeReply is not null) await BeforeReply(request, cancellation);
            var custom = Override?.Invoke(request); if (custom is not null) return custom;
            var hasCookie = request.Headers.TryGetValues("Cookie", out var values) && values.Any(value => value.Contains("PTASession=" + Session, StringComparison.Ordinal));
            var body = ""; var code = HttpStatusCode.OK;
            if (uri.AbsolutePath == "/api/u/current")
            {
                if (!hasCookie || RejectLogin) body = Guest;
                else body = Profile(CurrentAccount);
            }
            else if (!hasCookie || RejectLogin) { body = "{\"error\":{\"code\":\"USER_NOT_LOGGED_IN\"}}"; code = HttpStatusCode.Unauthorized; }
            else if (uri.AbsolutePath == "/api/user-groups/member-visible") body = Catalog();
            else if (uri.AbsolutePath == "/api/problem-sets") body = Teaching(Hidden);
            else if (uri.AbsolutePath == "/api/problem-sets/user-groups/" + Course.Id) body = Sets(CourseSets());
            else if (uri.AbsolutePath == "/api/problem-sets/user-groups/" + OtherCourse.Id) body = Sets([Set("40001", "合成未选课程题目集")]);
            else if (uri.AbsolutePath == "/api/problem-sets/user-groups/" + HiddenCourse.Id) { code = HttpStatusCode.Forbidden; body = "{\"error\":{\"code\":\"PERMISSION_DENIED\"}}"; }
            else if (uri.AbsolutePath.StartsWith("/api/problem-sets/", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith("/exams", StringComparison.Ordinal)) body = ExamFor(uri.AbsolutePath.Split('/')[3], CurrentAccount);
            else throw new InvalidOperationException("PTA fixture rejects unknown metadata route.");
            var response = Reply(request, code, body);
            if (RenewNext && uri.AbsolutePath == "/api/u/current" && hasCookie && !RejectLogin)
            {
                RenewNext = false; Session = "synthetic-pta-session-renewed";
                response.Headers.TryAddWithoutValidation("Set-Cookie", "PTASession=" + Session + "; Domain=.pintia.cn; Path=/; Secure; HttpOnly; SameSite=Strict");
            }
            if (RenewServerSession && uri.IdnHost == "pintia.cn")
                response.Headers.TryAddWithoutValidation("Set-Cookie", "JSESSIONID=synthetic-server-" + Paths.Count + "; Path=/; HttpOnly");
            return response;
        }
        public static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode code, string body)
            => new(code) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
