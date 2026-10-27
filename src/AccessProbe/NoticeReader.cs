using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AccessProbe;

public sealed record NoticeTask(string SourceKey, string Title, string Course, DateTimeOffset? Deadline)
{
    public string DeadlineText => Deadline?.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz") ?? "未知／未提供";
    public string SubmissionState => "未知（通知不证明已提交）";
}

public sealed record NoticeResult(string Outcome, IReadOnlyList<NoticeTask> Tasks, int Total, int Skipped, int InvalidTimes)
{
    public NoticeHttpDiagnostic? HttpDiagnostic { get; init; }
    public string? NextCursor { get; init; }
    public bool LastPage { get; init; }
    public int PageCount { get; init; } = 1;
    public string TerminationReason { get; init; } = "single_page";
    public bool? CursorWasPresent { get; init; }
    public bool NextCursorPresent => CursorWasPresent ?? !string.IsNullOrEmpty(NextCursor);
    public string Coverage { get; init; } = "notification_page_only_not_complete";
    public static NoticeResult Failure(string outcome) => new(outcome, [], 0, 0, 0)
    {
        PageCount = 0,
        TerminationReason = "failure",
        Coverage = "not_verified"
    };
}

public sealed record NoticeHttpDiagnostic(int StatusCode, string RedirectCategory);
public sealed record BrowserNoticeDiagnostic(
    string Outcome,
    bool RequestObserved,
    bool ResponseObserved,
    int? EndpointStatusObservedDuringWindow,
    int? FetchStatusCode,
    string? FetchResponseType);
public sealed record BrowserFetchMessage(int? StatusCode, string? ResponseType, bool Rejected);

public static class NoticeReader
{
    public const string Endpoint = "https://notice.chaoxing.com/pc/notice/getNoticeList";
    public const string RequestMethod = "POST";
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumPages = 5;
    public const int MaximumNotifications = 500;
    private const int MaximumCursorLength = 512;

    public static bool IsFixedNoticeEndpoint(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.IdnHost.Equals("notice.chaoxing.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("/pc/notice/getNoticeList", StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsFixedNoticeRequest(string method, string address) =>
        string.Equals(method, RequestMethod, StringComparison.OrdinalIgnoreCase)
        && IsFixedNoticeEndpoint(address);

    public static Dictionary<string, string> CreateFormFields(string? lastValue = null) => new()
    {
        ["type"] = "0",
        ["notice_type"] = "0",
        ["lastValue"] = lastValue ?? string.Empty,
        ["sort"] = "0",
        ["folderUUID"] = string.Empty,
        ["kw"] = string.Empty,
        ["startTime"] = string.Empty,
        ["endTime"] = string.Empty,
        ["gKw"] = string.Empty,
        ["gName"] = string.Empty,
        ["year"] = string.Empty,
        ["tag"] = string.Empty,
        ["fidsCode"] = string.Empty,
        ["filterSenderPuids"] = string.Empty,
        ["filterTags"] = string.Empty
    };

    public static HttpRequestMessage CreateRequest(string? lastValue = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new FormUrlEncodedContent(CreateFormFields(lastValue));
        return request;
    }

    public static BrowserNoticeDiagnostic ClassifyBrowserProbe(
        bool requestObserved,
        bool responseObserved,
        int? endpointStatusObservedDuringWindow,
        int? fetchStatusCode,
        string? fetchResponseType,
        bool fetchRejected)
    {
        var safeResponseType = fetchResponseType is "basic" or "cors" or "opaque" or "opaqueredirect" or "error"
            ? fetchResponseType : null;
        var outcome = safeResponseType == "opaqueredirect" ? "redirect_status_hidden"
            : safeResponseType == "opaque" ? "opaque_response"
            : fetchStatusCode.HasValue ? "response_received"
            : !fetchRejected ? "fetch_response_without_http_event"
            : responseObserved && endpointStatusObservedDuringWindow.HasValue ? "fetch_rejected_endpoint_status_observed"
            : responseObserved ? "fetch_rejected_response_without_status"
            : requestObserved ? "fetch_rejected_after_request_observed"
            : "fetch_rejected_without_request_observed";
        return new BrowserNoticeDiagnostic(outcome, requestObserved, responseObserved,
            endpointStatusObservedDuringWindow, fetchStatusCode, safeResponseType);
    }

    public static bool TryParseBrowserFetchMessage(string messageAsJson, string expectedNonce, out BrowserFetchMessage? message)
    {
        message = null;
        try
        {
            using var document = JsonDocument.Parse(messageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("nonce", out var nonceProperty)
                || nonceProperty.ValueKind != JsonValueKind.String
                || !string.Equals(nonceProperty.GetString(), expectedNonce, StringComparison.Ordinal)
                || !root.TryGetProperty("kind", out var kindProperty)
                || kindProperty.ValueKind != JsonValueKind.String)
                return false;

            var kind = kindProperty.GetString();
            if (kind == "fetch_rejected")
            {
                message = new BrowserFetchMessage(null, null, true);
                return true;
            }

            if (kind != "response"
                || !root.TryGetProperty("status", out var statusProperty)
                || statusProperty.ValueKind != JsonValueKind.Number
                || !statusProperty.TryGetInt32(out var status)
                || status is < 0 or > 599
                || !root.TryGetProperty("responseType", out var typeProperty)
                || typeProperty.ValueKind != JsonValueKind.String)
                return false;

            message = new BrowserFetchMessage(status, typeProperty.GetString(), false);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task<NoticeResult> FetchAsync(HttpClient client, CancellationToken cancellation, string? lastValue = null)
    {
        using var request = CreateRequest(lastValue);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        var redirect = (int)response.StatusCode is >= 300 and < 400;
        var diagnostic = new NoticeHttpDiagnostic((int)response.StatusCode,
            redirect ? ClassifyRedirect(response) : "not_applicable");
        NoticeResult Failure(string outcome) => NoticeResult.Failure(outcome) with { HttpDiagnostic = diagnostic };
        if (redirect) return Failure("redirect_received");
        if (response.StatusCode == HttpStatusCode.Unauthorized) return Failure("unauthorized");
        if (response.StatusCode == HttpStatusCode.Forbidden) return Failure("forbidden");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return Failure("rate_limited");
        if (!response.IsSuccessStatusCode) return Failure("http_error");
        if (response.Content.Headers.ContentLength > MaximumBytes) return Failure("response_too_large");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var memory = new System.IO.MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellation);
            if (count == 0) break;
            if (memory.Length + count > MaximumBytes) return Failure("response_too_large");
            memory.Write(buffer, 0, count);
        }
        return Parse(System.Text.Encoding.UTF8.GetString(memory.ToArray())) with { HttpDiagnostic = diagnostic };
    }

    public static async Task<NoticeResult> FetchRecentAsync(HttpClient client, CancellationToken cancellation)
    {
        var tasks = new List<NoticeTask>();
        var total = 0;
        var skipped = 0;
        var invalidTimes = 0;
        var pageCount = 0;
        var cursor = (string?)null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var nextCursorPresent = false;
        NoticeHttpDiagnostic? diagnostic = null;

        NoticeResult Aggregate(string terminationReason) => new("parsed_partial", tasks, total, skipped, invalidTimes)
        {
            HttpDiagnostic = diagnostic,
            PageCount = pageCount,
            TerminationReason = terminationReason,
            CursorWasPresent = nextCursorPresent,
            Coverage = pageCount > 1 ? "notification_recent_window_not_complete" : "notification_page_only_not_complete"
        };

        for (var page = 0; page < MaximumPages; page++)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = await FetchAsync(client, cancellation, cursor);
            if (result.Outcome != "parsed_partial")
                return result with
                {
                    PageCount = pageCount,
                    TerminationReason = "request_failure",
                    Coverage = "not_verified"
                };

            pageCount++;
            diagnostic = result.HttpDiagnostic;
            total += result.Total;
            skipped += result.Skipped;
            invalidTimes += result.InvalidTimes;
            tasks.AddRange(result.Tasks);
            nextCursorPresent = result.NextCursorPresent;

            if (result.Total == 0) return Aggregate("empty_page");
            if (result.LastPage) return Aggregate("last_page");
            if (!result.NextCursorPresent) return Aggregate("cursor_missing");
            if (cursor is not null && string.Equals(result.NextCursor, cursor, StringComparison.Ordinal))
                return Aggregate("cursor_repeated");
            if (!seenCursors.Add(result.NextCursor!)) return Aggregate("cursor_repeated");
            if (total >= MaximumNotifications) return Aggregate("notification_limit");
            cursor = result.NextCursor;
        }

        return Aggregate("page_limit");
    }

    private static string ClassifyRedirect(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Location", out var values)) return "missing_location";
        var locations = values.ToArray();
        if (locations.Length != 1 || string.IsNullOrWhiteSpace(locations[0])) return "invalid_location";
        if (!Uri.TryCreate(new Uri(Endpoint), locations[0], out var target)) return "invalid_location";
        var category = ProbePolicy.NavigationCategory(target.AbsoluteUri);
        if (category != "official_https") return category;
        if (target.IdnHost.Equals("passport2.chaoxing.com", StringComparison.OrdinalIgnoreCase)
            || target.IdnHost.Equals("passport2.xuexitong.com", StringComparison.OrdinalIgnoreCase))
            return "official_auth_host_https";
        return target.IdnHost.Equals(new Uri(Endpoint).IdnHost, StringComparison.OrdinalIgnoreCase)
            ? "same_host_https" : "other_official_host_https";
    }

    public static NoticeResult Parse(string content)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaximumBytes) return NoticeResult.Failure("response_too_large");
        if (content.TrimStart().StartsWith('<')) return NoticeResult.Failure("html_not_task_data");
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var status = Property(root, "status");
            if (status.ValueKind != JsonValueKind.Undefined && !IsTruthyStatus(status))
                return NoticeResult.Failure("api_rejected");
            var notices = Property(root, "notices");
            var list = Property(notices, "list");
            if (list.ValueKind != JsonValueKind.Array) return NoticeResult.Failure("schema_unrecognized");
            var nextCursor = NormalizeCursor(Text(Property(notices, "lastGetId")));
            var lastPage = IsTruthyStatus(Property(notices, "lastPage"));
            var tasks = new List<NoticeTask>();
            var skipped = 0;
            var invalidTimes = 0;
            foreach (var item in list.EnumerateArray())
            {
                try
                {
                    var parameters = Property(Property(item, "extendParam"), "cparams");
                    if (parameters.ValueKind != JsonValueKind.String) { skipped++; continue; }
                    using var nested = JsonDocument.Parse(parameters.GetString()!);
                    var config = Property(nested.RootElement, "funConfig");
                    var tag = Property(config, "funTag");
                    if (tag.ValueKind != JsonValueKind.Number || !tag.TryGetInt32(out var value)) { skipped++; continue; }
                    if (value != 3) continue;
                    var details = Property(config, "content");
                    var key = Text(Property(details, "key"));
                    var title = Text(Property(details, "title"));
                    var course = Text(Property(details, "courseName"));
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(course))
                    { skipped++; continue; }
                    var rawTime = Text(Property(details, "endTime"));
                    DateTimeOffset? deadline = null;
                    if (!string.IsNullOrEmpty(rawTime) && rawTime != "0")
                    {
                        if (long.TryParse(rawTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
                            && milliseconds >= 946684800000L && milliseconds <= 7258118400000L)
                            deadline = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                        else invalidTimes++;
                    }
                    tasks.Add(new NoticeTask(key, title, course, deadline));
                }
                catch (JsonException) { skipped++; }
            }
            return new NoticeResult("parsed_partial", tasks, list.GetArrayLength(), skipped, invalidTimes)
            {
                NextCursor = nextCursor,
                LastPage = lastPage
            };
        }
        catch (JsonException) { return NoticeResult.Failure("schema_unrecognized"); }
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) ? property : default;

    private static bool IsTruthyStatus(JsonElement status) => status.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null => false,
        JsonValueKind.Number => status.TryGetDouble(out var number) && number != 0,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(status.GetString())
            && !string.Equals(status.GetString(), "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status.GetString(), "0", StringComparison.Ordinal),
        _ => false
    };

    private static string? NormalizeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        return cursor.Length <= MaximumCursorLength ? cursor : null;
    }

    private static string? Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null
    };
}
