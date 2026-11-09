using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public static partial class MetadataTaskNavigation
{
    public static string SmartCourseId(string course, string classId) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { course, classId }))));
    public static bool IsSmartCourse(MetadataCourse course) => course is { Kind: "smart", Smart.SupportsTaskLists: true }
        && IsId(course.ApiCourseId) && IsId(course.Smart.ClassId) && course.Id == SmartCourseId(course.ApiCourseId, course.Smart.ClassId);
    public static string? SmartList(MetadataCourse course, string id, string kind)
    {
        if (!IsSmartCourse(course) || !IsId(id) || kind is not ("homework" or "exam")) return null;
        // taskType is an official category filter. taskId binds our saved navigation to the
        // discovered row; this list route does not start an exam or submit any work.
        return "https://ai-smart-course-student-pro.zhihuishu.com/singleCourse/taskAndExam/"
            + course.ApiCourseId + "/" + course.Smart!.ClassId + "?taskType=" + (kind == "homework" ? 1 : 2) + "&taskId=" + id;
    }
    private static (string Course, string Id, string Kind, string CourseKind)? SmartIdentity(Uri uri)
    {
        if (uri.Fragment.Length != 0) return null;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 5 || parts[1] != "singleCourse" || parts[2] != "taskAndExam" || !IsId(parts[3]) || !IsId(parts[4])) return null;
        var fields = RainAddressPolicy.Query(uri);
        if (fields is null || fields.Count != 2 || !fields.TryGetValue("taskType", out var type) || type is not ("1" or "2")
            || !fields.TryGetValue("taskId", out var id) || !IsId(id)) return null;
        return (SmartCourseId(parts[3], parts[4]), id, type == "1" ? "homework" : "exam", "smart");
    }
}
