using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SyncDiagnosticHarness;

// Independent read-only diagnosis; never controls the running application or writes its task/session archive.
internal static class MoocDiagnosis
{
    public static int Run(string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var reports = Path.Combine(root, ".local", "sync-diagnostics"); Directory.CreateDirectory(reports);
        var report = Path.Combine(reports, "mooc-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        File.WriteAllText(Path.Combine(reports, "latest-mooc-report.txt"), report);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var browser = new WebView2 { Width = 1, Height = 1, Visibility = Visibility.Hidden };
        var host = new Window { Content = browser, Width = 1, Height = 1, Left = -10000, Top = -10000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var stages = new List<object>(); var navigation = new List<object>(); var exitCode = 1;
        var stage = "initialization";
        host.Loaded += async (_, _) =>
        {
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2)); var token = cancellation.Token;
                var environment = await CoreWebView2Environment.CreateAsync(null, MoocSyncService.ProfilePath(root));
                await browser.EnsureCoreWebView2Async(environment);
                var core = browser.CoreWebView2;
                core.PermissionRequested += (_, request) => request.State = CoreWebView2PermissionState.Deny;
                core.ServerCertificateErrorDetected += (_, error) => error.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                core.DownloadStarting += (_, request) => request.Cancel = true;
                core.NewWindowRequested += (_, request) => request.Handled = true;
                core.NavigationStarting += (_, request) =>
                {
                    if (Uri.TryCreate(request.Uri, UriKind.Absolute, out var target) && request.Uri != "about:blank")
                        navigation.Add(new { phase = "starting", host = MoocPlatform.IsCookieDomain(target.IdnHost) ? target.IdnHost : "outside_platform",
                            path = target.AbsolutePath.Split('/').Select(segment => segment.Any(char.IsDigit) ? "[parameter]" : segment).Aggregate((left, right) => left + "/" + right),
                            parameterNames = target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                                .Select(pair => pair.Split('=', 2)[0]).Where(name => name.Length <= 64 && name.All(character => char.IsAsciiLetter(character) || character is '_' or '-')).Distinct().ToArray() });
                    if (request.Uri != "about:blank" && (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var address)
                        || address.Scheme != "https" || address.IdnHost != MoocPlatform.Host || address.AbsolutePath != "/home.htm"))
                        request.Cancel = true;
                };
                async Task<MoocSessionCookie[]> Cookies(CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    var observed = await core.CookieManager.GetCookiesAsync("");
                    try
                    {
                        return observed.Select(cookie => new MoocSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
                            cookie.IsSecure, cookie.IsHttpOnly, cookie.IsSession ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()),
                            cookie.SameSite.ToString())).Where(cookie => MoocSessionStore.IsValid(cookie) && !cookie.IsExpired(DateTimeOffset.UtcNow))
                            .DistinctBy(cookie => cookie.Key).ToArray();
                    }
                    finally { observed.Clear(); }
                }
                stage = "session_snapshot";
                var before = await Cookies(token);
                stages.Add(new { stage, cookies = before.Length, csrfCookiePresent = before.Any(cookie => cookie.Name == "NTESSTUDYSI"),
                    loginCookieNames = before.Where(cookie => cookie.Name is "STUDY_INFO" or "STUDY_SESS" or "NTES_SESS")
                        .Select(cookie => cookie.Name).Distinct().ToArray() });
                stage = "catalog_http";
                using var reader = new MoocHttpReader(async (_, ct) => await Cookies(ct));
                var catalogResponse = await reader.ReadAsync(MoocPlatform.CourseRpc,
                    new Dictionary<string, string> { ["type"] = "30", ["p"] = "1", ["psize"] = "8", ["courseType"] = "1" }, token);
                var catalog = catalogResponse.Outcome == "ok" ? MoocParser.Catalog(catalogResponse.Json) : null;
                string? resultShape = null; string[]? resultFields = null;
                if (catalogResponse.Outcome == "ok")
                {
                    using var responseDocument = JsonDocument.Parse(catalogResponse.Json);
                    var result = MoocParser.Get(responseDocument.RootElement, "result"); resultShape = result.ValueKind.ToString();
                    resultFields = result.ValueKind == JsonValueKind.Object ? result.EnumerateObject().Select(field => field.Name).Order().ToArray() : [];
                }
                stages.Add(new { stage, catalogResponse.Outcome, catalogResponse.StatusCode, parsed = catalog?.Outcome,
                    coursesOnPage = catalog?.Courses.Count, catalog?.TotalCount, catalog?.TotalPages, issues = catalog?.Issues, resultShape, resultFields });
                stage = "home_http";
                using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli })
                    { Timeout = TimeSpan.FromSeconds(25) })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, MoocPlatform.Home);
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 LearningTaskAggregator/1.2");
                    request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", before.Where(cookie => cookie.AppliesTo(MoocPlatform.Home))
                        .OrderByDescending(cookie => cookie.Path.Length).Select(cookie => cookie.Name + "=" + cookie.Value)));
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    var content = await response.Content.ReadAsStringAsync(token);
                    var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(content);
                    stages.Add(new { stage, statusCode = (int)response.StatusCode, bytes = Encoding.UTF8.GetByteCount(content),
                        loginRedirect = response.Headers.Location is { } location && Uri.TryCreate(MoocPlatform.Home, location, out var target)
                            && MoocPlatform.IsLoginNavigation(target.AbsoluteUri),
                        scripts = document.QuerySelectorAll("script").Length,
                        webUserScripts = document.QuerySelectorAll("script").Where(element => !element.HasAttribute("src") && element.TextContent.Contains("webUser", StringComparison.Ordinal))
                            .Select(element => new { length = element.TextContent.Length,
                                hasAssignment = System.Text.RegularExpressions.Regex.IsMatch(element.TextContent, @"(?:webUser\s*=|[""']webUser[""']\s*[:\]])"),
                                objectFields = System.Text.RegularExpressions.Regex.Matches(element.TextContent, @"[""']?([A-Za-z_][A-Za-z0-9_]*)[""']?\s*:")
                                    .Select(match => match.Groups[1].Value).Distinct().Take(40).ToArray() }).ToArray() });
                }
                stage = "account_navigation";
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, args) =>
                {
                    var valid = Uri.TryCreate(core.Source, UriKind.Absolute, out var address)
                        && address.IdnHost == MoocPlatform.Host && address.AbsolutePath == "/home.htm";
                    var userIdNavigation = valid && address!.Query.Contains("userId=", StringComparison.Ordinal);
                    navigation.Add(new { success = args.IsSuccess, status = args.WebErrorStatus.ToString(), validHome = valid, userIdNavigation });
                    if (args.IsSuccess && valid) ready.TrySetResult(true);
                };
                core.Navigate(MoocPlatform.Home.AbsoluteUri);
                core.DOMContentLoaded += (_, args) => navigation.Add(new { phase = "dom_ready", args.NavigationId });
                for (var sample = 0; sample < 5 && !ready.Task.IsCompleted; sample++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    var snapshot = await core.ExecuteScriptAsync("(() => ({ readyState: document.readyState, hasWebUser: !!window.webUser, elements: document.getElementsByTagName('*').length, scripts: document.scripts.length, resources: performance.getEntriesByType('resource').length }))()");
                    using var observed = JsonDocument.Parse(snapshot);
                    navigation.Add(new { phase = "document_sample", sample, state = observed.RootElement.Clone() });
                }
                try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), token); }
                catch (TimeoutException) { stages.Add(new { stage, outcome = "navigation_timeout" }); }
                var script = await core.ExecuteScriptAsync("(() => { const user = window.webUser; return !user ? { loggedIn: false } : { loggedIn: true, id: String(user.id || '') }; })()");
                using var identity = JsonDocument.Parse(script);
                var account = MoocParser.Text(MoocParser.Get(identity.RootElement, "id"));
                var after = await Cookies(token);
                stages.Add(new { stage, loggedIn = MoocParser.Get(identity.RootElement, "loggedIn").ValueKind == JsonValueKind.True,
                    accountRecognized = MoocPlatform.IsId(account), cookieValuesChanged = Fingerprint(before) != Fingerprint(after),
                    cookieCountBefore = before.Length, cookieCountAfter = after.Length,
                    changedCookieNames = after.Where(cookie => cookie.Value != before.FirstOrDefault(prior => prior.Key == cookie.Key)?.Value)
                        .Select(cookie => cookie.Name).Distinct().ToArray() });
                var cookieFailures = new List<object>();
                foreach (var cookie in after.Where(cookie => cookie.AppliesTo(MoocPlatform.Home)))
                {
                    try
                    {
                        var container = new CookieContainer(512, 512, 32768); var value = cookie.ToCookie();
                        if (!cookie.Domain.StartsWith('.')) value = new Cookie(cookie.Name, cookie.Value, cookie.Path)
                        { Secure = cookie.Secure, HttpOnly = cookie.HttpOnly, Expires = value.Expires };
                        container.Add(new Uri("https://" + cookie.Domain.TrimStart('.') + "/"), value);
                        _ = container.GetCookieHeader(MoocPlatform.Home);
                    }
                    catch (CookieException) { cookieFailures.Add(new { cookie.Name, domain = cookie.Domain, pathLength = cookie.Path.Length,
                        valueLength = cookie.Value.Length, hasSeparator = cookie.Value.Contains(';') || cookie.Value.Contains(','),
                        quoted = cookie.Value.StartsWith('"') && cookie.Value.EndsWith('"') }); }
                }
                stages.Add(new { stage = "cookie_transport", rejectedCookies = cookieFailures });
                var courses = catalog?.Courses.ToList() ?? [];
                if (catalog?.TotalPages is >= 2 and <= 10 && MoocPlatform.IsId(account))
                    for (var page = 2; page <= catalog.TotalPages; page++)
                    {
                        stage = "catalog_http";
                        var response = await reader.ReadAsync(MoocPlatform.CourseRpc, new Dictionary<string, string>
                        { ["type"] = "30", ["p"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture), ["psize"] = "8", ["courseType"] = "1" }, token);
                        var parsed = response.Outcome == "ok" ? MoocParser.Catalog(response.Json) : null;
                        stages.Add(new { stage, page, response.Outcome, response.StatusCode, parsed = parsed?.Outcome,
                            coursesOnPage = parsed?.Courses.Count, parsed?.TotalCount, parsed?.TotalPages, issues = parsed?.Issues });
                        if (parsed is null || parsed.Outcome != "ok") break;
                        courses.AddRange(parsed.Courses);
                    }
                if (catalog is not null && MoocPlatform.IsId(account))
                    foreach (var course in courses.Take(80))
                    {
                        stage = "term_http";
                        var termResponse = await reader.ReadAsync(MoocPlatform.LearnedTermRpc,
                            new Dictionary<string, string> { ["termId"] = course.TermId }, token);
                        var term = termResponse.Outcome == "ok" ? MoocParser.Term(termResponse.Json, account, course) : null;
                        stages.Add(new { stage, termResponse.Outcome, termResponse.StatusCode, parsed = term?.Outcome,
                            tasks = term?.Tasks.Count, issues = term?.Issues,
                            incompleteTaskShapes = term is { Outcome: "partial_tasks" } ? DescribeIncomplete(termResponse.Json, term) : null });
                        stage = "new_exam_http";
                        var examResponse = await reader.ReadAsync(MoocPlatform.NewExamRpc, new Dictionary<string, string> { ["termId"] = course.TermId }, token);
                        var exams = examResponse.Outcome == "ok" ? MoocParser.NewExams(examResponse.Json, account, course) : null;
                        var merged = term is not null && exams is not null ? MoocParser.MergeTaskBatches(term, exams) : null;
                        stages.Add(new { stage, examResponse.Outcome, examResponse.StatusCode, parsed = exams?.Outcome,
                            newExams = exams?.Tasks.Count, issues = exams?.Issues, mergedTasks = merged?.Tasks.Count,
                            states = merged?.Tasks.GroupBy(task => task.State?.ToString() ?? "needs_review")
                                .ToDictionary(group => group.Key, group => group.Count()),
                            deadlines = merged?.Tasks.Count(task => task.DeadlineUtc is not null),
                            links = merged?.Tasks.Count(task => task.Link is not null) });
                    }
                exitCode = 0; Write("diagnosed", null);
            }
            catch (Exception error) { Write("failed", new { stage, type = error.GetType().Name, hresult = error.HResult }); }
            finally { browser.Dispose(); app.Shutdown(exitCode); }
        };
        app.Run(host); return exitCode;

        void Write(string outcome, object? failure) => File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            schema = 1, generatedAtUtc = DateTimeOffset.UtcNow, outcome, failure, stages, navigation,
            taskDatabaseMode = "untouched", sessionArchiveMode = "untouched", answeringPagesRequested = 0,
            externalBrowserLaunched = false, responseBodiesRetained = false,
            privacy = "no_account_ids_no_titles_no_course_names_no_links_no_cookies"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object[] DescribeIncomplete(string json, MoocTaskBatch parsed)
    {
        using var document = JsonDocument.Parse(json);
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) { foreach (var item in node.EnumerateArray()) Visit(item); return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            var id = MoocParser.Text(MoocParser.Get(node, "id"));
            if (MoocPlatform.IsId(id)) metadata[id] = node;
            foreach (var field in node.EnumerateObject())
                if (field.Name is "result" or "mocTermDto" or "chapters" or "homeworks" or "quizs" or "exams" or "exam" or "lessons" or "test"
                    or "objectTest" or "objectTestVo" or "subjectTest" or "subjectTestVo") Visit(field.Value);
        }
        Visit(document.RootElement);
        return parsed.Tasks.Where(task => task.Link is null || task.DeadlineUtc is null).Select(task =>
        {
            var node = metadata.GetValueOrDefault(task.RemoteId);
            var parent = metadata.GetValueOrDefault(task.ExamId);
            var deadline = MoocParser.Get(node, "deadline");
            if (deadline.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) deadline = MoocParser.Get(parent, "deadline");
            return (object)new { task.Kind, hasLink = task.Link is not null, hasDeadline = task.DeadlineUtc is not null,
                fields = node.ValueKind == JsonValueKind.Object ? node.EnumerateObject().Select(field => field.Name).Order().ToArray() : [],
                parentFields = parent.ValueKind == JsonValueKind.Object ? parent.EnumerateObject().Select(field => field.Name).Order().ToArray() : [],
                testType = Numeric(MoocParser.Get(node, "type")), parentType = Numeric(MoocParser.Get(parent, "type")),
                parentExamType = Numeric(MoocParser.Get(parent, "examType")),
                parentContentType = Numeric(MoocParser.Get(parent, "contentType")),
                parentTargetType = Numeric(MoocParser.Get(parent, "targetType")),
                deadlineCategory = deadline.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "absent"
                    : Numeric(deadline) is < 0 ? "negative" : Numeric(deadline) == 0 ? "zero" : "unrecognized_positive" };
        }).ToArray();
    }

    private static decimal? Numeric(JsonElement value) => decimal.TryParse(MoocParser.Text(value),
        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string Fingerprint(IEnumerable<MoocSessionCookie> cookies)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cookies.OrderBy(cookie => cookie.Key, StringComparer.Ordinal)
            .Select(cookie => new[] { cookie.Key, cookie.Value }));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
