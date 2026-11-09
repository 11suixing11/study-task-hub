using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccessProbe;

public static partial class MetadataTaskNavigation
{
    public static bool IsId(string? id) => id is { Length: > 0 and <= 128 }
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsStudentTaskId(string? id) => id is { Length: > 0 and <= 512 }
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '+' or '/' or '=');
    public static string SharedCourseId(string course, string recruit) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { course, recruit }))));
    public static string SharedTaskId(string studentTask, string exam) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { studentTask, exam }))));
    public static string? Shared(MetadataCourse course, string studentTask, string exam, string school, int meetType, bool submitted, int? state = null)
    {
        if (course.Kind != "shared" || !IsId(course.ApiCourseId) || !IsId(course.RecruitId)
            || course.Id != SharedCourseId(course.ApiCourseId, course.RecruitId) || !IsStudentTaskId(studentTask)
            || !IsId(exam) || !IsId(school) || meetType is not (0 or 1) || submitted && state is not (3 or 4)) return null;
        var route = submitted ? "checkHomework" : "dohomework";
        return "https://onlineexamh5new.zhihuishu.com/stuExamWeb.html#/webExamList/" + route + "/"
            + course.RecruitId + "/" + Uri.EscapeDataString(studentTask) + "/" + exam + "/" + course.ApiCourseId + "/" + school
            + (submitted ? "?meetCourseType=" + meetType + "&state=" + state : "/" + meetType);
    }
    public static string? FlipList(MetadataCourse course, string id, string kind, int status, long paperId, int? dateStatus)
    {
        if (course.Kind != "flip" || !IsId(course.Id) || !IsId(id) || kind is not ("homework" or "exam")
            || status is < 1 or > 5 || paperId < 0) return null;
        var root = "https://hiexam.zhihuishu.com/atHomeworkExam/stu/";
        if (kind == "homework")
        {
            return root + $"homeworkQ/homeworkDetail/{id}/homeworkDetail/{(paperId == 0 ? status : 6)}/{(paperId == 0 ? 1 : status == 1 ? 6 : status == 5 ? 8 : 7)}/{course.Id}";
        }
        if (status is 1 or 2)
            return dateStatus == 2 ? root + $"studentExamQ/examVerification/{course.Id}/{id}/2/{paperId}" : null;
        return root + (paperId == 0 ? $"examQ/examDetail/{id}/examDetailStu/{status}/1/{course.Id}/0"
            : $"examQ/doExamnew/{id}/doExamnew/6/{(status == 5 ? 8 : 7)}/{course.Id}");
    }
    public static string? Zhihuishu(string course, string id, int type, int paperType, int status, int? examType,
        DateTimeOffset now)
    {
        if (!IsId(course) || !IsId(id) || type is not (1 or 2) || paperType is not (0 or 1) || status == 1
            || type == 2 && examType is not (null or 0 or 1)) return null;
        var root = "https://hiexam.zhihuishu.com/atHomeworkExam/stu/";
        if (type == 1)
            return root + (paperType == 1 ? status == 4 ? $"homeworkQ/doHomeWorknew/{id}/doHomeWorknew/6/7/{course}"
                : $"homeworkQ/exerciseList/{id}/exerciseList/6/6/{course}"
                : $"homeworkQ/homeworkDetail/{id}/doHomeWork/{(status == 4 ? 2 : 1)}/1/{course}") + "?formFlag=1";
        if (status == 2) return root + $"studentExamQ/examVerification/{course}/{id}/2/{paperType}";
        if (status != 4) return null;
        var date = now.ToOffset(TimeSpan.FromHours(8)).ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            + " GMT+0800 (China Standard Time)";
        return root + (paperType == 0 ? $"examQ/examDetail/{id}/examDetailStu/2/1/{course}/0"
            : $"examQ/doExamnew/{id}/doExamnew/6/8/{course}") + "?date=" + Uri.EscapeDataString(date) + "&formFlag=1";
    }
    private static readonly Regex Route = new(@"^/atHomeworkExam/stu/(?:homeworkQ/(?:doHomeWorknew/([A-Za-z0-9_-]+)/doHomeWorknew/6/[678]/([A-Za-z0-9_-]+)|exerciseList/([A-Za-z0-9_-]+)/exerciseList/6/6/([A-Za-z0-9_-]+)|homeworkDetail/([A-Za-z0-9_-]+)/(?:doHomeWork/[12]/1|homeworkDetail/(?:[1-5]/1|6/[678]))/([A-Za-z0-9_-]+))|studentExamQ/examVerification/([A-Za-z0-9_-]+)/([A-Za-z0-9_-]+)/2/[0-9]+|examQ/(?:examDetail/([A-Za-z0-9_-]+)/examDetailStu/[2-5]/1/([A-Za-z0-9_-]+)/0|doExamnew/([A-Za-z0-9_-]+)/doExamnew/6/[78]/([A-Za-z0-9_-]+)))$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static (string Course, string Id, string Kind, string CourseKind)? Identity(string? address)
    {
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsDefaultPort) return null;
        if (uri.IdnHost == "ai-smart-course-student-pro.zhihuishu.com") return SmartIdentity(uri);
        if (uri.IdnHost == "onlineexamh5new.zhihuishu.com") return SharedIdentity(uri);
        if (uri.IdnHost != "hiexam.zhihuishu.com" || uri.Fragment.Length > 0) return null;
        var match = Route.Match(uri.AbsolutePath); if (!match.Success) return null;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2); if (parts.Length != 2 || !query.TryAdd(parts[0], Uri.UnescapeDataString(parts[1]))) return null;
        }
        if (query.Keys.Any(key => key is not ("date" or "formFlag")) || query.TryGetValue("formFlag", out var flag) && flag != "1"
            || query.TryGetValue("date", out var date) && (date.Length > 160 || date.Any(char.IsControl))) return null;
        for (var i = 1; i <= 12; i += 2)
        {
            if (!match.Groups[i].Success) continue;
            var id = match.Groups[i].Value; var course = match.Groups[i + 1].Value;
            if (i == 7) (id, course) = (course, id);
            return IsId(course) && IsId(id) ? (course, id, i < 7 ? "homework" : "exam", "flip") : null;
        }
        return null;
    }
    private static (string Course, string Id, string Kind, string CourseKind)? SharedIdentity(Uri uri)
    {
        if (uri.AbsolutePath != "/stuExamWeb.html" || uri.Query.Length > 0 || !uri.Fragment.StartsWith("#/webExamList/", StringComparison.Ordinal)) return null;
        var fragment = uri.Fragment[1..].Split('?', 2); var parts = fragment[0].Split('/');
        if (parts.Length is not (8 or 9) || parts[1] != "webExamList" || parts[2] is not ("dohomework" or "checkHomework")) return null;
        if (!IsId(parts[3]) || !IsStudentTaskId(Uri.UnescapeDataString(parts[4])) || !IsId(parts[5]) || !IsId(parts[6]) || !IsId(parts[7])) return null;
        if (parts[2] == "dohomework" ? parts.Length != 9 || parts[8] is not ("0" or "1") || fragment.Length != 1
            : parts.Length != 8 || fragment.Length != 2
                || fragment[1] is not ("meetCourseType=0&state=3" or "meetCourseType=0&state=4" or "meetCourseType=1&state=3" or "meetCourseType=1&state=4")) return null;
        return (SharedCourseId(parts[6], parts[3]), SharedTaskId(Uri.UnescapeDataString(parts[4]), parts[5]), "homework", "shared");
    }
    public static string? Key(string platformId, string? address) => platformId == "ucampus" ? UcampusKey(address)
        : platformId == "pta" ? PtaKey(address)
        : platformId == "zhihuishu" && Identity(address) is { } identity
            ? platformId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((identity.CourseKind == "flip" ? "" : identity.CourseKind + "\n") + identity.Course + "\n" + identity.Kind + "\n" + identity.Id))) : null;
    public static bool Matches(PlatformCollectedTask task) => task.PlatformId == "ucampus" ? UcampusMatches(task)
        : task.PlatformId == "pta" ? PtaMatches(task)
        : task.PlatformId == "zhihuishu" && Identity(task.Link) is { } identity
            && identity.Course == task.CourseId && identity.Id == task.RemoteId && identity.Kind == task.Kind && identity.CourseKind == task.CourseKind;
}
