using System.Text;
using System.Text.Json.Serialization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AccessProbe;

// An observed exam's identity, scoped by course, class and the platform's own exam id.
// The reader never starts an exam; the address is only kept for the user's own navigation.
public sealed record ExamTaskLocation(
    string CourseId,
    string ClassId,
    string ExamId,
    [property: JsonIgnore] Uri PageUri)
{
    public override string ToString() => nameof(ExamTaskLocation);
}

public sealed record ExamListTask(string Title, string PlatformStatusText, string DisplayedTimeText, ExamTaskLocation? Location)
{
    [JsonIgnore] public DateTimeOffset? TimeObservedAtUtc { get; init; }
    // The platform marks an elapsed exam window with a "ks_02" picture and/or an "已结束" status.
    [JsonIgnore] public bool Expired { get; init; }
    [JsonIgnore] public string LinkOutcome => Location is null ? "unresolved" : "observed_from_list";
}

public sealed record ExamListParseResult(string Outcome, IReadOnlyList<ExamListTask> Tasks, int MissingTitles, int InvalidLinks)
{
    public string Coverage => "provided_html_only_not_complete";
}

// Independently implemented from the public Web request contract documented in docs/自动同步.md.
// The phone exam list is a cookie-authenticated HTML page; its rows carry course, class and exam ids.
public static class ChaoxingExamParser
{
    public const int MaximumBytes = ChaoxingSyncParser.MaximumBytes;
    public const string ListAddress = "https://mooc1-api.chaoxing.com/exam-ans/exam/phone/examcode";
    public const string ListPath = "/exam-ans/exam/phone/examcode";
    private static readonly System.Text.RegularExpressions.Regex ExamWindow = new(
        @"(?<start>(?:[0-9]{4}-)?[0-9]{1,2}-[0-9]{1,2}\s+[0-9]{1,2}:[0-9]{2})\s*(?:至|~|～|—|–|到)\s*(?<end>(?:(?:[0-9]{4}-)?[0-9]{1,2}-[0-9]{1,2}\s+)?[0-9]{1,2}:[0-9]{2})",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static Uri ListUri => new(ListAddress);

    public static bool IsReadAddress(Uri address) => address.Scheme == Uri.UriSchemeHttps
        && address.IsDefaultPort && address.UserInfo.Length == 0 && address.Fragment.Length == 0
        && address.AbsolutePath == ListPath
        && (address.IdnHost.Equals("mooc1-api.chaoxing.com", StringComparison.OrdinalIgnoreCase)
            || address.IdnHost.Equals("mooc1.chaoxing.com", StringComparison.OrdinalIgnoreCase));

    public static ExamListParseResult Parse(string html, Uri pageAddress)
    {
        var document = Document(html);
        if (document is null) return new("response_too_large", [], 0, 0);
        var rows = document.QuerySelectorAll("#chaoxing-assignment-wrapper ul.ks_list > li, ul.ks_list > li");
        var tasks = new List<ExamListTask>();
        var missingTitles = 0; var invalidLinks = 0;
        foreach (var row in rows)
        {
            var title = Text(row.QuerySelector("dl > dt, dt")?.TextContent ?? row.GetAttribute("title"));
            var status = Text(row.QuerySelector("span.ks_state, .ks_state")?.TextContent);
            var time = Text(row.QuerySelector("dl > dd, dd")?.TextContent);
            var expired = row.QuerySelector("img[src*='ks_02']") is not null;
            // Only literal addresses are read; scripts and click handlers are never evaluated.
            var raw = row.GetAttribute("data") ?? row.QuerySelector("a[data]")?.GetAttribute("data")
                ?? row.QuerySelector("a[href]")?.GetAttribute("href");
            var location = ParseLocation(raw, pageAddress);
            if (title.Length == 0) { missingTitles++; continue; }
            if (location is null) { invalidLinks++; continue; }
            tasks.Add(new(title, status, time, location) { Expired = expired });
        }
        var outcome = rows.Length == 0
            ? (IsExplicitEmptyExamList(document) ? "empty" : "schema_unrecognized")
            : tasks.Count == 0 ? "schema_unrecognized"
            : missingTitles + invalidLinks > 0 ? "parsed_partial" : "parsed";
        return new(outcome, tasks, missingTitles, invalidLinks);
    }

    public static ExamTaskLocation? ParseLocation(string? raw, Uri? pageAddress = null)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 16384) return null;
        if (raw.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)
            || character is '\\' or '"' or '\'' or '<' or '>')) return null;
        if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(pageAddress ?? ListUri, raw, out var address)) return null;
        if (!IsReadAddressNavigation(address)) return null;
        var fields = ChaoxingSyncParser.Query(address);
        if (fields is null) return null;
        var courseId = Field(fields, "courseId");
        var classId = Field(fields, "classId");
        var examId = Field(fields, "taskrefId", "examId");
        if (!Numeric(courseId) || !Numeric(classId) || !Numeric(examId)) return null;
        return new(courseId!, classId!, examId!, address);
    }

    // A row is concluded when the platform reports a finished answer state or an elapsed exam window.
    public static bool IsConcluded(ExamListTask row)
    {
        if (row.Expired) return true;
        var status = row.PlatformStatusText;
        return new[] { "已完成", "待批阅", "已批阅", "已批改", "已结束", "已过期" }
            .Any(token => status.Contains(token, StringComparison.Ordinal));
    }

    // Normalizes the exam window text into the detail-window shape the metadata reader understands.
    // Returns an empty string when the row only shows a countdown or a duration.
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

    private static bool IsReadAddressNavigation(Uri address) => IsReadAddress(address)
        // Do-task entries may live on another official Chaoxing host; they remain navigation targets only.
        || (address.Scheme == Uri.UriSchemeHttps && address.IsDefaultPort && address.UserInfo.Length == 0
            && address.Fragment.Length == 0 && address.AbsolutePath.StartsWith("/exam", StringComparison.Ordinal)
            && ProbePolicy.IsOfficialHost(address.IdnHost));

    private static bool IsExplicitEmptyExamList(IDocument document) =>
        document.QuerySelectorAll("#chaoxing-assignment-wrapper, .ks_list, .exam-list, .main")
            .Any(list => Text(list.TextContent) is "暂无考试" or "暂无考试安排" or "没有考试")
        || document.QuerySelectorAll("img[src*='no_data'], .no-data, .null-data")
            .Any(list => Text(list.TextContent).Contains("暂无考试", StringComparison.Ordinal));

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
