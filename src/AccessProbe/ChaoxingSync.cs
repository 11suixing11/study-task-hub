using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AccessProbe;

public sealed record ChaoxingCourse(
    string Name,
    [property: JsonIgnore] string CourseId,
    [property: JsonIgnore] string ClassId,
    [property: JsonIgnore] string Cpi,
    [property: JsonIgnore] Uri PageUri)
{
    [JsonIgnore] public string Key => string.Join("|", CourseId, ClassId, Cpi);
    public override string ToString() => nameof(ChaoxingCourse);
}

public sealed record ChaoxingPage(string Outcome, int? StatusCode,
    [property: JsonIgnore] Uri Address, [property: JsonIgnore] string Html)
{
    public override string ToString() => nameof(ChaoxingPage);
}

public interface IChaoxingPageReader
{
    Task<ChaoxingPage> ReadAsync(Uri address, CancellationToken cancellation);
}

public sealed record ChaoxingCatalog(string Outcome, IReadOnlyList<ChaoxingCourse> Courses,
    IReadOnlyList<Uri> NextPages, int? ExpectedPages, bool UnresolvedPaging);

public sealed record ChaoxingSyncProblem(string Code, string Course, string Message);
public sealed record ChaoxingSyncProgress(string Message, int Courses, int CoursesRead, int Tasks, int Added, int Updated);
public sealed record ChaoxingSyncSummary(int Courses, int CoursesRead, int Tasks, int Added, int Updated,
    int DetailsSaved, int DetailsDeferred, bool LoginRequired, bool CourseListCovered,
    IReadOnlyList<ChaoxingSyncProblem> Problems)
{
    public int DetailsUnavailable { get; init; }
    public int DetailsRequireAction { get; init; }
    public int? SelectedCourses { get; init; }
}

// Independently implemented from the public Web request contract documented in docs/自动同步.md.
// Page-provided signatures and course identifiers are used in memory, never invented or logged.
public static class ChaoxingSyncParser
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const string Portal = "https://i.chaoxing.com/base";
    public const string Catalog = "https://mooc2-ans.chaoxing.com/mooc2-ans/visit/courselistdata";
    private static readonly Regex FidLiteral = new(
        "(?:\\b(?:var|let|const)\\s+fid\\s*=\\s*['\"]?([0-9]{1,64})['\"]?|\"fid\"\\s*:\\s*\"?([0-9]{1,64})\"?)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex CourseUrl = new(
        @"(?<![A-Za-z0-9/:._-])(?:(?:https?:)?//mooc1\.chaoxing\.com)?/?visit/stucoursemiddle\?[^\s'""<>\\]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public static bool IsReadAddress(Uri address) => address.Scheme == Uri.UriSchemeHttps
        && address.IsDefaultPort && address.UserInfo.Length == 0 && address.Fragment.Length == 0
        && address.OriginalString.Length <= 16384
        && !address.OriginalString.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '"' or '\'' or '<' or '>')
        && ((address.IdnHost.Equals("i.chaoxing.com", StringComparison.OrdinalIgnoreCase) && address.AbsolutePath == "/base")
            || (address.IdnHost.Equals("mooc2-ans.chaoxing.com", StringComparison.OrdinalIgnoreCase)
                && address.AbsolutePath is "/mooc2-ans/visit/courselistdata" or "/mooc2-ans/visit/courses"
                    or "/mooc2-ans/mycourse/studentcourse" or "/mooc2-ans/mycourse/stu")
            || (address.IdnHost.Equals("mooc1.chaoxing.com", StringComparison.OrdinalIgnoreCase)
                && address.AbsolutePath is "/visit/stucoursemiddle" or "/mycourse/studentcourse" or "/mooc-ans/mycourse/studentcourse"
                    or "/mooc2/work/list" or "/mooc-ans/mooc2/work/list" or "/mooc-ans/mooc2/work/task"
                    or "/mooc-ans/mooc2/work/prompt" or "/mooc-ans/mooc2/work/view"));

    public static bool IsCourseRedirect(Uri source, Uri target)
    {
        var courseEntry = source.AbsolutePath is "/visit/stucoursemiddle" or "/mycourse/studentcourse"
            or "/mooc-ans/mycourse/studentcourse" or "/mooc2-ans/mycourse/studentcourse" or "/mooc2-ans/mycourse/stu";
        if (!courseEntry) return true;
        var sourceFields = Query(source);
        var targetFields = Query(target);
        return sourceFields is not null && targetFields is not null
            && new[] { "courseid", "clazzid", "cpi" }.All(key =>
                sourceFields.TryGetValue(key, out var value) && Numeric(value)
                && targetFields.TryGetValue(key, out var nextValue) && nextValue == value);
    }

    public static bool IsWorkRedirect(Uri source, Uri target)
    {
        if (source.AbsolutePath is not ("/mooc-ans/mooc2/work/task" or "/mooc-ans/mooc2/work/prompt" or "/mooc-ans/mooc2/work/view")) return true;
        var original = WorkListParser.ParseDetailLocation(source.OriginalString);
        var redirected = WorkListParser.ParseDetailLocation(target.OriginalString);
        return SameWork(original, redirected);
    }

    public static bool IsWorkActionRedirect(Uri source, Uri target) => SameWork(
        WorkListParser.ParseDetailLocation(source.OriginalString), WorkListParser.ParseActionLocation(target.OriginalString));

    private static bool SameWork(WorkTaskLocation? original, WorkTaskLocation? redirected) =>
        original is not null && redirected is not null
            && original.CourseId == redirected.CourseId && original.ClassId == redirected.ClassId
            && original.Cpi == redirected.Cpi && original.WorkId == redirected.WorkId
            && (original.AnswerId is null || original.AnswerId == redirected.AnswerId);

    public static bool IsAuthenticationAddress(Uri address) => ProbePolicy.IsOfficialHost(address.IdnHost)
        && (address.IdnHost.StartsWith("passport", StringComparison.OrdinalIgnoreCase)
            || address.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase));

    public static bool IsLoginPage(string html)
    {
        var document = Document(html);
        return document is not null && (document.QuerySelector("form[action*='fanyalogin'], #loginForm") is not null
            || document.Title?.Contains("登录", StringComparison.Ordinal) == true && document.QuerySelector("input[type='password']") is not null);
    }

    public static Uri? ReadCatalogAddress(string html)
    {
        var document = Document(html);
        if (document is null) return null;
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in document.QuerySelectorAll("input#fid, input[name='fid']"))
            if (Numeric(input.GetAttribute("value"))) values.Add(input.GetAttribute("value")!);
        foreach (var script in document.QuerySelectorAll("script:not([src])"))
            foreach (Match match in FidLiteral.Matches(script.TextContent))
                values.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        if (values.Count != 1) return null;
        return Build(Catalog, ("courseType", "1"), ("courseFid", values.Single()),
            ("courseTeacherid", "0"), ("courseOrderBy", "0"));
    }

    public static ChaoxingCatalog ParseCatalog(string html, Uri pageAddress)
    {
        var document = Document(html);
        if (document is null) return new("response_too_large", [], [], null, false);
        var courses = new Dictionary<string, ChaoxingCourse>(StringComparer.Ordinal);
        foreach (var element in document.QuerySelectorAll("[href], [data], [data-url], [onclick]"))
        {
            foreach (var attribute in new[] { "href", "data", "data-url", "onclick" })
            {
                var raw = element.GetAttribute(attribute);
                if (raw is null) continue;
                // Only literal course-entry URLs are read; JavaScript is never evaluated.
                foreach (Match match in CourseUrl.Matches(raw))
                {
                    var address = Resolve(new Uri("https://mooc1.chaoxing.com/"), match.Value);
                    if (address is null || address.IdnHost != "mooc1.chaoxing.com" || address.AbsolutePath != "/visit/stucoursemiddle") continue;
                    var fields = Query(address);
                    if (fields is null || !fields.TryGetValue("courseid", out var courseId) || !Numeric(courseId)
                        || !fields.TryGetValue("clazzid", out var classId) || !Numeric(classId)
                        || !fields.TryGetValue("cpi", out var cpi) || !Numeric(cpi)) continue;
                    IElement? title = null;
                    for (var row = element; row is not null && row.TagName is not ("BODY" or "HTML"); row = row.ParentElement)
                    {
                        title = row.Matches(".course-name") ? row : row.QuerySelector(".course-name");
                        if (title is not null) break;
                    }
                    var name = Text(title?.GetAttribute("title") ?? title?.TextContent);
                    var course = new ChaoxingCourse(name.Length == 0 ? "课程名称待识别" : name, courseId, classId, cpi, address);
                    courses.TryAdd(course.Key, course);
                }
            }
        }
        var pages = Paging(document, pageAddress);
        var empty = courses.Count == 0 && document.QuerySelector(".course-list, .courseList, #courseList") is { } list
            && Text(list.TextContent) is "暂无课程" or "您还没有课程";
        return new(courses.Count > 0 ? "parsed" : empty ? "empty" : "schema_unrecognized",
            courses.Values.ToArray(), pages.Links, pages.Expected, pages.Unresolved);
    }

    public static Uri? ReadWorkListAddress(string html, ChaoxingCourse course)
    {
        var document = Document(html);
        if (document is null) return null;
        foreach (var (field, expected) in new[] { ("courseid", course.CourseId), ("clazzid", course.ClassId), ("cpi", course.Cpi) })
        {
            var observed = Hidden(document, field);
            if (!string.IsNullOrEmpty(observed) && observed != expected) return null;
        }
        // workEnc is specifically the work-list signature; the course's general enc is not a substitute.
        var workEnc = Hidden(document, "workEnc");
        if (string.IsNullOrWhiteSpace(workEnc) || workEnc.Length > 4096) return null;
        return Build("https://mooc1.chaoxing.com/mooc2/work/list",
            ("courseId", course.CourseId), ("classId", course.ClassId), ("cpi", course.Cpi), ("ut", "s"),
            ("openc", Hidden(document, "openc") ?? ""), ("enc", workEnc),
            ("stuenc", Hidden(document, "enc") ?? ""), ("isdisplaytable", "2"));
    }

    public static (IReadOnlyList<Uri> Links, int? Expected, bool Unresolved) ReadWorkPaging(string html, Uri address)
    {
        var document = Document(html);
        return document is null ? ([], null, true) : Paging(document, address);
    }

    public static bool IsExplicitEmptyWorkList(string html)
    {
        var document = Document(html);
        if (document is null) return false;
        if (document.QuerySelector("li[data][onclick]") is not null) return false;
        return document.QuerySelectorAll("#workList, .work-list, .workList, .null, .task-list .has-content > .null-data")
            .Any(list => Text(list.TextContent) is "暂无作业" or "暂无作业！" or "暂无作业!" or "没有作业");
    }

    public static bool SameCourse(WorkTaskLocation location, ChaoxingCourse course) =>
        location.CourseId == course.CourseId && location.ClassId == course.ClassId && location.Cpi == course.Cpi;

    private static (IReadOnlyList<Uri> Links, int? Expected, bool Unresolved) Paging(IDocument document, Uri address)
    {
        var links = new Dictionary<string, Uri>(StringComparer.Ordinal);
        var scope = Query(address);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var candidate = Resolve(address, anchor.GetAttribute("href"));
            if (candidate is null || candidate.IdnHost != address.IdnHost || candidate.AbsolutePath != address.AbsolutePath) continue;
            var fields = Query(candidate);
            // Follow actual links within this course/catalog scope, never fabricate page numbers or account parameters.
            var scopeFields = address.AbsolutePath.Contains("courselist", StringComparison.Ordinal)
                ? new[] { "courseType", "courseFid" } : new[] { "courseId", "classId", "cpi" };
            if (scope is null || fields is null || scopeFields.Any(key => scope.GetValueOrDefault(key) != fields.GetValueOrDefault(key))) continue;
            if (candidate.AbsoluteUri != address.AbsoluteUri) links.TryAdd(candidate.AbsoluteUri, candidate);
        }
        int? expected = null;
        foreach (var element in document.QuerySelectorAll("[data-total-pages], input#totalPage, input[name='totalPage']"))
        {
            var raw = element.GetAttribute("data-total-pages") ?? element.GetAttribute("value");
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and < 10000)
                expected = Math.Max(expected ?? 0, count);
        }
        var unresolved = document.QuerySelectorAll(".pagination [onclick], .page [onclick], #page [onclick]")
            .Any(element => !element.HasAttribute("disabled") && !element.ClassList.Contains("disabled")
                && !string.IsNullOrWhiteSpace(element.GetAttribute("onclick"))
                && Resolve(address, element.GetAttribute("href")) is null);
        return (links.Values.ToArray(), expected, unresolved);
    }

    private static string? Hidden(IDocument document, string key)
    {
        var values = document.QuerySelectorAll($"input[id='{key}'], input[name='{key}']")
            .Select(element => element.GetAttribute("value")).Where(value => value is not null).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    internal static Dictionary<string, string>? Query(Uri address)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 1) return null;
            var key = Uri.UnescapeDataString(pair[..separator].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            if (!result.TryAdd(key, value)) return null;
        }
        return result;
    }

    private static Uri? Resolve(Uri address, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(address, raw, out var resolved)) return null;
        if (resolved.Scheme == Uri.UriSchemeHttp && ProbePolicy.TryGetHttpsNavigation(resolved.OriginalString, out var https)) resolved = new Uri(https);
        return IsReadAddress(resolved) ? resolved : null;
    }

    private static Uri Build(string endpoint, params (string Key, string Value)[] fields) => new(endpoint + "?"
        + string.Join("&", fields.Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value))));
    private static IDocument? Document(string html) => Encoding.UTF8.GetByteCount(html) <= MaximumBytes
        ? new HtmlParser().ParseDocument(html) : null;
    private static bool Numeric(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => c is >= '0' and <= '9');
    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? ""
        : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public sealed class ChaoxingSynchronizer(TaskStore store, IChaoxingPageReader reader, TimeSpan? requestSpacing = null)
{
    public const int MaximumCourses = 200;
    public const int MaximumPages = 20;
    public const int MaximumDetails = 100;
    public const int MaximumRequests = 600;
    private int requests;
    private long recordingRevision;

    public async Task<ChaoxingSyncSummary> RunAsync(IProgress<ChaoxingSyncProgress>? progress, CancellationToken cancellation, bool catalogOnly = false)
    {
        requests = 0;
        var problems = new List<ChaoxingSyncProblem>();
        var courses = new Dictionary<string, ChaoxingCourse>(StringComparer.Ordinal);
        var rowsByKey = new Dictionary<string, WorkListTask>(StringComparer.Ordinal);
        var coursesRead = 0; var added = 0; var updated = 0; var detailCount = 0; var deferred = 0;
        var detailUnavailable = 0; var detailRequireAction = 0;
        var loginRequired = false; var catalogCovered = true;
        recordingRevision = store.LoadCourseRecording("chaoxing").Revision;
        var selectedCourses = 0;
        void Report(string message) => progress?.Report(new(message, courses.Count, coursesRead, rowsByKey.Count, added, updated));
        void Problem(string code, string course, string message) => problems.Add(new(code, course, message));
        try
        {
            Report("正在检查学习通登录会话…");
            var portal = await Fetch(new(ChaoxingSyncParser.Portal), cancellation);
            RequirePage(portal);
            var catalogAddress = await Task.Run(() => ChaoxingSyncParser.ReadCatalogAddress(portal.Html), cancellation);
            if (catalogAddress is null) throw new SyncFailure("SYNC003", "无法识别课程列表所需的账号上下文，请重新登录或查看同步诊断。");
            var catalogQueue = new Queue<Uri>(); catalogQueue.Enqueue(catalogAddress);
            var catalogVisited = new HashSet<string>(StringComparer.Ordinal);
            int? catalogExpectedPages = null;
            while (catalogQueue.TryDequeue(out var address))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!catalogVisited.Add(address.AbsoluteUri)) continue;
                if (catalogVisited.Count > MaximumPages)
                { catalogCovered = false; Problem("SYNC007", "", "课程分页达到本轮读取上限，尚未覆盖剩余页面。"); break; }
                Report("正在自动发现课程…");
                var page = await Fetch(address, cancellation); RequirePage(page);
                var catalog = await Task.Run(() => ChaoxingSyncParser.ParseCatalog(page.Html, page.Address), cancellation);
                if (catalog.Outcome is not ("parsed" or "empty")) throw new SyncFailure("SYNC003", "课程列表结构未识别，不能确认没有课程。");
                catalogExpectedPages = Math.Max(catalogExpectedPages ?? 0, catalog.ExpectedPages ?? 0);
                if (catalog.UnresolvedPaging) catalogCovered = false;
                foreach (var course in catalog.Courses)
                {
                    if (courses.Count >= MaximumCourses && !courses.ContainsKey(course.Key)) { catalogCovered = false; continue; }
                    courses.TryAdd(course.Key, course);
                }
                foreach (var next in catalog.NextPages) if (!catalogVisited.Contains(next.AbsoluteUri)) catalogQueue.Enqueue(next);
            }
            if (catalogExpectedPages > catalogVisited.Count) catalogCovered = false;
            if (!catalogCovered && !problems.Any(problem => problem.Code == "SYNC007"))
                Problem("SYNC007", "", "课程列表存在尚未识别的分页或达到数量上限，当前清单可能不完整。");

            var recording = await Task.Run(() => store.ObserveRecordingCourses("chaoxing",
                courses.Values.Select(CourseRecordingRules.Chaoxing).ToArray(), catalogCovered,
                DateTimeOffset.UtcNow, cancellation), cancellation);
            store.CheckCourseRecordingRevision("chaoxing", recordingRevision, cancellation);
            var selected = courses.Values.Where(course => CourseRecordingRules.IsSelected(recording,
                CourseRecordingRules.ChaoxingKey(course.CourseId, course.ClassId, course.Cpi))).ToArray();
            selectedCourses = selected.Length;
            foreach (var course in catalogOnly ? [] : selected)
            {
                cancellation.ThrowIfCancellationRequested();
                store.CheckCourseRecordingRevision("chaoxing", recordingRevision, cancellation);
                Report($"正在读取课程：{course.Name}（{coursesRead + 1}/{selectedCourses} 门已选课程）");
                try
                {
                    var entry = await Fetch(course.PageUri, cancellation); RequirePage(entry);
                    var workAddress = await Task.Run(() => ChaoxingSyncParser.ReadWorkListAddress(entry.Html, course), cancellation);
                    if (workAddress is null) throw new SyncFailure("SYNC005", "课程页未返回可用的作业入口，不能确认此课程没有作业。");
                    var workQueue = new Queue<Uri>(); workQueue.Enqueue(workAddress);
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    var courseRows = new Dictionary<string, WorkListTask>(StringComparer.Ordinal);
                    int? expectedPages = null; var covered = true; var skipped = 0;
                    while (workQueue.TryDequeue(out var address))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!visited.Add(address.AbsoluteUri)) continue;
                        if (visited.Count > MaximumPages) { covered = false; break; }
                        var page = await Fetch(address, cancellation); RequirePage(page);
                        var timeObservedAt = DateTimeOffset.UtcNow;
                        var list = await Task.Run(() => WorkListParser.Parse(page.Html), cancellation);
                        if (list.Outcome != "parsed_partial" && !ChaoxingSyncParser.IsExplicitEmptyWorkList(page.Html))
                            throw new SyncFailure("SYNC006", "作业列表结构未识别，不能确认此课程没有作业。");
                        skipped += list.MissingTitles;
                        foreach (var row in list.Tasks)
                        {
                            if (row.Location is not { } location || !ChaoxingSyncParser.SameCourse(location, course)
                                || row.Title.Length > 300) { skipped++; continue; }
                            courseRows.TryAdd(TaskStore.ObservedWorkKey(location)!, row with { TimeObservedAtUtc = timeObservedAt });
                        }
                        var paging = ChaoxingSyncParser.ReadWorkPaging(page.Html, page.Address);
                        expectedPages = Math.Max(expectedPages ?? 0, paging.Expected ?? 0);
                        covered &= !paging.Unresolved;
                        foreach (var next in paging.Links) if (!visited.Contains(next.AbsoluteUri)) workQueue.Enqueue(next);
                    }
                    if (expectedPages > visited.Count) covered = false;
                    if (!covered) Problem("SYNC007", course.Name, "存在尚未识别的作业分页或达到读取上限，此课程任务可能不完整。");
                    if (skipped > 0) Problem("SYNC006", course.Name, $"{skipped} 条作业的名称、链接或课程归属无法确认，未自动保存这些条目。");
                    if (course.Name == "课程名称待识别") Problem("SYNC003", course.Name, "已读取任务，但课程名称尚未识别。");
                    if (course.Name.Length > 200) throw new SyncFailure("SYNC003", "课程名称超过保存长度，未保存此课程任务。");
                    var imported = await Task.Run(() => store.ImportWorks(courseRows.Values.ToArray(), course.Name, DateTimeOffset.UtcNow, cancellation, recordingRevision), cancellation);
                    added += imported.Added; updated += imported.Updated; coursesRead++;
                    foreach (var row in courseRows) rowsByKey.TryAdd(row.Key, row.Value);
                    Report($"已读取 {coursesRead}/{selectedCourses} 门已选课程，新增 {added} 条、更新 {updated} 条。");
                }
                catch (SyncFailure failure) when (failure.Code != "SYNC001" && failure.Code != "SYNC008")
                { Problem(failure.Code, course.Name, failure.Message); }
            }

            // Read metadata without asking the user to open each task; recently read headers are reused.
            var savedTasks = await Task.Run(store.LoadTasks, cancellation);
            var details = savedTasks.Where(task => task.SourceKey is not null && rowsByKey.ContainsKey(task.SourceKey)
                    && (task.DetailCheckedAtUtc is null || task.DetailCheckedAtUtc < DateTimeOffset.UtcNow.AddHours(-24)))
                .OrderBy(task => task.DetailCheckedAtUtc ?? DateTimeOffset.MinValue).ToArray();
            deferred = Math.Max(0, details.Length - MaximumDetails);
            foreach (var task in details.Take(MaximumDetails))
            {
                cancellation.ThrowIfCancellationRequested();
                store.CheckCourseRecordingRevision("chaoxing", recordingRevision, cancellation);
                var row = rowsByKey[task.SourceKey!];
                Report($"正在读取任务时间（{detailCount + 1}/{Math.Min(details.Length, MaximumDetails)}）…");
                try
                {
                    var page = await Fetch(row.Location!.PageUri, cancellation);
                    if (page.Outcome == "detail_requires_user_action")
                    {
                        detailRequireAction++;
                        await Task.Run(() => store.RecordDetailCheck(row.Location, DateTimeOffset.UtcNow, cancellation, recordingRevision), cancellation);
                        continue;
                    }
                    RequirePage(page);
                    // A redirect to another work must not associate its dates with this task.
                    var finalLocation = WorkListParser.ParseDetailLocation(page.Address.OriginalString);
                    if (!ChaoxingSyncParser.IsWorkRedirect(row.Location.PageUri, page.Address)
                        || TaskStore.ObservedWorkKey(finalLocation) != task.SourceKey)
                        throw new SyncFailure("SYNC006", "任务详情的归属无法确认，未保存时间。");
                    var detail = await Task.Run(() => WorkDetailParser.Parse(page.Html), cancellation);
                    if (detail.AnswerWindow is null)
                    {
                        await Task.Run(() => store.RecordDetailCheck(row.Location, DateTimeOffset.UtcNow, cancellation, recordingRevision), cancellation);
                        if (detail.Outcome == "submitted_prompt_without_time_window") detailUnavailable++;
                        else Problem("SYNC006", task.CourseName, "部分任务的时间结构未识别，已保留列表任务。");
                        continue;
                    }
                    await Task.Run(() => store.SaveDetail(row.Location, detail, DateTimeOffset.UtcNow, cancellation, recordingRevision), cancellation);
                    detailCount++;
                }
                catch (SyncFailure failure) when (failure.Code != "SYNC001" && failure.Code != "SYNC008")
                { Problem(failure.Code, task.CourseName, "部分任务时间未读取，已保留列表任务。" ); }
                catch (TaskValidationException failure) when (failure.Code == "LINK003")
                { /* A task deleted during synchronization is not recreated by its detail response. */ }
            }
        }
        catch (SyncFailure failure)
        {
            loginRequired = failure.Code == "SYNC001";
            catalogCovered = false;
            Problem(failure.Code, "", failure.Message);
        }
        cancellation.ThrowIfCancellationRequested();
        store.CheckCourseRecordingRevision("chaoxing", recordingRevision, cancellation);
        return new(courses.Count, coursesRead, rowsByKey.Count, added, updated, detailCount, deferred,
            loginRequired, catalogCovered, problems.Distinct().ToArray())
            { DetailsUnavailable = detailUnavailable, DetailsRequireAction = detailRequireAction, SelectedCourses = selectedCourses };
    }

    private async Task<ChaoxingPage> Fetch(Uri address, CancellationToken cancellation)
    {
        store.CheckCourseRecordingRevision("chaoxing", recordingRevision, cancellation);
        if (++requests > MaximumRequests) throw new SyncFailure("SYNC008", "本轮请求达到上限，剩余任务将在后续同步中继续检查。");
        if (requests > 1 && (requestSpacing ?? TimeSpan.FromMilliseconds(250)) > TimeSpan.Zero)
            await Task.Delay(requestSpacing ?? TimeSpan.FromMilliseconds(250), cancellation);
        return await reader.ReadAsync(address, cancellation);
    }

    private static void RequirePage(ChaoxingPage page)
    {
        if (page.Outcome == "ok") return;
        throw page.Outcome switch
        {
            "login_required" => new SyncFailure("SYNC001", "学习通尚未登录或会话已失效。完成一次官方登录后会自动继续同步。"),
            "forbidden" => new SyncFailure("SYNC004", "平台拒绝读取，请核对课程权限；已保留本地任务。"),
            "rate_limited" => new SyncFailure("SYNC008", "平台要求降低请求频率，已停止本轮同步。"),
            "response_too_large" => new SyncFailure("SYNC006", "页面超过读取上限，尚未确认完整任务。"),
            "unsupported_redirect" => new SyncFailure("SYNC005", "平台返回了未适配的页面跳转，未继续请求。"),
            _ => new SyncFailure("SYNC002", "学习通读取失败或超时，已保留本地任务，可稍后重新同步。")
        };
    }

    private sealed class SyncFailure(string code, string message) : Exception(message)
    { public string Code { get; } = code; }
}
