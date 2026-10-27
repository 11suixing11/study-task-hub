using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SyncDiagnosticHarness;

// Explicit development diagnosis: reads the project's session and official course metadata.
// Does not navigate, log in, export cookies, store identifiers or change the task database.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--project-root" && args[2] == "--rain-metadata")
            return RainDiagnosis.Run(args[1]);
        if (args.Length == 3 && args[0] == "--project-root" && args[2] == "--mooc-metadata")
            return MoocDiagnosis.Run(args[1]);
        if (args.Length == 3 && args[0] == "--project-root" && args[2] == "--mooc-login-assets")
            return MoocLoginDiagnosis.Run(args[1]);
        if (args.Length is not (2 or 3) || args[0] != "--project-root"
            || args.Length == 3 && args[2] is not ("--empty-shape" or "--task-route" or "--detail-shapes" or "--time-metadata" or "--task-navigation")) return 2;
        var inspectEmptyShape = args.Length == 3 && args[2] == "--empty-shape";
        var inspectTaskRoute = args.Length == 3 && args[2] == "--task-route";
        var inspectDetailShapes = args.Length == 3 && args[2] == "--detail-shapes";
        var inspectTimeMetadata = args.Length == 3 && args[2] == "--time-metadata";
        var inspectTaskNavigation = args.Length == 3 && args[2] == "--task-navigation";
        var root = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var reportRoot = Path.Combine(root, ".local", "sync-diagnostics");
        Directory.CreateDirectory(reportRoot);
        var reportPath = Path.Combine(reportRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(Path.Combine(reportRoot, "latest-report.txt"), reportPath);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var browser = new WebView2 { Width = 1, Height = 1, Visibility = Visibility.Hidden };
        var host = new Window { Content = browser, Width = 1, Height = 1, Left = -10000, Top = -10000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var exitCode = 1;
        host.Loaded += async (_, _) =>
        {
            var observations = new List<ChaoxingRedirectObservation>();
            var outcomes = new List<object>();
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(inspectDetailShapes || inspectTimeMetadata ? 240 : 90));
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, ".local", "webview-profile"));
                await browser.EnsureCoreWebView2Async(environment);
                browser.CoreWebView2.PermissionRequested += (_, request) => request.State = CoreWebView2PermissionState.Deny;
                browser.CoreWebView2.ServerCertificateErrorDetected += (_, error) => error.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                async Task<IReadOnlyList<Cookie>> Cookies(Uri address, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync(address.AbsoluteUri);
                    return cookies.Select(cookie => new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                        { Secure = cookie.IsSecure, HttpOnly = cookie.IsHttpOnly }).ToArray();
                }
                if (inspectTaskNavigation)
                {
                    var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(root, ".local", "data", "tasks.db"),
                        Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false
                    }.ToString();
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT payload,protected_link FROM tasks WHERE protected_link IS NOT NULL;";
                    var candidates = new List<(TaskItem Task, byte[] Link)>();
                    using (var saved = command.ExecuteReader())
                        while (saved.Read())
                            if (JsonSerializer.Deserialize<TaskItem>(saved.GetString(0)) is { } task
                                && task.Source == TaskSource.Chaoxing && !task.Overrides.HasFlag(TaskOverrides.Link))
                                candidates.Add((task, (byte[])saved.GetValue(1)));
                    var groups = candidates.GroupBy(value => value.Task.PlatformStatusText == "待批阅" ? "awaiting_review"
                        : ChaoxingTaskMetadata.ReadState(value.Task.PlatformStatusText) == TaskState.Pending ? "pending"
                        : ChaoxingTaskMetadata.ReadState(value.Task.PlatformStatusText) == TaskState.Completed ? "completed" : "other");
                    var protector = new WindowsLinkProtector();
                    foreach (var group in groups.Where(group => group.Key != "other"))
                    {
                        var representative = group.First();
                        var link = protector.Unprotect(representative.Link);
                        var requests = 0;
                        async Task<IReadOnlyList<Cookie>> CountedCookies(Uri address, CancellationToken token)
                        { requests++; return await Cookies(address, token); }
                        using var taskReader = new ChaoxingHttpReader(CountedCookies);
                        var resolution = await taskReader.ResolveTaskAddressAsync(new(link), cancellation.Token);
                        outcomes.Add(new { stage = "task_navigation", selection = group.Key, availableTasks = group.Count(),
                            resolution.Outcome, resolution.Destination, requests,
                            taskIdentityMatched = resolution.Address is not null && TaskNavigationRules.Destination(new(link), new(resolution.Address)) is not null });
                    }
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(new { outcome = "diagnosed", outcomes,
                        scope = "saved_task_entry_response_headers_only", bodiesRead = 0, answeringPagesRequested = 0,
                        externalBrowserLaunched = false, databaseMode = "read_only", privacy = "no_titles_no_ids_no_links_no_cookies" },
                        new JsonSerializerOptions { WriteIndented = true }));
                    exitCode = 0;
                    return;
                }
                using var reader = new ChaoxingHttpReader(Cookies, observeRedirect: observations.Add);
                var portal = await reader.ReadAsync(new(ChaoxingSyncParser.Portal), cancellation.Token);
                outcomes.Add(new { stage = "portal", portal.Outcome, portal.StatusCode });
                if (portal.Outcome == "ok" && ChaoxingSyncParser.ReadCatalogAddress(portal.Html) is { } catalogAddress)
                {
                    var page = await reader.ReadAsync(catalogAddress, cancellation.Token);
                    var catalog = ChaoxingSyncParser.ParseCatalog(page.Html, page.Address);
                    outcomes.Add(new { stage = "catalog", page.Outcome, page.StatusCode, courses = catalog.Courses.Count });
                    var courseIndex = 0;
                    var detailIndex = 0;
                    foreach (var course in catalog.Courses.Take(inspectEmptyShape ? 1 : inspectTaskRoute ? 4 : 20))
                    {
                        courseIndex++;
                        await Task.Delay(250, cancellation.Token);
                        var entry = await reader.ReadAsync(course.PageUri, cancellation.Token);
                        var workAddress = ChaoxingSyncParser.ReadWorkListAddress(entry.Html, course);
                        outcomes.Add(new { stage = "course_entry", courseIndex, entry.Outcome, entry.StatusCode, workEntryFound = workAddress is not null });
                        if (entry.Outcome == "ok" && workAddress is null)
                            outcomes.Add(new { stage = "course_entry_shape", courseIndex, shape = DescribeDocument(entry.Html, entry.Address) });
                        if (entry.Outcome == "ok" && workAddress is not null)
                        {
                            var list = await reader.ReadAsync(workAddress, cancellation.Token);
                            var tasks = WorkListParser.Parse(list.Html);
                            var shape = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(list.Html);
                            outcomes.Add(new { stage = "work_list", courseIndex, list.Outcome, list.StatusCode,
                                parsed = tasks.Outcome, taskCount = tasks.Tasks.Count, tasks.InvalidLinks,
                                explicitEmpty = ChaoxingSyncParser.IsExplicitEmptyWorkList(list.Html),
                                rowCount = shape.QuerySelectorAll("li[data][onclick]").Length,
                                workContainers = shape.QuerySelectorAll("#workList, .work-list, .workList, .null").Length });
                            if (inspectTimeMetadata)
                                outcomes.Add(new { stage = "list_time_metadata", courseIndex, metadata = DescribeTimeMetadata(list.Html) });
                            foreach (var row in tasks.Tasks.Where(task => task.Location is not null)
                                .Take(inspectTaskRoute ? 1 : inspectDetailShapes || inspectTimeMetadata ? Math.Max(0, 30 - detailIndex) : 0))
                            {
                                detailIndex++;
                                var location = row.Location!;
                                await Task.Delay(250, cancellation.Token);
                                var detailPage = await reader.ReadAsync(location.PageUri, cancellation.Token);
                                var detail = WorkDetailParser.Parse(detailPage.Html);
                                var detailShape = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(detailPage.Html);
                                var metadataLabels = new[] { "作答时间", "截止时间", "开始时间", "结束时间", "作业未开始", "作业已截止", "暂无作业" };
                                outcomes.Add(new { stage = "task_detail", courseIndex, detailIndex, detailPage.Outcome, detailPage.StatusCode,
                                    parsed = detail.Outcome, hasTimeWindow = detail.AnswerWindow is not null,
                                    detailHeadCount = detailShape.QuerySelectorAll(".detailsHead").Length,
                                    timeRowCount = detailShape.QuerySelectorAll("p.rightBord").Length,
                                    metadataLabels = metadataLabels.Where(label => (detailShape.Body?.TextContent ?? "").Contains(label, StringComparison.Ordinal)).ToArray(),
                                    labelPaths = detailShape.QuerySelectorAll("body *").Where(element => element.Children.Length == 0
                                            && metadataLabels.Any(label => element.TextContent.Trim().StartsWith(label, StringComparison.Ordinal)))
                                        .Take(8).Select(element =>
                                        {
                                            var path = new List<string>();
                                            for (var current = element; current is not null && path.Count < 4; current = current.ParentElement)
                                            {
                                                var classes = string.Join(".", current.ClassList.Where(value => value.Length <= 64
                                                    && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')));
                                                path.Add(current.TagName + (classes.Length > 0 ? "." + classes : ""));
                                            }
                                            return path;
                                        }).ToArray(),
                                    shape = DescribeDocument(detailPage.Html, detailPage.Address) });
                                if (inspectTimeMetadata)
                                    outcomes.Add(new { stage = "detail_time_metadata", courseIndex, detailIndex, metadata = DescribeTimeMetadata(detailPage.Html) });
                            }
                            if (inspectTaskRoute && detailIndex > 0) break;
                            if (inspectEmptyShape)
                            {
                                var phrases = new[] { "暂无作业", "还没有作业", "没有作业", "未发布作业", "暂无数据", "未查到相关数据", "暂无相关数据", "还没有发布", "没有此页面访问权限" };
                                var text = shape.Body?.TextContent ?? "";
                                outcomes.Add(new { stage = "empty_shape", pageBytes = System.Text.Encoding.UTF8.GetByteCount(list.Html),
                                    knownLabels = phrases.Where(label => text.Contains(label, StringComparison.Ordinal)).ToArray(),
                                    labelPaths = shape.QuerySelectorAll("body *").Where(element => element.Children.Length == 0
                                            && phrases.Contains(element.TextContent.Trim(), StringComparer.Ordinal))
                                        .Take(6).Select(element =>
                                        {
                                            var path = new List<string>();
                                            for (var current = element; current is not null && path.Count < 4; current = current.ParentElement)
                                            {
                                                var classes = string.Join(".", current.ClassList.Where(value => value.Length <= 64
                                                    && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')));
                                                path.Add(current.TagName + (classes.Length > 0 ? "." + classes : ""));
                                            }
                                            return path;
                                        }).ToArray(),
                                    containerClasses = shape.QuerySelectorAll("body > div, body > ul, body > main, .null, .no-data, .work-list, .workList, .listTit, .empty, .no-work, .noWork")
                                        .Select(element => element.GetAttribute("class") ?? "").Where(value => value.Length <= 100
                                            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '_' or '-'))
                                        .Distinct().Take(20).ToArray() });
                            }
                        }
                    }
                }
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { outcome = "diagnosed", outcomes, redirects = observations }, new JsonSerializerOptions { WriteIndented = true }));
                exitCode = 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { outcome = "failed", error = error.GetType().Name, outcomes, redirects = observations }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { browser.Dispose(); app.Shutdown(exitCode); }
        };
        app.Run(host);
        return exitCode;
    }

    private static object DescribeTimeMetadata(string html)
    {
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var temporalName = new System.Text.RegularExpressions.Regex("(?:start|end|begin|deadline|publish|submit|create|server|current).*?(?:time|date)|^(?:time|date)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var fullDate = new System.Text.RegularExpressions.Regex(@"\b20[0-9]{2}[-/][0-9]{1,2}[-/][0-9]{1,2}[ T][0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?\b");
        var scriptField = new System.Text.RegularExpressions.Regex(@"\b(?<name>[A-Za-z_][A-Za-z0-9_]{0,63})\s*(?:=|:)\s*['"" ]?(?<value>(?:20[0-9]{2}[-/][0-9]{1,2}[-/][0-9]{1,2}[ T][0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?)|[0-9]{10,13})");
        var fields = new List<object>();
        foreach (var input in document.QuerySelectorAll("input[id], input[name]"))
        {
            var name = input.GetAttribute("name") ?? input.GetAttribute("id") ?? "";
            if (!temporalName.IsMatch(name)) continue;
            var value = input.GetAttribute("value") ?? "";
            fields.Add(new { source = "input", name, fullDate = fullDate.IsMatch(value), epochLength = value.All(char.IsAsciiDigit) ? value.Length : 0 });
        }
        foreach (var script in document.QuerySelectorAll("script:not([src])"))
            foreach (System.Text.RegularExpressions.Match match in scriptField.Matches(script.TextContent))
            {
                var name = match.Groups["name"].Value;
                if (!temporalName.IsMatch(name)) continue;
                var value = match.Groups["value"].Value;
                fields.Add(new { source = "script_literal", name, fullDate = fullDate.IsMatch(value), epochLength = value.All(char.IsAsciiDigit) ? value.Length : 0 });
            }
        var attributes = document.QuerySelectorAll("li[data][onclick] .time, .detailsHead .infoHead p.rightBord")
            .SelectMany(element => element.Attributes.Where(attribute => attribute.Name != "class")
                .Select(attribute => new { tag = element.TagName, name = attribute.Name, hasFullDate = fullDate.IsMatch(attribute.Value) })).Take(50).ToArray();
        return new { fields, attributes, headerFullDates = document.QuerySelectorAll(".detailsHead .infoHead p.rightBord").Count(row => fullDate.IsMatch(row.TextContent)),
            listFullDates = document.QuerySelectorAll("li[data][onclick] .time").Count(row => fullDate.IsMatch(row.TextContent)) };
    }

    private static object DescribeDocument(string html, Uri address)
    {
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        foreach (var script in document.QuerySelectorAll("script, style")) script.Remove();
        var labels = new[] { "已完成", "已提交", "已结束", "已截止", "未开始", "未批阅", "已批阅", "不可查看", "不允许查看", "答案", "成绩", "查看", "作答", "提交", "权限", "验证码", "登录", "重修", "已结课", "课程已结束", "教师", "老师", "作业", "禁止", "不支持", "成功", "未到" };
        var text = document.Body?.TextContent ?? "";
        return new
        {
            pageBytes = System.Text.Encoding.UTF8.GetByteCount(html),
            textLength = text.Trim().Length,
            knownLabels = labels.Where(label => text.Contains(label, StringComparison.Ordinal)).ToArray(),
            promptLabels = labels.Where(label => (document.QuerySelector("p.dpy")?.TextContent ?? "").Contains(label, StringComparison.Ordinal)).ToArray(),
            promptTimeLabels = new[] { "作答时间", "提交时间", "截止时间", "批阅时间" }.Where(label =>
                (document.QuerySelector("p.subTime")?.TextContent ?? "").Contains(label, StringComparison.Ordinal)).ToArray(),
            promptPaths = document.QuerySelectorAll("p.dpy, p.subTime").Take(4).Select(element =>
            {
                var path = new List<string>();
                for (var current = element; current is not null && path.Count < 4; current = current.ParentElement)
                    path.Add(current.TagName + string.Concat(current.ClassList.Where(value => value.Length <= 64
                        && value.All(character => char.IsAsciiLetter(character) || character is '_' or '-')).Select(value => "." + value)));
                return path;
            }).ToArray(),
            containers = document.QuerySelectorAll("body > *, div, p, h1, h2, h3").Where(element => element.TagName is not ("SCRIPT" or "STYLE"))
                .Select(element => element.TagName + string.Concat(element.ClassList
                    .Where(value => value.Length <= 64 && value.All(character => char.IsAsciiLetter(character) || character is '_' or '-'))
                    .Select(value => "." + value))).Distinct().Take(40).ToArray(),
            inputNames = document.QuerySelectorAll("input[name], input[id]").Select(input => input.GetAttribute("name") ?? input.GetAttribute("id") ?? "")
                .Where(value => value.Length <= 64 && value.All(character => char.IsAsciiLetter(character) || character is '_' or '-')).Distinct().Take(32).ToArray(),
            links = document.QuerySelectorAll("a[href], iframe[src], form[action]").Select(element =>
            {
                var raw = element.GetAttribute("href") ?? element.GetAttribute("src") ?? element.GetAttribute("action");
                if (!Uri.TryCreate(address, raw, out var target) || target.Scheme is not ("http" or "https")
                    || !ProbePolicy.IsOfficialHost(target.IdnHost)) return null;
                var path = "/" + string.Join("/", target.AbsolutePath.Trim('/').Split('/').Select(segment =>
                    segment is "mooc2-ans" or "mooc-ans" or "mooc2"
                        || segment.Length is > 0 and <= 64 && segment.All(character => char.IsAsciiLetter(character) || character is '_' or '-' or '.')
                        ? segment : "[parameter]"));
                var names = target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)[0])
                    .Where(value => value.Length <= 64 && value.All(character => char.IsAsciiLetter(character) || character is '_' or '-')).Distinct().Take(24).ToArray();
                return new { tag = element.TagName, host = target.IdnHost, path, parameterNames = names };
            }).Where(link => link is not null).Take(12).ToArray()
        };
    }
}
