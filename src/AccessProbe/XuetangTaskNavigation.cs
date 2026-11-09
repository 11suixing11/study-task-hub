using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public static class XuetangTaskNavigation
{
    public static string? Link(string product, string course, string classroom, string remote, string kind)
        => XuetangPlatform.IsSign(product) && XuetangPlatform.IsSign(course) && XuetangPlatform.IsId(classroom)
            && XuetangPlatform.IsId(remote) && kind is "exercise" or "exam"
            ? "https://" + XuetangPlatform.Host + "/learn/" + product + "/" + course + "/" + classroom
                + (kind == "exam" ? "/quiz/" : "/exercise/") + remote : null;

    public static string? Key(string address) => Parse(address) is not null
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { XuetangPlatform.Id, new Uri(address).AbsoluteUri })))) : null;

    public static bool IsOfficialTaskLink(string address, string product, string course, string classroom, string remote, string kind)
        => Parse(address) is { } value && value.Product == product && value.Course == course
            && value.Classroom == classroom && value.Remote == remote && value.Kind == kind;

    internal static string? RecordingCourseKey(TaskItem task, string account)
    {
        if (task.Source != TaskSource.Xuetang || task.PlatformId != XuetangPlatform.Id || !XuetangPlatform.IsId(account)
            || task.Link is null || Parse(task.Link) is not { } value) return null;
        var expected = TaskStore.XuetangTaskKey(new(account, value.Classroom, value.Product, value.Course, value.Remote,
            value.Kind, "", "", null, "", "", "", null, null, "", null, task.Link));
        return task.SourceKey == expected ? CourseRecordingRules.XuetangKey(account, value.Classroom, value.Product, value.Course) : null;
    }

    private sealed record Location(string Product, string Course, string Classroom, string Remote, string Kind);
    private static Location? Parse(string address)
    {
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.IdnHost != XuetangPlatform.Host || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 7 || parts[0] != "" || parts[1] != "learn" || !XuetangPlatform.IsSign(parts[2])
            || !XuetangPlatform.IsSign(parts[3]) || !XuetangPlatform.IsId(parts[4]) || !XuetangPlatform.IsId(parts[6])
            || parts[5] is not ("exercise" or "quiz")) return null;
        return new(parts[2], parts[3], parts[4], parts[6], parts[5] == "quiz" ? "exam" : "exercise");
    }
}
