using System.Text.Json.Serialization;

namespace AccessProbe;

public static class MoocPlatform
{
    public const string Id = "mooc";
    public const string Name = "中国大学 MOOC";
    public const string Host = "www.icourse163.org";
    public static readonly Uri Home = new("https://www.icourse163.org/home.htm");
    // The official login action uses /member/login.htm with a base64 returnUrl.
    public static readonly Uri Login = new("https://www.icourse163.org/member/login.htm?returnUrl="
        + Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Home.AbsoluteUri))));
    public const string CourseRpc = "/web/j/learnerCourseRpcBean.getMyLearnedCoursePanelList.rpc";
    public const string TermRpc = "/web/j/courseBean.getMocTermDto.rpc";
    public const string LearnedTermRpc = "/web/j/courseBean.getLastLearnedMocTermDto.rpc";
    public const string NewExamRpc = "/web/j/courseBean.getNewExamList.rpc";

    public static bool IsId(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(char.IsAsciiDigit) && value.Any(character => character != '0');
    public static bool IsSchool(string? value) => value is { Length: > 0 and <= 80 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    public static bool IsCookieDomain(string domain) => domain.Equals("icourse163.org", StringComparison.OrdinalIgnoreCase)
        || domain.EndsWith(".icourse163.org", StringComparison.OrdinalIgnoreCase);
    public static bool IsOfficialNavigation(string address) => address == "about:blank"
        || TaskRules.IsSafeLink(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && IsCookieDomain(uri.IdnHost);

    // The official phone/mail login embeds the NetEase URS SDK; the WeChat flow returns through /passport/sns/.
    public static bool IsLoginNavigation(string address) => IsOfficialNavigation(address)
        || TaskRules.IsSafeLink(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && (uri.IdnHost is "open.weixin.qq.com" or "urs.163.com" or "reg.163.com"
                or "webzj.reg.163.com" or "webzj.netstatic.net" or "icourse163.org" or "passport-sym3.netease.com"
                || uri.IdnHost.EndsWith(".reg.163.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith(".urs.163.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith(".icourse163.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith(".dun.163.com", StringComparison.OrdinalIgnoreCase));

    public static bool IsReadRequest(string path, IReadOnlyDictionary<string, string> fields)
    {
        if (path == CourseRpc) return fields.Count == 4 && fields.GetValueOrDefault("type") == "30"
            && fields.GetValueOrDefault("psize") == "8" && fields.GetValueOrDefault("courseType") is "1" or "2"
            && int.TryParse(fields.GetValueOrDefault("p"), out var page) && page is >= 1 and <= 100;
        if (path is NewExamRpc or LearnedTermRpc) return fields.Count == 1 && IsId(fields.GetValueOrDefault("termId"));
        return path == TermRpc && fields.Count == 3 && IsId(fields.GetValueOrDefault("termId"))
            && fields.GetValueOrDefault("isDraft") == "0" && fields.GetValueOrDefault("excludeUnrelease") == "0";
    }
}

public sealed record MoocCourse(string Name,
    [property: JsonIgnore] string Id, [property: JsonIgnore] string TermId,
    string School, int Mode)
{
    public bool IsSpoc => Mode is 10 or 15 or 20;
    public override string ToString() => nameof(MoocCourse);
}

public sealed record MoocTask(
    [property: JsonIgnore] string AccountId, [property: JsonIgnore] string CourseId,
    [property: JsonIgnore] string TermId, [property: JsonIgnore] string RemoteId,
    [property: JsonIgnore] string ExamId,
    string Kind, string Title, string CourseName, TaskState? State,
    string PlatformStatusText, string StateEvidence, string StatusExplanation,
    DateTimeOffset? BeginUtc, DateTimeOffset? DeadlineUtc, string TimeText,
    string ScoreText, [property: JsonIgnore] string? Link)
{
    public override string ToString() => nameof(MoocTask);
}

public sealed record MoocSyncProblem(string Code, string Course, string Message);
public sealed record MoocSyncSummary(int Courses, int CoursesRead, int Tasks, int Added, int Updated,
    bool LoginRequired, bool CourseListCovered, IReadOnlyList<MoocSyncProblem> Problems)
{
    public int TasksWithDeadline { get; init; }
    public int TasksWithLink { get; init; }
    public int StatusNeedsReview { get; init; }
    public int DuplicateRows { get; init; }
    public int Requests { get; init; }
    public int CatalogPages { get; init; }
    public int SpocCourses { get; init; }
    public int NewExamTasks { get; init; }
    public int? SelectedCourses { get; init; }
}

public sealed record MoocSyncSettings
{
    public bool Connected { get; init; }
    public bool AutomaticSyncEnabled { get; init; } = true;
    public DateTimeOffset? LastSyncAtUtc { get; init; }
    public string LastSyncOutcome { get; init; } = "not_started";
    public MoocSyncSummary? LastSummary { get; init; }
}

public sealed record MoocResponse(string Outcome, int? StatusCode, [property: JsonIgnore] string Json = "")
{
    public override string ToString() => nameof(MoocResponse);
}

public interface IMoocMetadataReader
{
    Task<MoocResponse> ReadAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken cancellation);
}

public sealed record MoocCatalogPage(string Outcome, IReadOnlyList<MoocCourse> Courses, int RawCount,
    int? TotalPages, int? TotalCount, int? PageIndex, int DuplicateRows, IReadOnlyList<string> Issues);
public sealed record MoocTaskBatch(string Outcome, IReadOnlyList<MoocTask> Tasks, int DuplicateRows, IReadOnlyList<string> Issues);
