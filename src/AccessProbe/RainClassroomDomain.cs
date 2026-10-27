using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AccessProbe;

public sealed record RainPlatform(string Id, string Name, string DefaultHost, IReadOnlyList<string> Hosts)
{
    public Uri LoginUri(string host) => new("https://" + CheckHost(host) + "/web");
    public Uri IndexUri(string host) => new("https://" + CheckHost(host) + "/v2/web/index");
    public string CheckHost(string host) => Hosts.Contains(host, StringComparer.OrdinalIgnoreCase)
        ? host.ToLowerInvariant() : throw new TaskValidationException("RAIN003", "平台域名不属于当前连接。");

    public static readonly RainPlatform Rain = new("yuketang", "雨课堂", "www.yuketang.cn", ["www.yuketang.cn", "pro.yuketang.cn"]);
    public static readonly RainPlatform Changjiang = new("changjiang", "长江雨课堂", "changjiang.yuketang.cn", ["changjiang.yuketang.cn"]);
    public static readonly IReadOnlyList<RainPlatform> All = [Rain, Changjiang];
    public static RainPlatform Find(string id) => All.FirstOrDefault(platform => platform.Id == id)
        ?? throw new TaskValidationException("RAIN003", "不支持的雨课堂连接。");
}

public sealed record PlatformSyncSettings
{
    public bool Connected { get; init; }
    public bool AutomaticSyncEnabled { get; init; } = true;
    public string Host { get; init; } = "";
    public DateTimeOffset? LastSyncAtUtc { get; init; }
    public string LastSyncOutcome { get; init; } = "not_started";
    public RainSyncSummary? LastSummary { get; init; }
}

public sealed record RainClassroom(
    string Name, string CourseName,
    [property: JsonIgnore] string Id,
    [property: JsonIgnore] string CourseId,
    [property: JsonIgnore] string Sign,
    [property: JsonIgnore] string UniversityId,
    [property: JsonIgnore] string PlatformId,
    bool? HasChapterContent = null)
{
    public override string ToString() => nameof(RainClassroom);
}

public sealed record CollectedTask(
    string PlatformId,
    [property: JsonIgnore] string Host,
    [property: JsonIgnore] string AccountId,
    [property: JsonIgnore] string ClassroomId,
    [property: JsonIgnore] string RemoteId,
    string Kind, string Title, string CourseName, TaskState? State, string PlatformStatusText,
    string StateEvidence, string StatusExplanation, DeadlineKind DeadlineKind,
    DateTimeOffset? BeginUtc, DateTimeOffset? DeadlineUtc, string TimeText,
    decimal? ProgressPercent, string ScoreText,
    [property: JsonIgnore] string? Link)
{
    public override string ToString() => nameof(CollectedTask);
}

public sealed record RainSyncProblem(string Code, string Course, string Message);
public sealed record RainSyncSummary(string PlatformId, int Courses, int CoursesRead, int Tasks,
    int Added, int Updated, bool LoginRequired, bool CourseListCovered, IReadOnlyList<RainSyncProblem> Problems)
{
    public int TasksWithDeadline { get; init; }
    public int TasksWithLink { get; init; }
    public int StatusNeedsReview { get; init; }
    public int DuplicateRows { get; init; }
    public int Requests { get; init; }
    public int? SelectedCourses { get; init; }
}

public sealed record RainResponse(string Outcome, int? StatusCode,
    [property: JsonIgnore] string Json = "")
{
    public override string ToString() => nameof(RainResponse);
}

public interface IRainMetadataReader
{
    Task<RainResponse> ReadAsync(Uri address, CancellationToken cancellation);
}

public static class RainAddressPolicy
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly Regex ClassroomPath = new(@"^/v2/api/web/(?:classrooms|logs/learn)/[0-9]{1,64}$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static bool IsId(string? value) => value is { Length: > 0 and <= 64 } && value.All(char.IsAsciiDigit);
    public static bool IsToken(string? value) => value is { Length: > 0 and <= 256 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    public static bool IsNavigation(RainPlatform platform, string address) => address == "about:blank"
        || TaskRules.IsSafeLink(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && platform.Hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    public static bool IsAuthentication(Uri uri) => uri.AbsolutePath is "/web" or "/login"
        || uri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase);

    public static bool IsReadAddress(RainPlatform platform, string host, Uri address)
    {
        if (!platform.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            || !address.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase)
            || !TaskRules.IsSafeLink(address.OriginalString) || address.Fragment.Length > 0) return false;
        var path = address.AbsolutePath;
        if (!(path is "/v2/api/web/userinfo" or "/v2/api/web/courses/list"
            or "/mooc-api/v1/lms/learn/course/chapter" or "/mooc-api/v1/lms/learn/course/schedule"
            || ClassroomPath.IsMatch(path))) return false;
        var fields = Query(address);
        if (fields is null) return false;
        var allowed = path switch
        {
            "/v2/api/web/userinfo" => new[] { "uv_id", "platform_id" },
            "/v2/api/web/courses/list" => ["identity", "uv_id", "platform_id"],
            "/mooc-api/v1/lms/learn/course/chapter" => ["cid", "sign", "classroom_id", "show_unpublished", "uv_id", "platform_id"],
            "/mooc-api/v1/lms/learn/course/schedule" => ["cid", "sign", "classroom_id", "is_h5", "uv_id", "platform_id"],
            _ when path.StartsWith("/v2/api/web/logs/learn/", StringComparison.Ordinal) => ["actype", "page", "offset", "sort", "uv_id", "platform_id"],
            _ => ["role", "uv_id", "platform_id"]
        };
        if (fields.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))) return false;
        foreach (var (key, value) in fields)
        {
            if (key == "sign" ? !IsToken(value)
                : key == "is_h5" ? value != "true"
                : key is "actype" or "sort" ? value != "-1"
                : !IsId(value)) return false;
        }
        if (path == "/v2/api/web/courses/list") return fields.GetValueOrDefault("identity") == "2";
        if (path.Contains("/classrooms/", StringComparison.Ordinal)) return fields.GetValueOrDefault("role") == "5";
        if (path.StartsWith("/mooc-api/", StringComparison.Ordinal))
            return fields.TryGetValue("cid", out var cid) && IsId(cid)
                && (!fields.TryGetValue("classroom_id", out var classroom) || classroom == cid)
                && (!fields.TryGetValue("show_unpublished", out var unpublished) || unpublished == "0");
        if (path.Contains("/logs/learn/", StringComparison.Ordinal))
            return fields.GetValueOrDefault("actype") == "-1" && fields.GetValueOrDefault("sort") == "-1"
                && fields.GetValueOrDefault("offset") == "20" && IsId(fields.GetValueOrDefault("page"));
        return true;
    }

    public static Uri Api(string host, string path, params (string Name, string Value)[] fields) =>
        new("https://" + host + path + (fields.Length > 0 ? "?" + string.Join("&", fields.Select(field =>
            Uri.EscapeDataString(field.Name) + "=" + Uri.EscapeDataString(field.Value))) : ""));

    public static IReadOnlyDictionary<string, string>? Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2 || !result.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]))) return null;
            }
        }
        catch (UriFormatException) { return null; }
        return result;
    }
}
