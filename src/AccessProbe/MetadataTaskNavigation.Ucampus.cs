using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public static partial class MetadataTaskNavigation
{
    private const string UcampusEntryPath = "/uexercise/api/v1/enter_exercise_exam";
    private const string UcampusViewPath = "/uexercise/api/v1/enter_check_student_exam_detail";
    public static string UcampusCourseId(string course, UcampusCourseContext context) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { course, context.ClassId, context.EccId, context.SchoolHint, context.CourseType }))));
    public static string UcampusTaskId(string homework, string classId, string student) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { homework, classId, student }))));
    public static string? Ucampus(MetadataCourse course, string account, string homework, string student, bool submitted, string kind = "homework")
    {
        var exam = kind == "exam";
        if (!MetadataPlatformParser.ValidUcampusCourse(course) || !IsId(account) || !MetadataPlatformParser.UcampusNumber(homework)
            || !MetadataPlatformParser.UcampusNumber(student) || kind is not ("homework" or "exam")
            || !MetadataPlatformParser.UcampusNumber(exam ? course.Ucampus!.ExamListSchoolId : course.Ucampus!.ListSchoolId)) return null;
        var context = course.Ucampus!;
        var returnAddress = exam ? context.ExamAddress : context.HomeworkAddress;
        var returnFields = MetadataPlatformParser.UcampusFields(returnAddress, "/user/student/homework/index");
        if (returnFields is null || returnFields.GetValueOrDefault("courseId") != course.ApiCourseId || returnFields.GetValueOrDefault("classId") != context.ClassId
            || returnFields.GetValueOrDefault("eccId") != context.EccId || returnFields.GetValueOrDefault("school_id") != context.SchoolHint) return null;
        // Store an unsigned, account-bound entry. A signature is obtained only on an explicit click.
        // courseType is local navigation context, removed before opening the official entry.
        var forward = returnAddress + "#courseType=" + context.CourseType;
        return "https://uexercise.unipus.cn" + (submitted ? UcampusViewPath : UcampusEntryPath) + "?plf=0&schoolId=" + (exam ? context.ExamListSchoolId : context.ListSchoolId)
            + (submitted ? "&clsId=" : "&clazzId=") + context.ClassId + "&studentId=" + student + "&exerciseId=" + homework
            + "&openId=" + account + (submitted ? "" : "&exerciseType=" + (exam ? "2" : "1")) + "&forwardUrl=" + Uri.EscapeDataString(forward);
    }
    public sealed record UcampusLocation(string CourseId, string RemoteId, string Account, string Homework, string ClassId,
        string Student, string School, string Kind, bool Submitted, string Forward, Uri Entry)
    {
        public override string ToString() => nameof(UcampusLocation);
    }
    public static UcampusLocation? ParseUcampus(string? address)
    {
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.IdnHost != "uexercise.unipus.cn"
            || uri.AbsolutePath is not (UcampusEntryPath or UcampusViewPath) || uri.Fragment.Length > 0) return null;
        var fields = RainAddressPolicy.Query(uri); var submitted = uri.AbsolutePath == UcampusViewPath;
        if (fields is null || fields.Count != (submitted ? 7 : 8) || fields.GetValueOrDefault("plf") != "0"
            || !MetadataPlatformParser.UcampusNumber(fields.GetValueOrDefault("schoolId")) || !IsId(fields.GetValueOrDefault("openId"))
            || !MetadataPlatformParser.UcampusNumber(fields.GetValueOrDefault("exerciseId")) || !MetadataPlatformParser.UcampusNumber(fields.GetValueOrDefault("studentId"))
            || !MetadataPlatformParser.UcampusNumber(fields.GetValueOrDefault(submitted ? "clsId" : "clazzId"))
            || !submitted && fields.GetValueOrDefault("exerciseType") is not ("1" or "2") || !fields.TryGetValue("forwardUrl", out var forward)
            || !TaskRules.IsSafeLink(forward) || !Uri.TryCreate(forward, UriKind.Absolute, out var returnUri)
            || !returnUri.Fragment.StartsWith("#courseType=", StringComparison.Ordinal)) return null;
        var courseType = returnUri.Fragment[12..];
        if (!MetadataPlatformParser.UcampusNumber(courseType)) return null;
        var cleanReturn = new UriBuilder(returnUri) { Fragment = "" }.Uri.AbsoluteUri;
        var returning = MetadataPlatformParser.UcampusFields(cleanReturn, "/user/student/homework/index");
        var classId = fields[submitted ? "clsId" : "clazzId"];
        var exam = returning?.GetValueOrDefault("type") == "2";
        if (returning is null || returning.Count != (exam ? 6 : 5) || !MetadataPlatformParser.UcampusNumber(returning.GetValueOrDefault("courseId"))
            || returning.GetValueOrDefault("classId") != classId || !MetadataPlatformParser.UcampusNumber(returning.GetValueOrDefault("eccId"))
            || !MetadataPlatformParser.UcampusNumber(returning.GetValueOrDefault("school_id"), true)
            || !MetadataPlatformParser.UcampusNumber(returning.GetValueOrDefault("activationStatus"))
            || !submitted && fields.GetValueOrDefault("exerciseType") != (exam ? "2" : "1")) return null;
        var context = new UcampusCourseContext(classId, returning["eccId"], returning["school_id"], courseType);
        return new(UcampusCourseId(returning["courseId"], context), UcampusTaskId(fields["exerciseId"], classId, fields["studentId"]),
            fields["openId"], fields["exerciseId"], classId, fields["studentId"], fields["schoolId"], exam ? "exam" : "homework", submitted, cleanReturn, uri);
    }
    private static string? UcampusKey(string? address) => UcampusLearningKey(address) ?? (ParseUcampus(address) is { } identity
        ? "ucampus:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { identity.Account, identity.CourseId, identity.Kind, identity.RemoteId })))) : null);
    private static bool UcampusMatches(PlatformCollectedTask task) => UcampusLearningMatches(task) || task.CourseKind == "legacy" && task.Kind is "homework" or "exam"
        && ParseUcampus(task.Link) is { } identity && identity.CourseId == task.CourseId && identity.RemoteId == task.RemoteId && identity.Account == task.AccountId && identity.Kind == task.Kind;
    public static string? UcampusSignedAddress(UcampusLocation original, string signature, string? exerciseType)
    {
        if (signature is not { Length: > 0 and <= 4096 } || signature.Any(character => !char.IsAsciiLetterOrDigit(character)
            && character is not ('-' or '_' or '+' or '/' or '='))) return null;
        if (original.Submitted && (original.Kind == "exam" ? exerciseType != "2" : exerciseType is not ("1" or "4"))) return null;
        var target = "https://uexercise.unipus.cn" + (original.Submitted ? UcampusViewPath : UcampusEntryPath)
            + "?plf=0&schoolId=" + original.School + (original.Submitted ? "&clsId=" : "&clazzId=") + original.ClassId
            + "&studentId=" + original.Student + "&exerciseId=" + original.Homework + (original.Submitted ? "" : "&openId=" + original.Account)
            + "&exerciseType=" + (original.Submitted ? exerciseType : original.Kind == "exam" ? "2" : "1") + "&forwardUrl=" + Uri.EscapeDataString(original.Forward)
            + "&sign=" + Uri.EscapeDataString(signature);
        return TaskRules.IsSafeLink(target) ? target : null;
    }
    public static bool UcampusDestinationMatches(string originalAddress, string targetAddress)
    {
        if (ParseUcampusUnit(originalAddress) is not null) return UcampusLearningDestinationMatches(originalAddress, targetAddress);
        var original = ParseUcampus(originalAddress);
        if (original is null || !TaskRules.IsSafeLink(targetAddress) || !Uri.TryCreate(targetAddress, UriKind.Absolute, out var target)
            || target.IdnHost != "uexercise.unipus.cn" || target.AbsolutePath != original.Entry.AbsolutePath || target.Fragment.Length > 0) return false;
        var fields = RainAddressPolicy.Query(target);
        if (fields is null || fields.Count != (original.Submitted ? 8 : 9) || fields.GetValueOrDefault("plf") != "0" || fields.GetValueOrDefault("schoolId") != original.School
            || fields.GetValueOrDefault(original.Submitted ? "clsId" : "clazzId") != original.ClassId
            || fields.GetValueOrDefault("exerciseId") != original.Homework || fields.GetValueOrDefault("studentId") != original.Student
            || fields.GetValueOrDefault("forwardUrl") != original.Forward || !fields.TryGetValue("sign", out var signature)) return false;
        return (original.Submitted || fields.GetValueOrDefault("openId") == original.Account && fields.GetValueOrDefault("exerciseType") == (original.Kind == "exam" ? "2" : "1"))
            && UcampusSignedAddress(original, signature, fields.GetValueOrDefault("exerciseType")) == target.AbsoluteUri;
    }
}
