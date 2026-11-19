using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccessProbe;

public sealed record UaiTokenIdentity([property: JsonIgnore] string OpenId, long? ExpiresAt)
{
    public override string ToString() => nameof(UaiTokenIdentity);
}
public sealed record UaiPortalResponse(string Outcome, [property: JsonIgnore] string Json = "")
{
    public override string ToString() => nameof(UaiPortalResponse);
}
public sealed record UaiPortalProfile(string Outcome, [property: JsonIgnore] string? OpenId = null,
    [property: JsonIgnore] long? SchoolId = null)
{
    public override string ToString() => nameof(UaiPortalProfile);
}
public sealed record UaiPortalSummary(DateTimeOffset CheckedAtUtc, string Outcome, bool AccountVerified,
    int? ShelfResources, int? OverviewTasks, int? DisplayedTasks, bool? UaiModuleAllowed, IReadOnlyList<string> Codes)
{
    public bool FullTaskListsCovered => false;
}

public static class UaiPortal
{
    public const string Id = "uai";
    public const string Host = "uai.unipus.cn";
    public const string ProfilePath = "/api/account/user/info";
    public const string ModulesPath = "/api/portal_api/modules/student";
    public const string ShelfPath = "/api/cmgt/course/my/bookshelf";
    public const string HomeworkPath = "/api/portal/v1/homework/student-aggregation";
    public static Uri Home => new("https://" + Host + "/");
    public const string CoverageText = "已接入独立官网登录校验及首页概要预览。书架资源与教学班、作业归属仍待核对，暂不自动导入 AI 版作业；可先手动记录，任务状态以官网为准。";

    public static string Describe(UaiPortalSummary? summary) => summary is null ? "官网概要尚未读取，任务覆盖未确认。"
        : "最近校验：" + summary.CheckedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            + " · " + (summary.AccountVerified ? "账号已确认" : summary.Outcome == "login_required" ? "需要重新登录" : "校验未完成")
            + (summary.ShelfResources is { } resources ? "\n书架资源 " + resources + " 项，尚未确认为教学班。" : "\n书架概要读取未完成。")
            + (summary.OverviewTasks is { } total ? "\n官网首页作业总数 " + total + "，展示 " + summary.DisplayedTasks + " 条；个人完成状态尚未核对。" : "")
            + "\n完整任务列表尚未接入，已有手动记录保留。";

    public static bool IsLoginNavigation(string address) => TaskRules.IsSafeLink(address)
        && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IdnHost is Host or "sso.unipus.cn";
    public static bool IsPortalDocument(string? address) => TaskRules.IsSafeLink(address)
        && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IdnHost == Host;

    // Decoding binds the saved token to one identity; only the official server verifies it.
    public static UaiTokenIdentity? TokenIdentity(string? token)
    {
        if (token is not { Length: > 0 and <= 16384 } || token.Any(char.IsWhiteSpace)) return null;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/'); payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            var openId = Text(json.RootElement, "openId");
            var expires = Property(json.RootElement, "exp");
            if (!MetadataTaskNavigation.IsId(openId) || expires.ValueKind != JsonValueKind.Undefined && !expires.TryGetInt64(out _)) return null;
            return new(openId!, expires.ValueKind == JsonValueKind.Number ? expires.GetInt64() : null);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    public static bool CookieValid(MetadataSessionCookie cookie)
    {
        if (cookie.Domain is not { Length: > 0 and <= 253 }) return false;
        var domain = cookie.Domain.TrimStart('.').ToLowerInvariant();
        return cookie.Name is { Length: > 0 and <= 256 } && cookie.Name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))
            && cookie.Value is { Length: <= 16384 } && cookie.Value.All(c => c >= 0x21 && c <= 0x7e && c is not (';' or '\\' or '"'))
            && domain is "unipus.cn" or Host or "sso.unipus.cn"
            && cookie.Path is { Length: > 0 and <= 2048 } && cookie.Path.StartsWith('/') && !cookie.Path.Any(c => char.IsControl(c) || c == ';')
            && cookie.Secure && cookie.SameSite is null or "Strict" or "Lax" or "None";
    }

    public static string Outcome(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var code = Property(document.RootElement, "code");
            if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var value)) return "unknown_structure";
            return value == 1 ? "ok" : value == 6017 ? "login_required" : value >= 6000 ? "official_verification_required" : "official_error";
        }
        catch (JsonException) { return "unknown_structure"; }
    }
    public static UaiPortalProfile Profile(string json, string expectedOpenId, long? expectedSchool)
    {
        var outcome = Outcome(json); if (outcome != "ok") return new(outcome);
        using var document = JsonDocument.Parse(json);
        var user = Property(Property(document.RootElement, "value"), "userInfo");
        var account = Text(user, "ssoId"); var school = Property(user, "school"); var role = Property(user, "roleType");
        if (!MetadataTaskNavigation.IsId(account) || school.ValueKind != JsonValueKind.Number || !school.TryGetInt64(out var schoolId) || schoolId <= 0
            || role.ValueKind != JsonValueKind.Number || !role.TryGetInt32(out var roleType)) return new("unknown_structure");
        if (account != expectedOpenId || expectedSchool is { } expected && expected != schoolId) return new("account_mismatch");
        return roleType == 0 ? new("ok", account, schoolId) : new("student_account_required");
    }

    public static (int? Resources, string Outcome) Shelf(string json)
    {
        var outcome = Outcome(json); if (outcome != "ok") return (null, outcome);
        using var document = JsonDocument.Parse(json);
        var groups = Property(Property(document.RootElement, "value"), "groups");
        if (groups.ValueKind != JsonValueKind.Object || !groups.EnumerateObject().Any()) return (null, "unknown_structure");
        var count = 0;
        foreach (var group in groups.EnumerateObject())
        {
            if (group.Name is not ("CLASS_COURSE" or "PERSONAL" or "EXPIRED") || group.Value.ValueKind != JsonValueKind.Array) return (null, "unknown_structure");
            foreach (var row in group.Value.EnumerateArray())
            {
                if (!MetadataTaskNavigation.IsId(Text(row, "resourceId")) || Text(row, "name") is not { Length: > 0 and <= 200 }) return (null, "unknown_structure");
                count++; if (count > 10000) return (null, "limit_reached");
            }
        }
        return (count, "preview");
    }

    public static (int? Total, int? Displayed, string Outcome) Homework(string json)
    {
        var outcome = Outcome(json); if (outcome != "ok") return (null, null, outcome);
        using var document = JsonDocument.Parse(json);
        var homework = Property(Property(document.RootElement, "data"), "homework");
        var total = Property(homework, "total"); var items = Property(homework, "items");
        if (total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out var count) || count < 0 || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 10000) return (null, null, "unknown_structure");
        foreach (var row in items.EnumerateArray())
            if (Text(row, "platform") is not ("UAI" or "iWrite" or "iTranslate") || !MetadataTaskNavigation.IsId(Text(row, "assignmentId"))
                || Text(row, "title") is not { Length: > 0 and <= 300 } || Property(row, "classes").ValueKind != JsonValueKind.Array)
                return (null, null, "unknown_structure");
        if (count < items.GetArrayLength()) return (null, null, "unknown_structure");
        // This API powers the homepage overview. Equal counts still do not prove complete
        // per-class task lists or personal submission state; do not import by class name.
        return (count, items.GetArrayLength(), count > items.GetArrayLength() ? "overview_truncated" : "preview");
    }

    public static (bool? Allowed, string Outcome) Modules(string json)
    {
        var outcome = Outcome(json); if (outcome != "ok") return (null, outcome);
        using var document = JsonDocument.Parse(json); var items = Property(document.RootElement, "data");
        if (items.ValueKind != JsonValueKind.Array) return (null, "unknown_structure");
        foreach (var item in items.EnumerateArray())
            if (Text(item, "module_type") == "UCAMPUS")
            {
                var state = Text(item, "authorization_status");
                return state is { Length: > 0 and <= 64 } ? (state != "disabled", "ok") : (null, "unknown_structure");
            }
        return (null, "module_unconfirmed");
    }
    internal static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    internal static string? Text(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }
}

public sealed class UaiPortalReader(Func<CancellationToken, Task<(string Token, string OpenId, long? School)>> readSession,
    HttpMessageHandler? testHandler = null) : IDisposable
{
    private readonly HttpClient client = new(testHandler ?? new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(25) };
    private bool stopped;
    private DateTimeOffset nextRequest;
    public async Task<UaiPortalResponse> ReadAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (path is not (UaiPortal.ProfilePath or UaiPortal.ModulesPath or UaiPortal.ShelfPath or UaiPortal.HomeworkPath)) return new("unsupported_request");
        if (stopped) return new("rate_limited");
        if (testHandler is null && nextRequest > DateTimeOffset.UtcNow) await Task.Delay(nextRequest - DateTimeOffset.UtcNow, cancellation);
        var session = await readSession(cancellation); var identity = UaiPortal.TokenIdentity(session.Token);
        if (identity is null || identity.OpenId != session.OpenId) return new("account_mismatch");
        if (identity.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return new("login_required");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(UaiPortal.Home, path));
        request.Headers.TryAddWithoutValidation("Authorization", session.Token); request.Headers.TryAddWithoutValidation("u-openid", session.OpenId);
        if (session.School is { } school) request.Headers.TryAddWithoutValidation("u-school", school.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Referrer = UaiPortal.Home; request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("LearningTaskAggregator/1.1.3");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            await CheckSession();
            nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(650);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new("login_required");
            if ((int)response.StatusCode == 429) { stopped = true; return new("rate_limited"); }
            if (response.StatusCode == HttpStatusCode.Forbidden) return new("official_verification_required");
            if ((int)response.StatusCode is >= 300 and < 400) return new("official_redirect_required");
            if (!response.IsSuccessStatusCode) return new("temporarily_unavailable");
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) return new("limit_reached");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation); using var data = new MemoryStream();
            var buffer = new byte[8192]; int length;
            while ((length = await stream.ReadAsync(buffer, cancellation)) > 0)
            { if (data.Length + length > 2 * 1024 * 1024) return new("limit_reached"); data.Write(buffer, 0, length); }
            var json = Encoding.UTF8.GetString(data.ToArray());
            await CheckSession();
            return new(UaiPortal.Outcome(json), json);
        }
        catch (HttpRequestException) { return new("temporarily_unavailable"); }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { return new("temporarily_unavailable"); }

        async Task CheckSession()
        {
            var current = await readSession(cancellation);
            if (current != session)
                throw new OperationCanceledException("The official login changed during the request.", cancellation);
        }
    }
    public void Dispose() => client.Dispose();
}
