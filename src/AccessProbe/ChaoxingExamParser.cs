using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AccessProbe;

// An observed exam's identity, scoped by course, class and the platform's own exam ids.
// The reader never starts an exam; the address is only kept for the user's own navigation.
public sealed record ExamTaskLocation(
    string CourseId,
    string ClassId,
    string ExamId,
    string RelationId,
    [property: JsonIgnore] Uri PageUri)
{
    public override string ToString() => nameof(ExamTaskLocation);
}

public sealed record ExamListTask(string Title, string PlatformStatusText, string DisplayedTimeText, ExamTaskLocation? Location)
{
    [JsonIgnore] public DateTimeOffset? TimeObservedAtUtc { get; init; }
    [JsonIgnore] public string LinkOutcome => Location is null ? "unresolved" : "observed_from_list";
}

public sealed record ExamListParseResult(string Outcome, IReadOnlyList<ExamListTask> Tasks, int MissingTitles, int InvalidLinks, bool PaginationUnresolved)
{
    public string Coverage => "provided_html_only_not_complete";
}

// Independently implemented from a captured, signed-in course exam tab (see docs/自动同步.md).
// The exam list is a per-course, cookie-authenticated HTML page. A row carries only a click handler
// with the platform's exam and relation ids; the handler is never evaluated, its literal ids are kept.
public static class ChaoxingExamParser
{
    public const int MaximumBytes = ChaoxingSyncParser.MaximumBytes;
    public const string ListAddress = "https://mooc1.chaoxing.com/exam-ans/mooc2/exam/exam-list";
    public const string ListPath = "/exam-ans/mooc2/exam/exam-list";
    private static readonly Regex HandlerPair = new(
        @"[""'](?<exam>[0-9]{1,64})[""']\s*,\s*[""'](?<relation>[0-9]{1,64})[""']",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ExamWindow = new(
        @"(?<start>(?:[0-9]{4}-)?[0-9]{1,2}-[0-9]{1,2}\s+[0-9]{1,2}:[0-9]{2})\s*(?:至|~|～|—|–|到)\s*(?<end>(?:(?:[0-9]{4}-)?[0-9]{1,2}-[0-9]{1,2}\s+)?[0-9]{1,2}:[0-9]{2})",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // A readable exam list always carries the signed course, class and cpi scope.
    public static bool IsReadAddress(Uri address) => Shaped(address) && ScopeOf(address) is not null;

    private static bool Shaped(Uri address) => address.Scheme == Uri.UriSchemeHttps
        && address.IsDefaultPort && address.UserInfo.Length == 0 && address.Fragment.Length == 0
        && address.AbsolutePath == ListPath
        && (address.IdnHost.Equals("mooc1.chaoxing.com", StringComparison.OrdinalIgnoreCase)
            || address.IdnHost.Equals("mooc2-ans.chaoxing.com", StringComparison.OrdinalIgnoreCase));

    // The course and class ids are part of the signed exam-list address, not of the row.
    public static (string CourseId, string ClassId, string Cpi)? Scope(Uri address) => Shaped(address) ? ScopeOf(address) : null;

    private static (string CourseId, string ClassId, string Cpi)? ScopeOf(Uri address)
    {
        var fields = ChaoxingSyncParser.Query(address);
        if (fields is null) return null;
        var courseId = Field(fields, "courseid");
        var classId = Field(fields, "clazzid");
        var cpi = Field(fields, "cpi");
        if (!Numeric(courseId) || !Numeric(classId) || !Numeric(cpi)) return null;
        return (courseId!, classId!, cpi!);
    }

    public static ExamListParseResult Parse(string html, Uri pageAddress)
    {
        var document = Document(html);
        if (document is null) return new("response_too_large", [], 0, 0, false);
        if (Scope(pageAddress) is not { } scope) return new("schema_unrecognized", [], 0, 0, false);
        var rows = document.QuerySelectorAll(".bottomList > ul > li, .bottomList ul > li");
        var pages = document.QuerySelector("#page, .pageDiv");
        // Pagination is filled by script; a non-empty indicator means further exams may exist unseen.
        var paginationUnresolved = pages is not null
            && (pages.ChildElementCount > 0 || Text(pages.TextContent).Length > 0);
        var tasks = new List<ExamListTask>();
        var missingTitles = 0; var invalidLinks = 0;
        foreach (var row in rows)
        {
            var title = Text(row.QuerySelector("p.overHidden2, .right-content p")?.TextContent);
            var status = Text(row.QuerySelector("p.status, .status")?.TextContent);
            var time = Text(row.QuerySelector("p.time, .time, .date")?.TextContent);
            var location = HandlerLocation(row, scope, pageAddress);
            if (title.Length == 0) { missingTitles++; continue; }
            if (location is null) { invalidLinks++; continue; }
            tasks.Add(new(title, status, time, location));
        }
        var outcome = rows.Length == 0
            ? (IsExplicitEmpty(document) ? "empty" : "schema_unrecognized")
            : tasks.Count == 0 ? "schema_unrecognized"
            : missingTitles + invalidLinks > 0 || paginationUnresolved ? "parsed_partial" : "parsed";
        return new(outcome, tasks, missingTitles, invalidLinks, paginationUnresolved);
    }

    // Only a literal handler is read; a click handler is never evaluated or replayed.
    public static ExamTaskLocation? HandlerLocation(IElement row, (string CourseId, string ClassId, string Cpi) scope, Uri pageAddress)
    {
        foreach (var element in row.QuerySelectorAll("[onclick]"))
        {
            var handler = element.GetAttribute("onclick");
            if (string.IsNullOrWhiteSpace(handler) || handler.Length > 4096) continue;
            var match = HandlerPair.Match(handler);
            if (!match.Success) continue;
            var examId = match.Groups["exam"].Value;
            var relationId = match.Groups["relation"].Value;
            if (!Numeric(examId) || !Numeric(relationId)) continue;
            return new(scope.CourseId, scope.ClassId, examId, relationId, pageAddress);
        }
        return null;
    }

    // Stored locations are re-verified before use: numeric ids and a signed list page for the same course.
    public static bool VerifyLocation(ExamTaskLocation location)
    {
        if (!Numeric(location.CourseId) || !Numeric(location.ClassId)
            || !Numeric(location.ExamId) || !Numeric(location.RelationId)) return false;
        if (Scope(location.PageUri) is not { } scope) return false;
        return scope.CourseId == location.CourseId && scope.ClassId == location.ClassId;
    }

    // Normalizes an optional window text into the shape the metadata reader understands.
    // Returns an empty string when the row shows only a status or a countdown.
    public static string WindowText(string displayedTimeText)
    {
        var match = ExamWindow.Match(displayedTimeText.Trim());
        if (!match.Success) return "";
        var start = match.Groups["start"].Value;
        var end = match.Groups["end"].Value;
        if (!end.Contains('-'))
        {
            // "11-20 14:00 至 16:00" borrows the date already shown for the start.
            var dateEnd = start.LastIndexOf(' ');
            if (dateEnd < 1) return "";
            end = start[..dateEnd] + " " + end;
        }
        return "作答时间:" + start + " 至 " + end;
    }

    // The signed list page itself is read; an empty known list container means no exams were published.
    private static bool IsExplicitEmpty(IDocument document)
    {
        if (document.QuerySelector(".has-content, .bottomList, .main.task-list") is null) return false;
        return document.QuerySelectorAll(".has-content, .null-data, .no-data, .bottomList")
            .Any(element => Text(element.TextContent).Contains("暂无考试", StringComparison.Ordinal))
            || document.QuerySelector(".has-content")?.GetAttribute("style")?.Contains("none", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? Field(IReadOnlyDictionary<string, string> fields, params string[] names)
    {
        var values = fields.Where(pair => names.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private static IDocument? Document(string html) => Encoding.UTF8.GetByteCount(html) <= MaximumBytes
        ? new HtmlParser().ParseDocument(html) : null;
    private static bool Numeric(string? value) => value is { Length: > 0 and <= 64 } && value.All(character => character is >= '0' and <= '9');
    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? ""
        : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
