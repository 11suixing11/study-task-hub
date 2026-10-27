using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace AccessProbe;

// A displayed month/day without a year is deliberately not a DateTime.
public sealed record WorkDisplayedTime(int? Year, int Month, int Day, int Hour, int Minute);

public sealed record WorkAnswerWindow(
    string StartText,
    string EndText,
    WorkDisplayedTime Start,
    WorkDisplayedTime End)
{
    public string Resolution => Start.Year.HasValue && End.Year.HasValue
        ? "time_zone_unconfirmed" : "year_unconfirmed";

    // No UTC deadline is produced until the platform's full date and time zone are established.
    public DateTimeOffset? DeadlineUtc => null;
}

public sealed record WorkDetailParseResult(
    string Outcome,
    string Title,
    string DisplayedWindowText,
    WorkAnswerWindow? AnswerWindow);

public static class WorkDetailParser
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly Regex WindowLabel = new(
        @"^作答时间\s*[:：]",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex DisplayedTime = new(
        @"^(?:(?<year>[0-9]{4})-)?(?<month>[0-9]{1,2})-(?<day>[0-9]{1,2})\s+(?<hour>[0-9]{1,2}):(?<minute>[0-9]{2})$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static WorkDetailParseResult ParseDisplayedWindow(string text)
    {
        if (text.Length > 512) return new("response_too_large", "", "", null);
        var match = Regex.Match(text, @"^作答时间\s*[:：]\s*(?<start>.+?)\s*至\s*(?<end>.+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return new("schema_unrecognized", "", "", null);
        return Parse("<p class='rightBord'>作答时间:<em>" + System.Net.WebUtility.HtmlEncode(match.Groups["start"].Value.Trim())
            + "</em>至<em>" + System.Net.WebUtility.HtmlEncode(match.Groups["end"].Value.Trim()) + "</em></p>");
    }

    // Parses supplied HTML or the inspected <p> fragment without executing scripts or fetching resources.
    public static WorkDetailParseResult Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (Encoding.UTF8.GetByteCount(html) > MaximumBytes)
            return new("response_too_large", string.Empty, string.Empty, null);

        var document = new HtmlParser().ParseDocument(html);
        var header = document.QuerySelector(".detailsHead");
        var title = NormalizeText(header?.QuerySelector("h2.mark_title")?.TextContent);
        var rows = (header is null
            ? document.QuerySelectorAll("p.rightBord")
            : header.QuerySelectorAll(".infoHead p.rightBord"))
            .Where(row => WindowLabel.IsMatch(NormalizeText(row.TextContent)))
            .ToArray();
        if (rows.Length == 0)
        {
            // A submitted-work prompt exposes a submission timestamp, not the permitted answer window.
            var submittedPrompt = header is null && document.QuerySelector(".workHead") is not null
                && document.QuerySelector(".workCen .subSucess") is not null
                && document.QuerySelector(".workCen > p.dpy") is not null
                && NormalizeText(document.QuerySelector(".workCen > p.subTime")?.TextContent)
                    .StartsWith("提交时间", StringComparison.Ordinal);
            return new(submittedPrompt ? "submitted_prompt_without_time_window" : "schema_unrecognized", title, string.Empty, null);
        }
        if (rows.Length != 1) return new("ambiguous_answer_window", title, string.Empty, null);

        var row = rows[0];
        var displayed = NormalizeText(row.TextContent);
        var endpoints = row.QuerySelectorAll("em");
        if (endpoints.Length != 2) return new("time_structure_unrecognized", title, displayed, null);
        var startText = NormalizeText(endpoints[0].TextContent);
        var endText = NormalizeText(endpoints[1].TextContent);
        var expectedWindow = @"^作答时间\s*[:：]\s*" + Regex.Escape(startText)
            + @"\s*至\s*" + Regex.Escape(endText) + "$";
        if (!Regex.IsMatch(displayed, expectedWindow, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            return new("time_structure_unrecognized", title, displayed, null);

        var start = ParseTime(startText);
        var end = ParseTime(endText);
        if (start is null || end is null) return new("invalid_time", title, displayed, null);
        if (start.Year.HasValue && end.Year.HasValue
            && CompareFullTimes(start, end) > 0)
            return new("invalid_time_window", title, displayed, null);

        return new("parsed_time_window", title, displayed, new(startText, endText, start, end));
    }

    private static WorkDisplayedTime? ParseTime(string text)
    {
        var match = DisplayedTime.Match(text);
        if (!match.Success) return null;
        int Number(string name) => int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
        int? year = match.Groups["year"].Success ? Number("year") : null;
        var month = Number("month");
        var day = Number("day");
        var hour = Number("hour");
        var minute = Number("minute");
        if (year is < 1 or > 9999 || month is < 1 or > 12
            || day < 1 || hour is < 0 or > 23 || minute is < 0 or > 59) return null;
        // A leap calendar validates a yearless Feb 29 as possible; it never supplies a missing year.
        if (day > DateTime.DaysInMonth(year ?? 2000, month)) return null;
        return new(year, month, day, hour, minute);
    }

    private static int CompareFullTimes(WorkDisplayedTime start, WorkDisplayedTime end)
    {
        // Comparison only; no local time zone or unobserved seconds are assigned to the output.
        var startDate = new DateOnly(start.Year!.Value, start.Month, start.Day);
        var endDate = new DateOnly(end.Year!.Value, end.Month, end.Day);
        var dateOrder = startDate.CompareTo(endDate);
        return dateOrder != 0 ? dateOrder
            : (start.Hour * 60 + start.Minute).CompareTo(end.Hour * 60 + end.Minute);
    }

    private static string NormalizeText(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty
        : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
