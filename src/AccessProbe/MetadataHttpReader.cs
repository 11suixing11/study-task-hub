using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public sealed partial class MetadataHttpReader : IPlatformMetadataReader, IDisposable
{
    private readonly MetadataPlatform platform;
    private readonly HttpClient client;
    private readonly Func<Uri, CancellationToken, Task<IReadOnlyList<MetadataSessionCookie>>> readCookies;
    private readonly Func<Uri, IReadOnlyList<MetadataSessionCookie>, IReadOnlyList<MetadataSessionCookie>, CancellationToken, Task>? saveCookies;
    private readonly Func<CancellationToken, Task<string?>>? readToken;
    private readonly bool spacing;
    private bool rateLimited;
    private int sentRequests;
    private string? verifiedAccount;
    private byte[]? key;
    private byte[]? sharedKey;
    private byte[]? smartKey;
    private DateTimeOffset nextRequestAt;
    private string? ucampusContentToken, ucampusContentAccount;
    private bool suppressZhihuishuStudentToken;
    private sealed record Renewal(MetadataSessionCookie Cookie, MetadataSessionCookie? Previous);
    private readonly Dictionary<string, Renewal> responseCookies = new(StringComparer.Ordinal);
    public event Action<MetadataRequest, MetadataResponse>? ReadCompleted;
    public MetadataHttpReader(MetadataPlatform platform,
        Func<Uri, CancellationToken, Task<IReadOnlyList<MetadataSessionCookie>>> readCookies, HttpMessageHandler? testHandler = null,
        Func<Uri, IReadOnlyList<MetadataSessionCookie>, IReadOnlyList<MetadataSessionCookie>, CancellationToken, Task>? saveResponseCookies = null,
        Func<CancellationToken, Task<string?>>? readToken = null, ReadOnlyMemory<byte>? testMetadataKey = null,
        ReadOnlyMemory<byte>? testSharedMetadataKey = null, ReadOnlyMemory<byte>? testSmartMetadataKey = null)
    {
        if (testMetadataKey is { } syntheticKey)
        {
            if (testHandler is null || syntheticKey.Length != 16 || platform.Id != "zhihuishu") throw new ArgumentException("Synthetic key requires an isolated test handler.");
            key = syntheticKey.ToArray();
            if (testSharedMetadataKey is { Length: not 16 }) throw new ArgumentException("Synthetic shared key must be 16 bytes.");
            sharedKey = testSharedMetadataKey?.ToArray() ?? syntheticKey.ToArray();
            if (testSmartMetadataKey is { Length: not 16 }) throw new ArgumentException("Synthetic smart key must be 16 bytes.");
            smartKey = testSmartMetadataKey?.ToArray() ?? syntheticKey.ToArray();
        }
        else if (testSharedMetadataKey is not null) throw new ArgumentException("Synthetic shared key requires an isolated test handler and homepage key.");
        else if (testSmartMetadataKey is not null) throw new ArgumentException("Synthetic smart key requires an isolated test handler and homepage key.");
        this.platform = platform; this.readCookies = readCookies; saveCookies = saveResponseCookies; this.readToken = readToken; spacing = testHandler is null;
        client = new(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli }) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json"); client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LearningTaskAggregator/1.3");
        client.DefaultRequestHeaders.Referrer = platform.Id == "zhihuishu" ? new("https://onlinestuh5.zhihuishu.com/") : platform.Home;
    }
    public async Task<MetadataResponse> ReadAsync(MetadataRequest request, CancellationToken cancellation)
    {
        var previousRequests = sentRequests;
        var response = await ReadCoreAsync(request, cancellation);
        if (request.Operation == "profile") verifiedAccount = response.Outcome == "ok" ? response.AccountId : null;
        else response = await RecheckCourseSessionAsync(response, () => ReadCoreAsync(request, cancellation), cancellation);
        response = response with { AdditionalRequests = Math.Max(0, sentRequests - previousRequests - 1) };
        ReadCompleted?.Invoke(request, response);
        return response;
    }
    private async Task<MetadataResponse> RecheckCourseSessionAsync(MetadataResponse response, Func<Task<MetadataResponse>> retry, CancellationToken cancellation)
    {
        if (response.Outcome != "login_required" || verifiedAccount is null || platform.Id is not ("ucampus" or "zhihuishu")) return response;
        var expectedAccount = verifiedAccount;
        var confirmation = await ReadCoreAsync(new("profile"), cancellation);
        ReadCompleted?.Invoke(new("profile"), confirmation);
        if (confirmation.Outcome == "login_required") { verifiedAccount = null; return response with { AdditionalRequests = 1 }; }
        if (confirmation.Outcome == "account_mismatch" || confirmation.Outcome == "ok" && confirmation.AccountId != expectedAccount)
        { verifiedAccount = null; return new("account_mismatch", confirmation.StatusCode) { AdditionalRequests = 1 }; }
        if (confirmation.Outcome != "ok") return new("session_check_unavailable", response.StatusCode) { AdditionalRequests = 1 };
        // A profile check can renew official service cookies. Retry once under the same account.
        var renewed = await retry();
        return renewed with { Outcome = renewed.Outcome == "login_required" ? "course_session_expired" : renewed.Outcome, AdditionalRequests = 2 };
    }
    private async Task<MetadataResponse> ReadCoreAsync(MetadataRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsSupported(platform.Id, request)) return new("unsupported_request", null);
        if (rateLimited) return new("rate_limited", 429);
        try
        {
            if (platform.Id == "pta") return await SendAsync(PtaAddress(request), HttpMethod.Get, null, false, request.Operation, cancellation);
            var sharedTaskRead = request.Operation is "shared-profile" or "shared-exam-profile" or "shared-school" or "shared-homework"
                or "shared-exam-type" or "shared-exams" or "shared-room-exam" or "shared-offline-exam" or "shared-makeup";
            var smartTaskRead = request.Operation is "smart-task-types" or "smart-homework" or "smart-exam";
            var module = sharedTaskRead ? 3 : smartTaskRead ? 6 : 13;
            if (platform.Id == "zhihuishu" && (sharedTaskRead || smartTaskRead || request.Operation is "courses" or "shared-courses" or "smart-courses" or "shared-reminders")
                && (sharedTaskRead ? sharedKey : smartTaskRead ? smartKey : key) is null)
            {
                var handshake = new Uri("https://appcomm-user.zhihuishu.com/app-commserv-user/c/has?uid="
                    + Uri.EscapeDataString(ZhihuishuMetadataCrypto.HandshakeParameter(module)));
                var result = await SendAsync(handshake, HttpMethod.Get, null, false, "handshake", cancellation);
                if (result.Outcome != "ok") return result;
                using var document = JsonDocument.Parse(result.Json);
                var recovered = ZhihuishuMetadataCrypto.RecoverKey(MetadataPlatformParser.Text(MetadataPlatformParser.Property(document.RootElement, "rt"), "sl"), module);
                if (sharedTaskRead) sharedKey = recovered; else if (smartTaskRead) smartKey = recovered; else key = recovered;
            }
            Uri address; HttpMethod method = HttpMethod.Get; string? body = null;
            var now = DateTimeOffset.UtcNow;
            if (platform.Id == "ucampus")
            {
                if (IsUcampusContentOperation(request.Operation) && (verifiedAccount != request.AccountId || ucampusContentAccount != request.AccountId
                    || ucampusContentToken is null)) return new("course_credentials_unavailable", null);
                address = UcampusAddress(request);
                if (request.Operation == "profile")
                {
                    var expectedAccount = MetadataPlatformParser.UcampusAccount(await readCookies(address, cancellation));
                    var profile = await SendAsync(address, HttpMethod.Get, null, true, "profile", cancellation);
                    return profile.Outcome == "login_required" && expectedAccount is not null
                        ? await RenewUcampusSessionAsync(profile, expectedAccount, cancellation) : profile;
                }
            }
            else if (request.Operation == "profile")
                return await CheckZhihuishuSessionAsync(new("https://onlineservice-api.zhihuishu.com/gateway/f/v1/login/getLoginUserInfo?time=" + now.ToUnixTimeMilliseconds()), cancellation);
            else if (smartTaskRead)
            {
                var course = request.Course!;
                address = new("https://kg-run-student.zhihuishu.com/student/gateway/t/"
                    + (request.Operation == "smart-task-types" ? "home/query-course-task-type" : "task/taskList"));
                method = HttpMethod.Post;
                var parameters = request.Operation == "smart-task-types"
                    ? JsonSerializer.Serialize(new { classId = course.Smart!.ClassId, courseId = course.ApiCourseId })
                    : JsonSerializer.Serialize(new { courseId = course.ApiCourseId, taskType = request.Operation == "smart-homework" ? 1 : 2,
                        taskName = "", status = request.Bucket });
                body = JsonSerializer.Serialize(new { secretStr = ZhihuishuMetadataCrypto.Encrypt(parameters, smartKey!), date = now.ToUnixTimeMilliseconds() });
            }
            else if (request.Operation == "smart-courses")
            {
                address = new("https://onlineservice-api.zhihuishu.com/gateway/t/v1/student/queryStudentAICourseList"); method = HttpMethod.Post;
                body = "secretStr=" + Uri.EscapeDataString(ZhihuishuMetadataCrypto.Encrypt(JsonSerializer.Serialize(new { status = request.Bucket }), key!))
                    + "&date=" + now.ToUnixTimeMilliseconds();
            }
            else if (request.Operation == "agent-courses")
            {
                var observed = await readCookies(platform.Home, cancellation); var account = MetadataPlatformParser.ZhihuishuAccount(observed);
                if (account is null) return new("unrecognized_user", null);
                var date = now.ToOffset(TimeSpan.FromHours(8)).ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " GMT+0800 (China Standard Time)";
                address = new("https://cloudapi.polymas.com/student-course/student/index/teacherQueryStudentCourse?uuid="
                    + Uri.EscapeDataString(account) + "&date=" + Uri.EscapeDataString(date)); method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new { term = "", status = 2, keyword = "", crateCourseType = "", page = request.Page,
                    size = MetadataPlatformParser.SharedCoursePageSize, allCourse = false });
            }
            else if (request.Operation == "reminders")
            {
                var observed = await readCookies(platform.Home, cancellation); var account = MetadataPlatformParser.ZhihuishuAccount(observed);
                if (account is null) return new("unrecognized_user", null);
                var date = now.ToOffset(TimeSpan.FromHours(8)).ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " GMT+0800 (China Standard Time)";
                address = new("https://hike-examstu.zhihuishu.com/zhsathome/homework/findImportantReminderList?uuid="
                    + Uri.EscapeDataString(account) + "&date=" + Uri.EscapeDataString(date));
            }
            else if (request.Operation is "flip-homework" or "flip-exam")
            {
                var course = request.Course!;
                var host = request.Operation == "flip-homework" ? "hiexam-server.zhihuishu.com" : "hike-examstu.zhihuishu.com";
                var path = request.Operation == "flip-homework" ? "/zhsathome/homeworkUser/List" : "/zhsathome/examStu/List";
                var parameters = "courseId=" + Uri.EscapeDataString(course.ApiCourseId) + "&key=&pageIndex=" + request.Page + "&pageSize=20&completeTab=0";
                if (request.Operation == "flip-exam")
                {
                    var observed = await readCookies(platform.Home, cancellation); var account = MetadataPlatformParser.ZhihuishuAccount(observed);
                    if (account is null) return new("unrecognized_user", null);
                    parameters += "&uuid=" + Uri.EscapeDataString(account);
                }
                address = new("https://" + host + path + "?" + parameters);
            }
            else if (sharedTaskRead)
            {
                var course = request.Course;
                var host = request.Operation is "shared-exam-profile" or "shared-exam-type" or "shared-exams"
                    or "shared-room-exam" or "shared-offline-exam" or "shared-makeup" ? "taurusexam-api.zhihuishu.com" : "studentexam-api.zhihuishu.com";
                var prefix = host.StartsWith("taurus", StringComparison.Ordinal) ? "/taurusExam/gateway/" : "/studentExam/gateway/";
                var path = request.Operation switch
                {
                    "shared-profile" or "shared-exam-profile" => "f/v1/ulogin/getLoginUserInfo",
                    "shared-school" => "t/v1/exam/getLoginSchoolInfo",
                    "shared-homework" => "t/v1/student/getStudentHomework",
                    "shared-exam-type" => "t/v1/exam/getExamType",
                    "shared-room-exam" => "t/v1/student/getStudentRoomExam",
                    "shared-offline-exam" => "t/v1/student/getStudentOfflineExam",
                    "shared-makeup" => "t/v1/student/hasMakeupExam",
                    "shared-exams" => "t/v1/student/getStudentFinalExam",
                    _ => throw new InvalidOperationException("Unsupported shared metadata operation.")
                };
                var parameters = request.Operation == "shared-homework" ? JsonSerializer.Serialize(new
                    { recruitId = course!.RecruitId, courseId = course.ApiCourseId, flag = request.Bucket, pageSize = 100, pageNum = 0 })
                    : request.Operation is "shared-exams" or "shared-room-exam" or "shared-offline-exam"
                        ? JsonSerializer.Serialize(new { courseId = course!.ApiCourseId, recruitId = course.RecruitId, flag = request.Bucket })
                    : request.Operation == "shared-exam-type" ? JsonSerializer.Serialize(new { recruitId = course!.RecruitId, schoolId = request.SchoolId })
                    : request.Operation == "shared-makeup" ? JsonSerializer.Serialize(new { recruitId = course!.RecruitId })
                    : request.Operation is "shared-profile" or "shared-exam-profile" ? JsonSerializer.Serialize(new { time = now.ToUnixTimeMilliseconds() }) : "{}";
                address = new("https://" + host + prefix + path); method = HttpMethod.Post;
                body = "secretStr=" + Uri.EscapeDataString(ZhihuishuMetadataCrypto.Encrypt(parameters, sharedKey!));
            }
            else if (request.Operation == "shared-courses")
            {
                address = new("https://onlineservice-api.zhihuishu.com/gateway/t/v1/student/course/share/queryShareCourseInfo"); method = HttpMethod.Post;
                var parameters = JsonSerializer.Serialize(new { status = request.Bucket, pageNo = request.Page, pageSize = MetadataPlatformParser.SharedCoursePageSize });
                body = "secretStr=" + Uri.EscapeDataString(ZhihuishuMetadataCrypto.Encrypt(parameters, key!)) + "&date=" + now.ToUnixTimeMilliseconds();
            }
            else
            {
                var parameters = request.Operation == "courses" ? JsonSerializer.Serialize(new { pageNum = request.Page, pageSize = 20 }) : "{}";
                var host = request.Operation == "courses" ? "hike-teaching-api.zhihuishu.com" : "onlineservice-api.zhihuishu.com";
                var path = request.Operation == "courses" ? "/gateway/t/v1/student/online/course/getCourseList" : "/gateway/t/v1/student/getStudentNoCommitExam";
                address = new("https://" + host + path + "?secretStr=" + Uri.EscapeDataString(ZhihuishuMetadataCrypto.Encrypt(parameters, key!)) + "&date=" + now.ToUnixTimeMilliseconds());
            }
            return await SendAsync(address, method, body, true, request.Operation, cancellation);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new("timeout", null); }
        catch (HttpRequestException) { return new("network_error", null); }
        catch (CookieException) { return new("session_unavailable", null); }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException) { return new("unrecognized_encryption", null); }
    }
    public static bool IsSupported(string platformId, MetadataRequest request)
    {
        if (request.Page is < 1 or > MetadataSynchronizer.MaximumPages) return false;
        if (platformId == "ucampus") return IsUcampusRequest(request);
        if (request.AccountId.Length > 0 || request.UnitId.Length > 0) return false;
        if (platformId == "pta") return IsPtaRequest(request);
        if (request.ProblemSetId.Length > 0) return false;
        if (platformId != "zhihuishu") return false;
        if (request.SchoolId.Length > 0 && request.Operation != "shared-exam-type") return false;
        if (request.Operation == "smart-courses") return request.Page == 1 && request.Bucket is 3 or 4 && request.Course is null;
        if (request.Operation == "agent-courses") return request.Bucket == 0 && request.Course is null;
        if (request.Operation is "smart-task-types" or "smart-homework" or "smart-exam")
            return request.Page == 1 && request.Course is { } smart && MetadataTaskNavigation.IsSmartCourse(smart)
                && (request.Operation == "smart-task-types" ? request.Bucket == 0 : request.Bucket is 0 or 1);
        if (request.Operation == "shared-courses") return request.Bucket is 0 or 1 && request.Course is null;
        if (request.Operation == "courses") return request.Bucket == 0 && request.Course is null;
        if (request.Operation is "flip-homework" or "flip-exam") return request.Bucket == 0 && request.Course is { Kind: "flip" } flip
            && MetadataTaskNavigation.IsId(flip.Id) && flip.Id == flip.ApiCourseId;
        if (request.Page != 1) return false;
        if (request.Operation is "shared-homework" or "shared-exams" or "shared-exam-type" or "shared-room-exam" or "shared-offline-exam" or "shared-makeup")
            return request.Course is { Kind: "shared" } shared && MetadataTaskNavigation.IsId(shared.ApiCourseId)
                && MetadataTaskNavigation.IsId(shared.RecruitId) && shared.Id == MetadataTaskNavigation.SharedCourseId(shared.ApiCourseId, shared.RecruitId)
                && (request.Operation == "shared-exam-type" ? request.Bucket == 0 && MetadataTaskNavigation.IsId(request.SchoolId)
                    : request.SchoolId.Length == 0 && (request.Operation == "shared-makeup" ? request.Bucket == 0 : request.Bucket is 1 or 2));
        return request.Bucket == 0 && request.Course is null && request.Operation is "profile" or "reminders" or "shared-reminders" or "shared-profile" or "shared-exam-profile" or "shared-school";
    }
    private async Task<MetadataResponse> SendAsync(Uri address, HttpMethod method, string? body, bool authorize, string operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (sentRequests >= MetadataSynchronizer.MaximumRequests) return new("request_limit", null);
        var observed = await readCookies(address, cancellation);
        var profile = observed.Where(cookie => MetadataSessionStore.IsValid(platform, cookie) && cookie.AppliesTo(address))
            .DistinctBy(cookie => cookie.Key).ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
        var outgoing = new Dictionary<string, MetadataSessionCookie>(profile, StringComparer.Ordinal);
        foreach (var (cookieKey, renewal) in responseCookies.ToArray())
        {
            if (!renewal.Cookie.AppliesTo(address)) continue;
            if (profile.GetValueOrDefault(cookieKey) != renewal.Previous) { responseCookies.Remove(cookieKey); continue; }
            outgoing[cookieKey] = renewal.Cookie;
        }
        var sending = outgoing.Values.Where(cookie => !cookie.IsExpired(DateTimeOffset.UtcNow)).OrderByDescending(cookie => cookie.Path.Length).ToArray();
        using var request = new HttpRequestMessage(method, address);
        var smartTaskRead = operation is "smart-task-types" or "smart-homework" or "smart-exam";
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8,
            smartTaskRead || operation == "agent-courses" ? "application/json" : "application/x-www-form-urlencoded");
        var header = string.Join("; ", sending.Select(cookie => cookie.Name + "=" + cookie.Value));
        if (header.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", header);
        if (address.IdnHost is "studentexam-api.zhihuishu.com" or "taurusexam-api.zhihuishu.com")
            request.Headers.Referrer = new("https://onlineexamh5new.zhihuishu.com/stuExamWeb.html");
        if (smartTaskRead)
        {
            request.Headers.Referrer = new("https://ai-smart-course-student-pro.zhihuishu.com/");
            request.Headers.TryAddWithoutValidation("XQJZXHIZ", ZhihuishuMetadataCrypto.Encrypt("\"\"", smartKey!));
        }
        if (operation == "agent-courses")
        {
            // Only this public directory uses the visible jt-cas cookie from the student home.
            // Polymas cookies and student localStorage tokens are not interchangeable credentials.
            var source = await readCookies(platform.Home, cancellation);
            var valuesForName = source.Where(cookie => MetadataSessionStore.IsValid(platform, cookie) && cookie.AppliesTo(platform.Home)
                    && !cookie.HttpOnly && !cookie.IsExpired(DateTimeOffset.UtcNow) && cookie.Name == "jt-cas")
                .Select(cookie => Uri.UnescapeDataString(cookie.Value)).Distinct(StringComparer.Ordinal).ToArray();
            if (valuesForName.Length != 1 || valuesForName[0].Length is 0 or > 16384 || valuesForName[0].Any(char.IsControl))
                return new("course_credentials_unavailable", null);
            request.Headers.TryAddWithoutValidation("Authorization", valuesForName[0]);
        }
        if (platform.Id == "ucampus")
        {
            request.Headers.Referrer = new("https://u.unipus.cn/user/student/index");
            if (IsUcampusContentOperation(operation))
            {
                request.Headers.TryAddWithoutValidation("X-ANNOTATOR-AUTH-TOKEN", ucampusContentToken);
                request.Headers.TryAddWithoutValidation("uni-client-ver", "12040");
                request.Headers.TryAddWithoutValidation("appid", "5");
            }
            if (operation is "profile" or "ucampus-homework" or "ucampus-exam" or "ucampus-entry-signature")
            {
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
                request.Headers.Pragma.ParseAdd("no-cache");
            }
        }
        if (authorize && platform.Id == "zhihuishu" && !suppressZhihuishuStudentToken && readToken is not null
            && address.IdnHost is "onlineservice-api.zhihuishu.com" or "hike-teaching-api.zhihuishu.com")
        {
            var token = await readToken(cancellation);
            if (token is { Length: > 0 and <= 16384 } && !token.Any(char.IsControl)) request.Headers.TryAddWithoutValidation("Authorization", token);
        }
        if (authorize && platform.Id == "zhihuishu" && operation is "flip-homework" or "flip-exam")
        {
            // These clients read Authorization from a cookie visible to the official exam document.
            // The student homepage localStorage token is a different credential.
            var examDocument = new Uri("https://hiexam.zhihuishu.com/atHomeworkExam/");
            var examCookies = await readCookies(examDocument, cancellation);
            string? authorization = null;
            foreach (var name in new[] { "getByNative", "jt-cas" })
            {
                var valuesForName = examCookies.Where(cookie => MetadataSessionStore.IsValid(platform, cookie)
                        && cookie.AppliesTo(examDocument) && !cookie.HttpOnly && !cookie.IsExpired(DateTimeOffset.UtcNow) && cookie.Name == name)
                    .Select(cookie => Uri.UnescapeDataString(cookie.Value)).Distinct(StringComparer.Ordinal).ToArray();
                if (valuesForName.Length > 1 || valuesForName.Any(value => value.Length > 16384 || value.Any(char.IsControl)))
                    return new("unrecognized_authorization", null);
                if (valuesForName is [var value] && value.Length > 0) { authorization = value; break; }
            }
            if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.Referrer = examDocument;
        }
        if (spacing && nextRequestAt - DateTimeOffset.UtcNow is { } delay && delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
        nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(180);
        sentRequests++;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); cancellation.ThrowIfCancellationRequested();
        var status = (int)response.StatusCode;
        if (response.RequestMessage?.RequestUri is { } actual && actual != address) return new("unsupported_redirect", status);
        var renewed = response.Headers.TryGetValues("Set-Cookie", out var values) ? MetadataResponseCookies.Parse(platform, address, values, DateTimeOffset.UtcNow) : [];
        if (saveCookies is not null) await saveCookies(address, observed, renewed, cancellation);
        foreach (var cookie in renewed) responseCookies[cookie.Key] = new(cookie, profile.GetValueOrDefault(cookie.Key));
        if (status == 401 && platform.Id != "zhihuishu") return new("login_required", status);
        if (response.Headers.TryGetValues("loginstatus", out var loginStatus) && loginStatus.Any(value => value.Equals("timeout", StringComparison.OrdinalIgnoreCase)))
            return new("login_required", status);
        if (status == 429) { rateLimited = true; return new("rate_limited", status); }
        if (status is >= 300 and < 400)
        {
            if (operation is "ucampus-session-renewal" or "zhihuishu-session-renewal")
                return response.Headers.Location is { } renewalLocation && Uri.TryCreate(address, renewalLocation, out var renewalTarget)
                    ? new("session_redirect", status) { SessionRedirect = renewalTarget } : new("unsupported_redirect", status);
            if (response.Headers.Location is { } location && Uri.TryCreate(address, location, out var target)
                && (platform.Id == "ucampus" && IsUcampusLoginRedirect(target, address)
                    || platform.IsLoginNavigation(target.AbsoluteUri) && target.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase))) return new("login_required", status);
            return new("unsupported_redirect", status);
        }
        if (!response.IsSuccessStatusCode && platform.Id != "pta" && status is not (401 or 403)) return new("http_error", status);
        if (response.Content.Headers.ContentLength > MetadataPlatformParser.MaximumBytes) return new("response_too_large", status);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation); using var memory = new MemoryStream(); var buffer = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        { if (memory.Length + count > MetadataPlatformParser.MaximumBytes) return new("response_too_large", status); memory.Write(buffer, 0, count); }
        cancellation.ThrowIfCancellationRequested(); var json = Encoding.UTF8.GetString(memory.ToArray());
        if (platform.Id == "zhihuishu" && operation == "zhihuishu-cas-check")
            return response.IsSuccessStatusCode ? new(ZhihuishuCasOutcome(json), status) : new("session_check_unavailable", status);
        if (platform.Id == "zhihuishu" && operation == "zhihuishu-session-renewal")
            return response.IsSuccessStatusCode && address.IdnHost == platform.Host && address.AbsolutePath == platform.HomePath && json.TrimStart().StartsWith('<')
                ? new("session_document", status) : new("session_check_unavailable", status);
        if (platform.Id == "ucampus" && operation == "ucampus-session-renewal")
            return !response.IsSuccessStatusCode ? new(status == 403 ? "course_access_denied" : "http_error", status)
                : address.IdnHost == "sso.unipus.cn" ? new("login_required", status)
                : address.AbsolutePath is "/user/student" or "/user/student/index" && MetadataPlatformParser.UcampusEnvelope("courses", json) == "ok"
                    ? new("session_document", status) : new("session_check_unavailable", status);
        var outcome = operation == "handshake" ? HandshakeOutcome(json) : MetadataPlatformParser.EnvelopeOutcome(platform, operation, json);
        if (status == 401 && outcome != "course_credentials_unavailable") return new("login_required", status);
        if (status == 403 && outcome is not ("login_required" or "course_credentials_unavailable"))
            return new("course_access_denied", status);
        if (!response.IsSuccessStatusCode && outcome == "ok") outcome = status == 403 ? "course_access_denied" : "http_error";
        string? account = null;
        if (outcome == "ok" && platform.Id == "pta" && operation == "profile") account = MetadataPlatformParser.PtaAccount(json);
        if (outcome == "ok" && platform.Id == "zhihuishu" && operation == "profile")
        {
            var accountCookies = await readCookies(platform.Home, cancellation); cancellation.ThrowIfCancellationRequested();
            account = MetadataPlatformParser.ZhihuishuAccount(accountCookies); outcome = MetadataPlatformParser.ZhihuishuProfile(json, account);
        }
        if (outcome == "ok" && platform.Id == "ucampus" && operation == "profile")
        {
            var accountCookies = await readCookies(new("https://u.unipus.cn/user/data/getToken"), cancellation); cancellation.ThrowIfCancellationRequested();
            account = MetadataPlatformParser.UcampusAccount(accountCookies); outcome = MetadataPlatformParser.UcampusProfile(json, account);
            using var tokenDocument = JsonDocument.Parse(json);
            ucampusContentToken = outcome == "ok" ? MetadataPlatformParser.Text(tokenDocument.RootElement, "token") : null;
            ucampusContentAccount = outcome == "ok" ? account : null;
        }
        return new(outcome, status, outcome == "ok" ? json : "") { AccountId = outcome == "ok" ? account : null };
    }
    private static string HandshakeOutcome(string json)
    {
        using var document = JsonDocument.Parse(json);
        return MetadataPlatformParser.Text(document.RootElement, "status") == "200"
            && MetadataPlatformParser.Text(MetadataPlatformParser.Property(document.RootElement, "rt"), "sl").Length > 0 ? "ok" : "unrecognized_encryption";
    }
    public void Dispose()
    {
        ucampusContentToken = ucampusContentAccount = null; verifiedAccount = null;
        if (key is not null) CryptographicOperations.ZeroMemory(key); if (sharedKey is not null) CryptographicOperations.ZeroMemory(sharedKey);
        if (smartKey is not null) CryptographicOperations.ZeroMemory(smartKey);
        key = sharedKey = smartKey = null; client.Dispose();
    }
}
