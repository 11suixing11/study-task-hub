using System.Text.Json.Serialization;

namespace AccessProbe;

public static class XuetangPlatform
{
    public const string Id = "xuetang";
    public const string Name = "学堂在线";
    public const string Host = "www.xuetangx.com";
    public const string Scope = "current_main_site_standard_courses";
    public const string CoverageText = "采集新版主站普通课程的练习与考试；旧版课程、多课项目、讨论和其他学习活动暂未覆盖。";
    // The desktop /login route mounts a hidden dialog. The official home has the visible login control.
    public static readonly Uri Login = new("https://www.xuetangx.com/");
    public static readonly Uri Home = new("https://www.xuetangx.com/my-courses/current");
    public const string LoginCheck = "/api/v1/u/login/check_is_l/";
    public const string Profile = "/api/v1/u/user/basic_profile/";
    public const string Courses = "/api/v1/lms/user/user-courses/";
    public const string Product = "/api/v1/lms/learn/product/info";
    public const string Chapter = "/api/v1/lms/learn/course/chapter";
    public const string Schedule = "/api/v1/lms/learn/course/schedule";
    public const string ResetDeadlines = "/api/v1/lms/learn/course/reset_leaf/";

    public static bool IsId(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(char.IsAsciiDigit) && value.Any(character => character != '0');
    public static bool IsSign(string? value) => value is { Length: > 0 and <= 256 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    public static bool IsCookieDomain(string domain) => domain.Equals("xuetangx.com", StringComparison.OrdinalIgnoreCase)
        || domain.Equals(Host, StringComparison.OrdinalIgnoreCase);
    public static bool IsOfficialNavigation(string address) => address == "about:blank"
        || TaskRules.IsSafeLink(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && IsCookieDomain(uri.IdnHost);
    public static bool IsLoginNavigation(string address) => IsOfficialNavigation(address)
        || TaskRules.IsSafeLink(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.IdnHost == "open.weixin.qq.com";

    public static bool IsLoginFrameNavigation(string address)
    {
        if (IsLoginNavigation(address)) return true;
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        // Only the verifier documents used by the official login scripts may open inside a frame.
        if (uri.IdnHost == "newassets.hcaptcha.com")
        {
            var parts = uri.AbsolutePath.Split('/');
            return parts.Length == 6 && parts[0] == "" && parts[1] == "captcha" && parts[2] == "v1"
                && parts[3].Length is >= 1 and <= 128 && parts[3].All(char.IsAsciiLetterOrDigit)
                && parts[4] == "static" && parts[5] == "hcaptcha.html";
        }
        return uri.IdnHost is "turing.captcha.qcloud.com" or "ca.turing.captcha.qcloud.com"
            && uri.AbsolutePath is "/template/placeholder_v2.html" or "/template/new_slide_placeholder.html"
                or "/template/drag_ele.html" or "/template/drag_ele_global.html" or "/template/drag_ele_rce.html";
    }

    public static string LoginResourceKind(string address)
    {
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return "none";
        var application = uri.IdnHost == "fe-static-yuketang.yuketang.cn"
            && uri.AbsolutePath.StartsWith("/fe/static/xt/", StringComparison.Ordinal)
            || IsCookieDomain(uri.IdnHost) && uri.AbsolutePath.StartsWith("/xtassets/", StringComparison.Ordinal);
        if (!application) return "none";
        return uri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "script"
            : uri.AbsolutePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "style" : "none";
    }

    public static bool IsReadAddress(Uri address)
    {
        if (!TaskRules.IsSafeLink(address.OriginalString) || address.IdnHost != Host || address.Fragment.Length > 0) return false;
        var fields = RainAddressPolicy.Query(address);
        if (fields is null) return false;
        return address.AbsolutePath switch
        {
            LoginCheck or Profile => fields.Count == 0,
            Courses => fields.Count == 2 && fields.GetValueOrDefault("status") is "1" or "2" or "3"
                && int.TryParse(fields.GetValueOrDefault("page"), out var page) && page is >= 1 and <= 100
                || fields.Count == 1 && fields.GetValueOrDefault("course_select_delete_status") == "1",
            Product or Chapter or Schedule => fields.Count == 2 && IsId(fields.GetValueOrDefault("cid"))
                && IsSign(fields.GetValueOrDefault("sign")),
            ResetDeadlines => fields.Count == 1 && IsId(fields.GetValueOrDefault("sku_id")),
            _ => false
        };
    }
    public static Uri Api(string path, params (string Name, string Value)[] fields) => RainAddressPolicy.Api(Host, path, fields);
}

public sealed record XuetangCourse(string Name,
    [property: JsonIgnore] string ClassroomId, [property: JsonIgnore] string ProductSign,
    [property: JsonIgnore] string CourseSign, int Status, bool Stowed,
    [property: JsonIgnore] string SkuId = "")
{
    public override string ToString() => nameof(XuetangCourse);
}

public sealed record XuetangTask(
    [property: JsonIgnore] string AccountId, [property: JsonIgnore] string ClassroomId,
    [property: JsonIgnore] string ProductSign, [property: JsonIgnore] string CourseSign,
    [property: JsonIgnore] string RemoteId, string Kind, string Title, string CourseName,
    TaskState? State, string PlatformStatusText, string StateEvidence, string StatusExplanation,
    DateTimeOffset? BeginUtc, DateTimeOffset? DeadlineUtc, string TimeText, decimal? ProgressPercent,
    [property: JsonIgnore] string? Link)
{
    public override string ToString() => nameof(XuetangTask);
}

public sealed record XuetangSyncSettings
{
    public bool Connected { get; init; }
    public bool AutomaticSyncEnabled { get; init; } = true;
    public DateTimeOffset? LastSyncAtUtc { get; init; }
    public string LastSyncOutcome { get; init; } = "not_started";
    public XuetangSyncSummary? LastSummary { get; init; }
}

public sealed record XuetangSyncProblem(string Code, string Course, string Message);
public sealed record XuetangSyncSummary(int Courses, int CoursesRead, int Tasks, int Added, int Updated,
    bool LoginRequired, bool CourseListCovered, IReadOnlyList<XuetangSyncProblem> Problems)
{
    public int SelectedCourses { get; init; }
    public int TasksWithDeadline { get; init; }
    public int TasksWithLink { get; init; }
    public int StatusNeedsReview { get; init; }
    public int DuplicateRows { get; init; }
    public int Requests { get; init; }
    public int CatalogPages { get; init; }
    public int UnsupportedCourses { get; init; }
    public string CoverageScope { get; init; } = XuetangPlatform.Scope;
}
public sealed record XuetangResponse(string Outcome, int? StatusCode, [property: JsonIgnore] string Json = "")
{
    public override string ToString() => nameof(XuetangResponse);
}
public interface IXuetangMetadataReader
{
    Task<XuetangResponse> ReadAsync(Uri address, CancellationToken cancellation);
}
