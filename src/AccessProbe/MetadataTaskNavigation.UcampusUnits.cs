using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public static partial class MetadataTaskNavigation
{
    public static string UcampusUnitId(string content, string unit) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { content, unit }))));
    public sealed record UcampusUnitLocation(string CourseId, string RemoteId, string Account, string UnitId, string Route, MetadataCourse Course)
    {
        public override string ToString() => nameof(UcampusUnitLocation);
    }
    public static string? UcampusUnit(MetadataCourse course, string account, string unit, string route)
    {
        if (!MetadataPlatformParser.ValidUcampusContentCourse(course) || !IsId(account) || !MetadataPlatformParser.UcampusContentSegment(unit)
            || !ValidUnitRoute(unit, route)) return null;
        // Local identity is held in an unsigned official course-catalog entry. Only the resolved official route is opened.
        return MetadataPlatformParser.UcampusCatalogAddress(course) + "#learningUnit=" + unit + "&openId=" + Uri.EscapeDataString(account)
            + "&contentId=" + Uri.EscapeDataString(course.Ucampus!.ContentId) + "&version=" + course.Ucampus.Version + "&route=" + Uri.EscapeDataString(route);
    }
    private static bool ValidUnitRoute(string unit, string route)
    {
        var parts = route.Split('/');
        return parts.Length is >= 2 and <= 22 && parts[0] == unit && parts.All(MetadataPlatformParser.UcampusContentSegment);
    }
    public static UcampusUnitLocation? ParseUcampusUnit(string? address)
    {
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.IdnHost != "u.unipus.cn"
            || uri.AbsolutePath != "/user/student/mycourse/courseCatalog" || !uri.Fragment.StartsWith("#learningUnit=", StringComparison.Ordinal)) return null;
        var courseFields = MetadataPlatformParser.UcampusFields(new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri, uri.AbsolutePath);
        var local = RainAddressPolicy.Query(new Uri("https://u.unipus.cn/?" + uri.Fragment[1..]));
        if (courseFields is null || courseFields.Count != 5 || local is null || local.Count != 5
            || !local.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "contentId", "learningUnit", "openId", "route", "version" }.Order(StringComparer.Ordinal))
            || !IsId(local["openId"]) || !MetadataPlatformParser.UcampusContentSegment(local["learningUnit"])
            || !ValidUnitRoute(local["learningUnit"], local["route"])) return null;
        if (!courseFields.TryGetValue("courseId", out var apiCourse) || !courseFields.TryGetValue("classId", out var classId)
            || !courseFields.TryGetValue("eccId", out var eccId) || !courseFields.TryGetValue("school_id", out var school)
            || !courseFields.TryGetValue("coursetype", out var courseType)) return null;
        var context = new UcampusCourseContext(classId, eccId, school, courseType) { ContentId = local["contentId"], Version = local["version"] };
        var course = new MetadataCourse(UcampusCourseId(apiCourse, context), "", "legacy", "") { ApiCourseId = apiCourse, Ucampus = context };
        return MetadataPlatformParser.ValidUcampusContentCourse(course) ? new(course.Id, UcampusUnitId(context.ContentId, local["learningUnit"]),
            local["openId"], local["learningUnit"], local["route"], course) : null;
    }
    private static string? UcampusLearningKey(string? address) => ParseUcampusUnit(address) is { } identity
        ? "ucampus:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { identity.Account, identity.CourseId, "unit", identity.RemoteId })))) : null;
    private static bool UcampusLearningMatches(PlatformCollectedTask task) => task.CourseKind == "legacy" && task.Kind == "unit"
        && ParseUcampusUnit(task.Link) is { } identity && identity.CourseId == task.CourseId && identity.RemoteId == task.RemoteId && identity.Account == task.AccountId;
    public static string? UcampusUnitDestination(UcampusUnitLocation entry)
    {
        if (!MetadataPlatformParser.ValidUcampusContentCourse(entry.Course) || !ValidUnitRoute(entry.UnitId, entry.Route)) return null;
        var context = entry.Course.Ucampus!;
        var result = "https://ucontent.unipus.cn/_pc_" + context.Version + "/pc.html?cid=" + context.ClassId + "&appid=5"
            + (context.SchoolHint.Length > 0 ? "&schId=" + context.SchoolHint : "") + "#/" + context.ContentId + "/courseware/" + entry.Route;
        return TaskRules.IsSafeLink(result) ? result : null;
    }
    private static bool UcampusLearningDestinationMatches(string original, string target) => ParseUcampusUnit(original) is { } entry
        && UcampusUnitDestination(entry) == target;
}
