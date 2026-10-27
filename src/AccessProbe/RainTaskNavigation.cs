using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public static class RainTaskNavigation
{
    private sealed record Location(string Host, string ClassroomId, string RemoteId, string Kind);

    public static string? LeafLink(RainPlatform platform, string host, string classroom, string leaf, string kind, string chapter)
    {
        platform.CheckHost(host);
        if (!RainAddressPolicy.IsId(classroom) || !RainAddressPolicy.IsId(leaf) || kind is not ("exercise" or "exam")) return null;
        var path = "/ai-workspace/lms-graph/" + classroom + (kind == "exercise" ? "/exercise/" : "/quiz/") + leaf;
        return RainAddressPolicy.Api(host, path, RainAddressPolicy.IsId(chapter)
            ? [("is_chapter", "1"), ("node_id", chapter)] : []).AbsoluteUri;
    }

    public static string? LegacyLink(RainPlatform platform, string host, string classroom, string remote, string kind, bool completed)
    {
        platform.CheckHost(host);
        if (!RainAddressPolicy.IsId(classroom) || !RainAddressPolicy.IsId(remote) || kind is not ("quiz" or "exam")) return null;
        var path = kind == "exam" ? "/v2/web/exam/" + classroom + "/" + remote
            : completed ? "/v2/web/quizSummary/" + classroom + "/" + remote
            : "/v2/web/studentQuiz/" + remote + "/1";
        return "https://" + host + path;
    }

    public static string? Key(string platformId, string address)
    {
        var location = Parse(platformId, address);
        if (location is null) return null;
        // Opaque local binding. It contains no account IDs, signed links, or credential material.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { platformId, new Uri(address).AbsoluteUri }))));
    }

    public static bool IsOfficialTaskLink(string platformId, string address, string classroom, string remote, string kind)
    {
        var location = Parse(platformId, address);
        return location is not null && location.RemoteId == remote && location.Kind == kind
            && (location.ClassroomId == classroom || location.ClassroomId == "" && kind == "quiz");
    }

    internal static string? RecordingCourseKey(TaskItem task, string accountId, string classroomId)
    {
        if (task.Source != TaskSource.RainClassroom || !RainAddressPolicy.IsId(accountId) || !RainAddressPolicy.IsId(classroomId)
            || task.Link is null || Parse(task.PlatformId, task.Link) is not { } value
            || value.ClassroomId.Length > 0 && value.ClassroomId != classroomId) return null;
        var observed = new CollectedTask(task.PlatformId, value.Host, accountId, classroomId, value.RemoteId,
            value.Kind, "", "", null, "", "", "", DeadlineKind.Unknown, null, null, "", null, "", null);
        return TaskStore.CollectedTaskKey(observed) == task.SourceKey ? CourseRecordingRules.RainKey(task.PlatformId, value.Host, accountId, classroomId) : null;
    }

    private static Location? Parse(string platformId, string address)
    {
        var platform = RainPlatform.All.FirstOrDefault(value => value.Id == platformId);
        if (platform is null || !TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !platform.Hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) || uri.Fragment.Length > 0) return null;
        var fields = RainAddressPolicy.Query(uri);
        if (fields is null) return null;
        var pieces = uri.AbsolutePath.Trim('/').Split('/');
        if (pieces.Length == 5 && pieces[0] == "ai-workspace" && pieces[1] == "lms-graph"
            && RainAddressPolicy.IsId(pieces[2]) && RainAddressPolicy.IsId(pieces[4]) && pieces[3] is "exercise" or "quiz")
        {
            if (fields.Keys.Any(name => name is not ("is_chapter" or "node_id"))
                || fields.TryGetValue("is_chapter", out var isChapter) && isChapter != "1"
                || fields.TryGetValue("node_id", out var nodeId) && !RainAddressPolicy.IsId(nodeId)) return null;
            return new(uri.IdnHost, pieces[2], pieces[4], pieces[3] == "exercise" ? "exercise" : "exam");
        }
        if (fields.Count != 0 || pieces.Length != 5 || pieces[0] != "v2" || pieces[1] != "web"
            || !RainAddressPolicy.IsId(pieces[3]) || !RainAddressPolicy.IsId(pieces[4])) return null;
        return pieces[2] switch
        {
            "studentQuiz" when pieces[4] == "1" => new(uri.IdnHost, "", pieces[3], "quiz"),
            "quizSummary" => new(uri.IdnHost, pieces[3], pieces[4], "quiz"),
            "exam" => new(uri.IdnHost, pieces[3], pieces[4], "exam"),
            _ => null
        };
    }
}
