using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace AccessProbe;

// Observed list identifiers are used only for local deduplication within the same course/class/account scope.
public sealed record WorkTaskLocation(
    string CourseId,
    string ClassId,
    string WorkId,
    string? AnswerId,
    [property: JsonIgnore] Uri PageUri)
{
    [JsonIgnore] public string Cpi { get; init; } = "";
    // The platform-provided URL may contain an account-bound signature. Do not export it in reports.
    public override string ToString() => nameof(WorkTaskLocation);
}

public sealed record WorkListTask(
    string Title,
    string PlatformStatusText,
    string DisplayedTimeText,
    WorkTaskLocation? Location)
{
    [JsonIgnore] public DateTimeOffset? TimeObservedAtUtc { get; init; }
    // A rounded remaining-time label does not establish an exact deadline.
    public DateTimeOffset? DeadlineUtc => null;
    public string LinkOutcome => Location is null ? "unresolved" : "observed_from_list";
}

public sealed record WorkListParseResult(
    string Outcome,
    IReadOnlyList<WorkListTask> Tasks,
    int MissingTitles,
    int InvalidLinks)
{
    public string Coverage => Outcome == "parsed_partial" ? "provided_html_only_not_complete" : "not_verified";
}

public static class WorkListParser
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private const string TaskPath = "/mooc-ans/mooc2/work/task";
    private const string PromptPath = "/mooc-ans/mooc2/work/prompt";
    private const string ViewPath = "/mooc-ans/mooc2/work/view";
    private static readonly Regex TaskHandler = new(
        @"^\s*goTask\s*\(\s*this\s*\)\s*;?\s*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // HtmlParser parses the supplied text only. It does not execute scripts or load resources.
    public static WorkListParseResult Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (Encoding.UTF8.GetByteCount(html) > MaximumBytes)
            return new("response_too_large", [], 0, 0);

        var document = new HtmlParser().ParseDocument(html);
        var tasks = new List<WorkListTask>();
        var missingTitles = 0;
        var invalidLinks = 0;
        foreach (var row in document.QuerySelectorAll("li[data][onclick]"))
        {
            if (!TaskHandler.IsMatch(row.GetAttribute("onclick") ?? string.Empty)) continue;
            var title = NormalizeText(row.QuerySelector(".right-content p.overHidden2")?.TextContent);
            if (title.Length == 0)
            {
                missingTitles++;
                continue;
            }

            // GetAttribute already decodes HTML entities. Do not decode or rebuild the signed URL again.
            var location = ParseTaskLocation(row.GetAttribute("data"));
            if (location is null) invalidLinks++;
            tasks.Add(new WorkListTask(
                title,
                NormalizeText(row.QuerySelector(".right-content p.status")?.TextContent),
                NormalizeText(row.QuerySelector(".time")?.TextContent),
                location));
        }

        // A login page or changed markup must not be reported as a confirmed empty task list.
        return new(tasks.Count == 0 ? "schema_unrecognized" : "parsed_partial",
            tasks.AsReadOnly(), missingTitles, invalidLinks);
    }

    public static WorkTaskLocation? ParseTaskLocation(string? raw) => ParseLocation(raw, TaskPath);

    // Observed view/prompt metadata redirects keep the list's original signed task link intact.
    public static WorkTaskLocation? ParseDetailLocation(string? raw) => ParseLocation(raw, TaskPath, PromptPath, ViewPath);

    // Used only to recognize and stop a redirect into answering; it does not authorize fetching this route.
    internal static WorkTaskLocation? ParseActionLocation(string? raw) => ParseLocation(raw, "/mooc-ans/mooc2/work/dowork");

    private static WorkTaskLocation? ParseLocation(string? raw, params string[] paths)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var address = raw.Trim();
        if (address.Length > 16384 || address.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)
            || character is '\\' or '"' or '\'' or '<' or '>')) return null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !uri.IdnHost.Equals("mooc1.chaoxing.com", StringComparison.OrdinalIgnoreCase)
            || !paths.Contains(uri.AbsolutePath, StringComparer.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 1) return null;
            var key = Uri.UnescapeDataString(pair[..separator].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            if (!fields.TryAdd(key, value)) return null;
        }

        foreach (var key in new[] { "courseId", "classId", "cpi", "workId" })
            if (!fields.TryGetValue(key, out var value) || !IsNumericIdentifier(value)) return null;
        if (!fields.TryGetValue("enc", out var signature) || string.IsNullOrWhiteSpace(signature)) return null;
        fields.TryGetValue("answerId", out var answerId);
        if (answerId is not null && !IsNumericIdentifier(answerId)) return null;

        return new(fields["courseId"], fields["classId"], fields["workId"], answerId, uri) { Cpi = fields["cpi"] };
    }

    private static bool IsNumericIdentifier(string value) => value.Length is > 0 and <= 64
        && value.All(character => character is >= '0' and <= '9');

    private static string NormalizeText(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty
        : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
