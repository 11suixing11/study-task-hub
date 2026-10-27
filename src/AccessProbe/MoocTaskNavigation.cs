using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public static class MoocTaskNavigation
{
    private static readonly IReadOnlyDictionary<string, string> Routes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["homework"] = "/learn/hw", ["homework_oj"] = "/learn/ojhw", ["quiz"] = "/learn/quiz",
        ["exam_object"] = "/learn/examObject", ["exam_subject"] = "/learn/examSubjective", ["exam_oj"] = "/learn/examOj",
        ["exam_new"] = "/learn/newExamAgreement"
    };

    public static string? Create(MoocCourse course, string kind, string testId, string examId = "", int? examType = null,
        bool platformNullExamType = false)
    {
        if (!MoocPlatform.IsId(course.Id) || !MoocPlatform.IsId(course.TermId) || !MoocPlatform.IsSchool(course.School)
            || !MoocPlatform.IsId(testId) || !Routes.TryGetValue(kind, out var route)
            || course.Mode is not (0 or 10 or 15 or 20)) return null;
        var isExam = kind.StartsWith("exam_", StringComparison.Ordinal);
        if (isExam && !MoocPlatform.IsId(examId)) return null;
        if (kind == "exam_new" && testId != examId) return null;
        if (isExam && kind != "exam_new" && (examType is < 0 or > 10 || examType is null && !platformNullExamType)) return null;
        var query = kind == "exam_new" ? "eid=" + examId
            : isExam ? "eid=" + examId + "&id=" + testId + "&examType=" + (examType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") : "id=" + testId;
        return "https://" + MoocPlatform.Host + (course.IsSpoc ? "/spoc/learn/" : "/learn/")
            + course.School + "-" + course.Id + "?tid=" + course.TermId + "#" + route + "?" + query;
    }

    public static bool IsOfficialTaskLink(string address, string courseId, string termId, string testId, string kind, string examId)
        => Parse(address) is { } value && value.CourseId == courseId && value.TermId == termId
            && value.TestId == testId && value.Kind == kind && value.ExamId == examId;

    public static string? Key(string address)
    {
        var value = Parse(address);
        return value is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    }

    internal static string? RecordingCourseKey(TaskItem task, string accountId)
    {
        if (task.Source != TaskSource.Mooc || !MoocPlatform.IsId(accountId) || task.Link is null || Parse(task.Link) is not { } value) return null;
        var observed = new MoocTask(accountId, value.CourseId, value.TermId, value.TestId, value.ExamId,
            value.Kind, "", "", null, "", "", "", null, null, "", "", null);
        return TaskStore.MoocTaskKey(observed) == task.SourceKey ? CourseRecordingRules.MoocKey(accountId, value.CourseId, value.TermId) : null;
    }

    private sealed record Location(string Path, string CourseId, string TermId, string TestId, string Kind, string ExamId, string ExamType);
    private static Location? Parse(string address)
    {
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.IdnHost != MoocPlatform.Host) return null;
        var prefix = uri.AbsolutePath.StartsWith("/spoc/learn/", StringComparison.Ordinal) ? "/spoc/learn/" : "/learn/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var slug = uri.AbsolutePath[prefix.Length..]; var split = slug.LastIndexOf('-');
        if (split <= 0 || !MoocPlatform.IsSchool(slug[..split]) || !MoocPlatform.IsId(slug[(split + 1)..])) return null;
        var query = RainAddressPolicy.Query(uri);
        if (query is null || query.Count != 1 || !MoocPlatform.IsId(query.GetValueOrDefault("tid"))) return null;
        var hash = uri.Fragment.TrimStart('#'); var separator = hash.IndexOf('?');
        if (separator < 0) return null;
        var route = hash[..separator]; var kind = Routes.FirstOrDefault(pair => pair.Value == route).Key;
        if (kind is null || !Uri.TryCreate("https://" + MoocPlatform.Host + hash, UriKind.Absolute, out var fragment)) return null;
        var fields = RainAddressPolicy.Query(fragment);
        if (kind == "exam_new")
            return fields is not null && fields.Count == 1 && MoocPlatform.IsId(fields.GetValueOrDefault("eid"))
                ? new(uri.AbsolutePath, slug[(split + 1)..], query["tid"], fields["eid"], kind, fields["eid"], "") : null;
        if (fields is null || !MoocPlatform.IsId(fields.GetValueOrDefault("id"))) return null;
        var exam = kind.StartsWith("exam_", StringComparison.Ordinal);
        if (exam ? fields.Count != 3 || !MoocPlatform.IsId(fields.GetValueOrDefault("eid"))
            || fields.GetValueOrDefault("examType") != "null" && (!int.TryParse(fields.GetValueOrDefault("examType"), out var type) || type is < 0 or > 10)
            : fields.Count != 1) return null;
        return new(uri.AbsolutePath, slug[(split + 1)..], query["tid"], fields["id"], kind,
            fields.GetValueOrDefault("eid", ""), fields.GetValueOrDefault("examType", ""));
    }
}
