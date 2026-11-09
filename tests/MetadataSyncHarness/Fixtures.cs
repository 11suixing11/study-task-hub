using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Data = PlatformMetadataTestData.Fixtures;
using U = PlatformMetadataTestData.UcampusFixtures;

namespace MetadataSyncHarness;

internal sealed class Fixtures(MetadataPlatform platform)
{
    public MetadataPlatform Platform { get; } = platform;
    public string Account { get; set; } = Data.Account;
    public string Session { get; set; } = "synthetic-session-seed";
    public bool RejectLogin { get; set; }
    public bool UnknownCatalog { get; set; }
    public bool UnknownProfile { get; set; }
    public bool DenyProfile { get; set; }
    public bool MissingStudentToken { get; set; }
    public bool RedirectUcampusLogin { get; set; }
    public bool RejectCourseSession { get; set; }
    public bool RejectCourseOnce { get; set; }
    public bool RenewAccountSession { get; set; }
    public bool AccountSessionRenewed { get; set; }
    public bool IncludeLearningUnits { get; set; }
    public bool UnknownLearningCatalog { get; set; }
    public List<string> RenewalPaths { get; } = [];
    public bool UnknownReminders { get; set; }
    public bool RenewNext { get; set; }
    public bool LateRenewal { get; set; }
    public bool DelayStudentToken { get; set; }
    public bool IncludeUnrelatedTokenFrame { get; set; }
    public bool SimulateZhihuishuLoginReturn { get; set; }
    public bool InjectLoginScript { get; set; }
    public bool FailLoginScript { get; set; }
    public bool IncludeSmartCourses { get; set; }
    public int LoginScriptRequests { get; private set; }
    public int HealthyLoginScriptRequests { get; private set; }
    public bool StudentDocumentLoadedWithoutToken { get; private set; }
    public CoreWebView2Frame? StudentFrame { get; private set; }
    public int Requests { get; private set; }
    public int BrowserRequests { get; private set; }
    public List<string> BrowserDocumentRoutes { get; } = [];
    public List<string> Paths { get; } = [];
    public const string ZhihuishuStudentHome = "https://onlineweb.zhihuishu.com/onlinestuh5";
    public static readonly string ZhihuishuSsoReturn = "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect="
        + Uri.EscapeDataString(ZhihuishuStudentHome);
    public static readonly string ZhihuishuPassportReturn = "https://passport.zhihuishu.com/login?service="
        + Uri.EscapeDataString(ZhihuishuSsoReturn);
    public TaskCompletionSource<bool>? Entered { get; private set; }
    public TaskCompletionSource<bool>? Release { get; private set; }
    public void PauseReminders() { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void PauseUcampusCatalog() { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void ResetPause() { Entered = Release = null; LateRenewal = false; }
    public string AuthName => "SYNTHETIC_SESSION";
    public void SetCookies(WebView2 browser, string? session = null)
    {
        var manager = browser.CoreWebView2.CookieManager; var domain = Platform.Id == "ucampus" ? ".unipus.cn" : ".zhihuishu.com";
        var value = manager.CreateCookie(AuthName, session ?? Session, domain, "/"); value.IsSecure = true; value.IsHttpOnly = true; value.Expires = DateTime.MinValue;
        value.SameSite = CoreWebView2CookieSameSiteKind.Strict; manager.AddOrUpdateCookie(value);
        if (Platform.Id == "zhihuishu")
        {
            var cookie = Data.Cookie(Account); value = manager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path); value.IsSecure = true; value.Expires = DateTime.MinValue;
            manager.AddOrUpdateCookie(value);
            value = manager.CreateCookie("jt-cas", "synthetic-flip-token", ".zhihuishu.com", "/"); value.IsSecure = true;
            manager.AddOrUpdateCookie(value);
        }
        else if (Platform.Id == "ucampus")
        {
            var cookie = U.Cookie(Account == Data.Account ? U.Account : Account);
            value = manager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path); value.IsSecure = true; value.Expires = DateTime.MinValue;
            manager.AddOrUpdateCookie(value);
        }
    }
    public void BlockBrowser(WebView2 browser)
    {
        browser.CoreWebView2InitializationCompleted += (_, result) =>
        {
            if (!result.IsSuccess) return; var core = browser.CoreWebView2;
            if (Platform.Id == "zhihuishu") core.FrameCreated += (_, created) =>
            {
                var frame = created.Frame;
                frame.DOMContentLoaded += async (_, _) =>
                {
                    try
                    {
                        var eligible = await frame.ExecuteScriptAsync("location.origin === 'https://onlinestuh5.zhihuishu.com' && location.pathname === '/'");
                        if (eligible != "true") return;
                        StudentFrame = frame;
                        StudentDocumentLoadedWithoutToken = await frame.ExecuteScriptAsync("localStorage.getItem('token') === null") == "true";
                    }
                    catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
                };
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                BrowserRequests++;
                var uri = new Uri(request.Request.Uri);
                if (request.ResourceContext == CoreWebView2WebResourceContext.Document)
                    BrowserDocumentRoutes.Add(uri.IdnHost + uri.AbsolutePath);
                if (Platform.Id == "zhihuishu" && InjectLoginScript && uri.IdnHost == "login.zhihuishu.com" && uri.AbsolutePath == "/assets/synthetic-login.js")
                {
                    LoginScriptRequests++;
                    var body = FailLoginScript ? "<!doctype html><title>合成失效脚本的 HTML 回退页</title>"
                        : "document.body.setAttribute('data-synthetic-script','ready');"
                            + (SimulateZhihuishuLoginReturn ? "location.replace(" + JsonSerializer.Serialize(ZhihuishuPassportReturn) + ");" : "");
                    if (!FailLoginScript) HealthyLoginScriptRequests++;
                    request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), 200, "OK",
                        "Content-Type: " + (FailLoginScript ? "text/html" : "text/javascript") + "; charset=utf-8\r\nCache-Control: public, max-age=86400\r\n");
                    return;
                }
                if (Platform.Id == "zhihuishu" && SimulateZhihuishuLoginReturn)
                {
                    string? target = null;
                    if (uri.IdnHost == "passport.zhihuishu.com" && uri.AbsolutePath == "/login")
                        target = ZhihuishuSsoReturn;
                    else if (uri.IdnHost == "cloudapi.polymas.com" && uri.AbsolutePath is "/oauth/login/ssoLogin" or "/oauth/login/checkLogin")
                    {
                        var name = uri.AbsolutePath.EndsWith("ssoLogin", StringComparison.Ordinal) ? "redirect=" : "url=";
                        var field = uri.Query.TrimStart('?').Split('&').FirstOrDefault(field => field.StartsWith(name, StringComparison.Ordinal));
                        if (field is not null) target = Uri.UnescapeDataString(field[name.Length..]);
                    }
                    if (target is not null)
                    {
                        request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 302, "Found",
                            "Location: " + target + "\r\nCache-Control: no-store\r\n");
                        return;
                    }
                }
                var html = "<!doctype html><meta charset='utf-8'><title>本地平台接入验收</title><p>合成官方登录页；没有连接任何真实账号。</p>";
                if (Platform.Id == "zhihuishu")
                {
                    html += InjectLoginScript && uri.IdnHost == "login.zhihuishu.com" && uri.AbsolutePath == "/"
                        ? "<script src='/assets/synthetic-login.js'></script>"
                        : SimulateZhihuishuLoginReturn && uri.IdnHost == "login.zhihuishu.com" && uri.AbsolutePath == "/"
                        ? "<script>location.replace(" + JsonSerializer.Serialize(ZhihuishuPassportReturn) + ");</script>"
                        : uri.AbsolutePath == "/synthetic-unrelated"
                        ? "<script>localStorage.setItem('token','synthetic-unrelated-token');</script>"
                        : uri.IdnHost == "onlinestuh5.zhihuishu.com"
                            ? DelayStudentToken
                                ? "<script>localStorage.removeItem('token');document.addEventListener('DOMContentLoaded',()=>setTimeout(()=>localStorage.setItem('token','synthetic-localstorage-token'),2500));</script>"
                                : "<script>localStorage.setItem('token','synthetic-localstorage-token');</script>"
                            : "<iframe src='https://onlinestuh5.zhihuishu.com/'></iframe>"
                                // localStorage is origin-wide; use a different official origin
                                // to exercise rejection of an unrelated frame credential.
                                + (IncludeUnrelatedTokenFrame ? "<iframe src='https://hiexam.zhihuishu.com/synthetic-unrelated'></iframe>" : "");
                }
                request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            };
        };
    }
    public HttpMessageHandler CreateHandler() => new Handler(this);
    private sealed class Handler(Fixtures fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); fixture.Requests++; var uri = request.RequestUri!; fixture.Paths.Add(uri.AbsolutePath);
            if (fixture.RenewAccountSession)
            {
                var profilePath = fixture.Platform.Id == "ucampus" ? "/user/data/getToken" : "/gateway/f/v1/login/getLoginUserInfo";
                if (!fixture.AccountSessionRenewed && uri.AbsolutePath == profilePath)
                    return Json("{\"code\":401,\"message\":\"未登录\"}", request);
                if (fixture.Platform.Id == "ucampus" && uri.AbsolutePath is "/user/comm/login" or "/sso/login" or "/user/student")
                {
                    fixture.RenewalPaths.Add(uri.IdnHost + uri.AbsolutePath); var renewalResponse = Json("", request, HttpStatusCode.Found);
                    if (uri.AbsolutePath == "/user/comm/login" && !uri.Query.Contains("ticket=", StringComparison.Ordinal))
                        renewalResponse.Headers.Location = new Uri("/t?p=" + Uri.EscapeDataString("https://sso.unipus.cn/sso/login?service=" + Uri.EscapeDataString(MetadataPlatform.Ucampus.Login.AbsoluteUri)), UriKind.Relative);
                    else if (uri.AbsolutePath == "/sso/login") renewalResponse.Headers.Location = new Uri(MetadataPlatform.Ucampus.Login.AbsoluteUri + "&ticket=ST-synthetic-renewal");
                    else if (uri.AbsolutePath == "/user/comm/login")
                    {
                        fixture.AccountSessionRenewed = true; fixture.Session = "synthetic-account-renewed";
                        renewalResponse.Headers.TryAddWithoutValidation("Set-Cookie", fixture.AuthName + "=" + fixture.Session + "; Domain=.unipus.cn; Path=/; Secure; HttpOnly");
                        renewalResponse.Headers.Location = new Uri("https://u.unipus.cn/user/student?school_id=");
                    }
                    else { renewalResponse.StatusCode = HttpStatusCode.OK; renewalResponse.Content = new StringContent(U.Catalog()); }
                    return renewalResponse;
                }
                if (fixture.Platform.Id == "zhihuishu" && (uri.IdnHost == "user.zhihuishu.com" && uri.AbsolutePath == "/zhsuser/login/isLogin"
                    || uri.IdnHost == "passport.zhihuishu.com" && uri.AbsolutePath == "/login"
                    || uri.IdnHost == "cloudapi.polymas.com" && uri.AbsolutePath is "/oauth/login/ssoLogin" or "/oauth/login/checkLogin"
                    || uri.IdnHost == "onlineweb.zhihuishu.com" && uri.AbsolutePath == "/onlinestuh5"))
                {
                    fixture.RenewalPaths.Add(uri.IdnHost + uri.AbsolutePath);
                    if (uri.IdnHost == "user.zhihuishu.com") return Json("{\"status\":200,\"rt\":true}", request);
                    if (uri.IdnHost == "onlineweb.zhihuishu.com") return Json("<!doctype html><title>合成学生首页</title>", request);
                    var renewalResponse = Json("", request, HttpStatusCode.Found);
                    if (uri.IdnHost == "passport.zhihuishu.com")
                    {
                        fixture.Session = "synthetic-account-renewed";
                        renewalResponse.Headers.TryAddWithoutValidation("Set-Cookie", fixture.AuthName + "=" + fixture.Session + "; Domain=.zhihuishu.com; Path=/; Secure; HttpOnly");
                        renewalResponse.Headers.Location = new Uri(ZhihuishuSsoReturn + "&ticket=ST-synthetic-renewal");
                    }
                    else
                    {
                        fixture.AccountSessionRenewed = true; fixture.Session = "synthetic-account-renewed";
                        renewalResponse.Headers.Location = new Uri(ZhihuishuStudentHome);
                    }
                    return renewalResponse;
                }
            }
            var headers = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(';', cookies) : "";
            var auth = headers.Split(';').Select(part => part.Trim().Split('=', 2)).FirstOrDefault(part => part.Length == 2 && part[0] == fixture.AuthName)?[1];
            var courseProbe = fixture.Platform.Id == "ucampus" ? uri.AbsolutePath == "/user/student/homework/list"
                : uri.AbsolutePath.EndsWith("findImportantReminderList", StringComparison.Ordinal);
            if (courseProbe && (fixture.RejectCourseSession || fixture.RejectCourseOnce))
            {
                fixture.RejectCourseOnce = false;
                return Json("{\"code\":401,\"status\":401,\"message\":\"未登录\"}", request);
            }
            var agentAuthorized = uri.IdnHost == "cloudapi.polymas.com" && uri.AbsolutePath == "/student-course/student/index/teacherQueryStudentCourse"
                && request.Headers.TryGetValues("Authorization", out var agentAuth) && agentAuth.Single() == "synthetic-flip-token";
            if (!agentAuthorized && auth != fixture.Session || fixture.RejectLogin)
            {
                var response = Json("{\"code\":403,\"status\":\"401\",\"message\":\"未登录\"}", request);
                response.Headers.TryAddWithoutValidation("Set-Cookie", fixture.AuthName + "=; Path=/; Domain=." + (fixture.Platform.Id == "ucampus" ? "unipus.cn" : "zhihuishu.com") + "; Max-Age=0");
                return response;
            }
            string body;
            if (fixture.Platform.Id == "zhihuishu" && uri.AbsolutePath.EndsWith("/getLoginUserInfo", StringComparison.Ordinal))
            {
                if (fixture.DenyProfile) return Json("{\"code\":403,\"message\":\"Access denied\"}", request, HttpStatusCode.Forbidden);
                if (fixture.MissingStudentToken) return Json("{\"code\":401,\"message\":\"Missing JWT\"}", request, HttpStatusCode.Unauthorized);
            }
            if (fixture.Platform.Id == "ucampus")
            {
                if (uri.IdnHost is not ("u.unipus.cn" or "ucontent.unipus.cn") || request.Method != HttpMethod.Get || request.Content is not null || request.Headers.Contains("Authorization"))
                    throw new InvalidOperationException("Unexpected U campus request.");
                if (fixture.RedirectUcampusLogin && uri.AbsolutePath == "/user/data/getToken")
                {
                    var redirected = Json("", request, HttpStatusCode.Found);
                    redirected.Headers.Location = new Uri("/t?p=https://sso.unipus.cn/sso/login?service="
                        + Uri.EscapeDataString("https://u.unipus.cn/user/data/getToken"), UriKind.Relative);
                    return redirected;
                }
                if (uri.AbsolutePath == "/user/student/index" && fixture.Release is { Task.IsCompleted: false } pending)
                { fixture.Entered!.TrySetResult(true); await pending.Task.WaitAsync(cancellation); }
                body = uri.AbsolutePath switch
                {
                    "/user/data/getToken" => U.Profile(fixture.Account == Data.Account ? U.Account : fixture.Account),
                    "/user/student/index" => fixture.UnknownCatalog ? Data.Ucampus() : U.Catalog(),
                    "/user/student/mycourse/courseCatalog" => U.CatalogPage(),
                    "/course/api/course/synthetic%2Bcontent%3Apc/default/" or "/course/api/course/synthetic+content:pc/default/" => fixture.IncludeLearningUnits ? U.LearningContentInfo() : U.EmptyContentInfo(),
                    "/course/api/course_catalog/synthetic%2Bcontent%3Apc/default/" or "/course/api/course_catalog/synthetic+content:pc/default/" => fixture.UnknownLearningCatalog ? "{}" : fixture.IncludeLearningUnits ? U.LearningCatalog() : U.EmptyContentCatalog(),
                    "/course/api/v2/course_progress/synthetic%2Bcontent%3Apc/" + U.Account + "/default/"
                        or "/course/api/v2/course_progress/synthetic+content:pc/" + U.Account + "/default/" => fixture.IncludeLearningUnits ? U.LearningCourseProgress(Data.Now) : U.EmptyContentProgress(),
                    _ when uri.IdnHost == "ucontent.unipus.cn" && uri.AbsolutePath.Contains("/course_progress/", StringComparison.Ordinal)
                        && fixture.IncludeLearningUnits => U.LearningProgress(Uri.UnescapeDataString(uri.AbsolutePath).Split('/')[6], Data.Now),
                    "/user/student/homework/index" => ParseForm(uri.Query).GetValueOrDefault("type") == "2" ? U.ExamShell() : U.HomeworkShell(),
                    "/user/student/homework/list" => ParseForm(uri.Query).GetValueOrDefault("type") == "2" ? U.DefaultExamList() : U.DefaultList(),
                    "/user/student/homework/chk" => "{\"code\":0,\"rs\":{\"code\":0,\"sign\":\"synthetic-desktop-sign\"}}",
                    "/user/student/homework/examDetail" => JsonSerializer.Serialize(new { code = 0,
                        rs = new { sign = "synthetic-desktop-sign", type = ParseForm(uri.Query).GetValueOrDefault("homeworkId") == "4005" ? 2 : 1 } }),
                    _ => throw new InvalidOperationException("Unexpected legacy metadata endpoint.")
                };
            }
            else if (fixture.Platform.Id == "fif")
            {
                throw new InvalidOperationException("FiF adaptation is disabled.");
            }
            else if (uri.AbsolutePath == "/app-commserv-user/c/has")
            {
                // Exercise the service failure path when metadata handshake is unavailable.
                // Desktop tests inject a key through a separate reader factory below; production always negotiates it.
                return Json("{}", request, HttpStatusCode.ServiceUnavailable);
            }
            else
            {
                if (uri.IdnHost == "hiexam.zhihuishu.com" || uri.Scheme != "https") throw new InvalidOperationException("Unexpected answering request.");
                var jsonBody = request.Content?.Headers.ContentType?.MediaType == "application/json";
                var form = request.Method == HttpMethod.Post && !jsonBody ? ParseForm(await request.Content!.ReadAsStringAsync(cancellation)) : ParseForm(uri.Query);
                MetadataRequest metadata;
                var sharedCourse = Data.SharedCourseModel();
                var flipCourse = new MetadataCourse("101", "合成翻转课", "flip", "合成翻转课");
                if (uri.IdnHost == "kg-run-student.zhihuishu.com")
                {
                    if (request.Method != HttpMethod.Post || !jsonBody || uri.Query.Length != 0 || request.Headers.Contains("Authorization")
                        || request.Headers.Referrer?.AbsoluteUri != "https://ai-smart-course-student-pro.zhihuishu.com/")
                        throw new InvalidOperationException("Unexpected smart task request.");
                    using var envelope = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                    if (envelope.RootElement.EnumerateObject().Count() != 2 || !envelope.RootElement.GetProperty("date").TryGetInt64(out _))
                        throw new InvalidOperationException("Unexpected smart JSON envelope.");
                    using var marker = Decode(request.Headers.GetValues("XQJZXHIZ").Single());
                    if (marker.RootElement.GetString() != "") throw new InvalidOperationException("Unexpected smart map marker.");
                    using var decoded = Decode(envelope.RootElement.GetProperty("secretStr").GetString()!); var parameters = decoded.RootElement;
                    var model = Data.SmartCourseModel(parameters.GetProperty("courseId").GetString()!, "901");
                    var typeRead = uri.AbsolutePath == "/student/gateway/t/home/query-course-task-type";
                    if (typeRead ? parameters.EnumerateObject().Count() != 2 || parameters.GetProperty("classId").GetString() != "901"
                        : uri.AbsolutePath != "/student/gateway/t/task/taskList" || parameters.EnumerateObject().Count() != 4 || parameters.GetProperty("taskName").GetString() != "")
                        throw new InvalidOperationException("Unknown smart task path or parameters.");
                    metadata = new(typeRead ? "smart-task-types" : parameters.GetProperty("taskType").GetInt32() == 1 ? "smart-homework" : "smart-exam",
                        Bucket: typeRead ? 0 : parameters.GetProperty("status").GetInt32()) { Course = model };
                }
                else if (uri.IdnHost == "cloudapi.polymas.com")
                {
                    if (uri.AbsolutePath != "/student-course/student/index/teacherQueryStudentCourse" || request.Method != HttpMethod.Post || !jsonBody
                        || request.Headers.GetValues("Authorization").Single() != "synthetic-flip-token" || request.Headers.Contains("Cookie"))
                        throw new InvalidOperationException("Unexpected AI course directory request.");
                    using var parameters = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                    metadata = new("agent-courses", parameters.RootElement.GetProperty("page").GetInt32());
                }
                else if (uri.AbsolutePath.EndsWith("queryStudentAICourseList", StringComparison.Ordinal))
                {
                    if (uri.IdnHost != "onlineservice-api.zhihuishu.com" || request.Method != HttpMethod.Post || jsonBody || form.Count != 2 || !form.ContainsKey("date"))
                        throw new InvalidOperationException("Unexpected smart course directory request.");
                    using var decoded = Decode(form["secretStr"]);
                    metadata = new("smart-courses", Bucket: decoded.RootElement.GetProperty("status").GetInt32());
                }
                else if (uri.AbsolutePath.EndsWith("queryShareCourseInfo", StringComparison.Ordinal))
                {
                    if (request.Method != HttpMethod.Post || form.Count != 2 || !form.ContainsKey("date")) throw new InvalidOperationException("Unexpected shared catalog request.");
                    using var decoded = Decode(form["secretStr"]); metadata = new("shared-courses", decoded.RootElement.GetProperty("pageNo").GetInt32(), decoded.RootElement.GetProperty("status").GetInt32());
                }
                else if (uri.IdnHost is "studentexam-api.zhihuishu.com" or "taurusexam-api.zhihuishu.com")
                {
                    if (request.Method != HttpMethod.Post || form.Count != 1 || !form.ContainsKey("secretStr") || uri.Query.Length != 0 || request.Headers.Contains("Authorization"))
                        throw new InvalidOperationException("Unexpected shared task metadata request.");
                    using var decoded = Decode(form["secretStr"]); var parameters = decoded.RootElement;
                    var operation = uri.AbsolutePath.EndsWith("getLoginUserInfo", StringComparison.Ordinal) ? uri.IdnHost.StartsWith("taurus", StringComparison.Ordinal) ? "shared-exam-profile" : "shared-profile"
                        : uri.AbsolutePath.EndsWith("getLoginSchoolInfo", StringComparison.Ordinal) ? "shared-school"
                        : uri.AbsolutePath.EndsWith("getStudentHomework", StringComparison.Ordinal) ? "shared-homework"
                        : uri.AbsolutePath.EndsWith("getExamType", StringComparison.Ordinal) ? "shared-exam-type"
                        : uri.AbsolutePath.EndsWith("getStudentFinalExam", StringComparison.Ordinal) ? "shared-exams"
                        : uri.AbsolutePath.EndsWith("getStudentRoomExam", StringComparison.Ordinal) ? "shared-room-exam"
                        : uri.AbsolutePath.EndsWith("getStudentOfflineExam", StringComparison.Ordinal) ? "shared-offline-exam"
                        : uri.AbsolutePath.EndsWith("hasMakeupExam", StringComparison.Ordinal) ? "shared-makeup" : throw new InvalidOperationException("Unknown shared path.");
                    var taurus = operation is "shared-exam-profile" or "shared-exam-type" or "shared-exams" or "shared-room-exam" or "shared-offline-exam" or "shared-makeup";
                    var prefix = taurus ? "/taurusExam/gateway/" : "/studentExam/gateway/";
                    if (uri.IdnHost != (taurus ? "taurusexam-api.zhihuishu.com" : "studentexam-api.zhihuishu.com") || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("Shared task metadata origin mismatch.");
                    if (operation is "shared-homework" or "shared-exams" or "shared-exam-type" or "shared-room-exam" or "shared-offline-exam" or "shared-makeup")
                        sharedCourse = Data.SharedCourseModel(parameters.TryGetProperty("courseId", out var courseId) ? courseId.GetString()! : "101", parameters.GetProperty("recruitId").GetString()!);
                    var parameterCount = parameters.EnumerateObject().Count();
                    if (operation is "shared-exams" or "shared-room-exam" or "shared-offline-exam"
                        && (parameterCount != 3 || parameters.GetProperty("flag").GetInt32() is not (1 or 2))) throw new InvalidOperationException("Unexpected exam list parameters.");
                    if (operation == "shared-makeup" && parameterCount != 1) throw new InvalidOperationException("Unexpected makeup discriminator parameters.");
                    if (operation == "shared-homework" && (parameters.GetProperty("pageSize").GetInt32() != 100 || parameters.GetProperty("pageNum").GetInt32() != 0))
                        throw new InvalidOperationException("Unverified shared pagination.");
                    metadata = new(operation, Bucket: parameters.TryGetProperty("flag", out var flag) ? flag.GetInt32() : 0)
                        { Course = sharedCourse, SchoolId = Data.SchoolId };
                }
                else
                {
                    if (request.Method != HttpMethod.Get || request.Content is not null) throw new InvalidOperationException("Unexpected metadata GET.");
                    var operation = uri.AbsolutePath.EndsWith("getLoginUserInfo", StringComparison.Ordinal) ? "profile"
                        : uri.AbsolutePath.EndsWith("getCourseList", StringComparison.Ordinal) ? "courses"
                        : uri.AbsolutePath.EndsWith("getStudentNoCommitExam", StringComparison.Ordinal) ? "shared-reminders"
                        : uri.AbsolutePath.EndsWith("findImportantReminderList", StringComparison.Ordinal) ? "reminders"
                        : uri.AbsolutePath == "/zhsathome/homeworkUser/List" ? "flip-homework"
                        : uri.AbsolutePath == "/zhsathome/examStu/List" ? "flip-exam" : throw new InvalidOperationException("Unknown metadata path.");
                    if (operation is "flip-homework" or "flip-exam" && (!request.Headers.TryGetValues("Authorization", out var authorization) || authorization.Single() != "synthetic-flip-token"))
                        throw new InvalidOperationException("Flip cookie authorization missing.");
                    metadata = new(operation, form.TryGetValue("pageIndex", out var page) ? int.Parse(page, System.Globalization.CultureInfo.InvariantCulture) : 1)
                        { Course = operation is "flip-homework" or "flip-exam" ? flipCourse : null };
                }
                var responses = new Data.Reader { AccountId = fixture.Account, IncludeCourseLists = true, IncludeSchoolExams = true };
                body = fixture.UnknownProfile && metadata.Operation == "profile" ? "{\"code\":200,\"result\":null}"
                    : fixture.UnknownCatalog && metadata.Operation == "courses" || fixture.UnknownReminders && metadata.Operation == "reminders"
                    ? "{\"status\":200,\"rt\":{}}" : fixture.IncludeSmartCourses && metadata.Operation == "smart-courses"
                    ? Data.SmartCatalog(metadata.Bucket == 3 ? [Data.SmartCourse()] : [])
                    : fixture.IncludeSmartCourses && metadata.Operation == "smart-task-types" ? Data.SmartTypes(Data.SmartType(1, 5, 1), Data.SmartType(2, 1, 1))
                    : fixture.IncludeSmartCourses && metadata.Operation == "smart-homework" ? Data.SmartList(metadata.Bucket == 0
                        ? [Data.SmartTask()] : Enumerable.Range(1, 4).Select(index => Data.SmartTask("smart-done-" + index, examStatus: 6, status: 1)).ToArray())
                    : fixture.IncludeSmartCourses && metadata.Operation == "smart-exam" ? Data.SmartList(metadata.Bucket == 0 ? [Data.SmartTask("smart-exam", type: 2, examStatus: 1, status: 1)] : [])
                    : responses.DefaultResponse(metadata).Json;
                if (uri.AbsolutePath.EndsWith("findImportantReminderList", StringComparison.Ordinal) && fixture.Release is { Task.IsCompleted: false } pending)
                { fixture.Entered!.TrySetResult(true); await pending.Task; }
            }
            var result = Json(body, request);
            if (fixture.RenewNext || fixture.LateRenewal && uri.AbsolutePath.EndsWith("findImportantReminderList", StringComparison.Ordinal))
            {
                fixture.RenewNext = false; fixture.Session = "synthetic-session-renewed";
                result.Headers.TryAddWithoutValidation("Set-Cookie", fixture.AuthName + "=" + fixture.Session + "; Path=/; Domain=." + (fixture.Platform.Id == "ucampus" ? "unipus.cn" : "zhihuishu.com") + "; Secure; HttpOnly; SameSite=Strict");
            }
            return result;
        }
        private static HttpResponseMessage Json(string body, HttpRequestMessage request, HttpStatusCode code = HttpStatusCode.OK)
            => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
        private static Dictionary<string, string> ParseForm(string text) => text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
        private static JsonDocument Decode(string secret)
        {
            using var aes = System.Security.Cryptography.Aes.Create(); aes.Key = Data.Key;
            return JsonDocument.Parse(aes.DecryptCbc(Convert.FromBase64String(secret), Encoding.UTF8.GetBytes("1g3qqdh4jvbskb9x")));
        }
    }
}
