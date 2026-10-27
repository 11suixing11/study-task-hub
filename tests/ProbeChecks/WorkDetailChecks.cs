using System.Net;
using AccessProbe;

public static class WorkDetailChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            count++;
            if (!condition) throw new Exception($"Work-detail check {count} failed: {name}");
        }

        // Synthetic title and dates mirror the inspected structure; no task content or signed URL is stored.
        static string Window(string start = "11-02 09:15", string end = "11-12 23:59") =>
            $"<p class=\"rightBord\">作答时间: <em>{WebUtility.HtmlEncode(start)}</em> 至 <em>{WebUtility.HtmlEncode(end)}</em></p>";
        static string Detail(string window) =>
            "<div class=\"detailsHead\"><h2 class=\"mark_title\">示例作业</h2>"
            + "<div class=\"infoHead fl\"><span>题量: 12</span><span>满分: 100</span><div>"
            + window + "</div></div></div>";

        var result = WorkDetailParser.Parse(Detail(Window()));
        Check(result.Outcome == "parsed_time_window" && result.Title == "示例作业", "observed header and window are parsed");
        Check(result.AnswerWindow is { Start: { Year: null, Month: 11, Day: 2, Hour: 9, Minute: 15 } },
            "yearless start components stay yearless");
        Check(result.AnswerWindow is { End: { Year: null, Month: 11, Day: 12, Hour: 23, Minute: 59 } },
            "yearless end components stay yearless");
        Check(result.AnswerWindow is { Resolution: "year_unconfirmed", DeadlineUtc: null },
            "current system year and time zone are not used to invent a UTC deadline");
        Check(result.AnswerWindow is { StartText: "11-02 09:15", EndText: "11-12 23:59" },
            "displayed endpoint text is preserved");

        var fragment = WorkDetailParser.Parse(Window());
        Check(fragment.Outcome == "parsed_time_window" && fragment.Title.Length == 0,
            "the inspected p fragment can be parsed without a whole page");
        var compactFragment = WorkDetailParser.Parse(
            "<div style=\"margin-top: 10px;margin-left: -9px;\">"
            + "<p class=\"rightBord\">作答时间:<em>11-02 09:15</em>至<em>11-12 23:59</em></p></div>");
        Check(compactFragment.AnswerWindow == result.AnswerWindow,
            "response fragment without spaces between text and em tags preserves both endpoints");
        var spacing = WorkDetailParser.Parse(Window().Replace("作答时间:", "作答时间：\n", StringComparison.Ordinal)
            .Replace("11-02 09:15", "11-02&nbsp;09:15", StringComparison.Ordinal));
        Check(spacing.AnswerWindow?.Start == result.AnswerWindow?.Start, "HTML whitespace and colon variants preserve dates");
        var nested = WorkDetailParser.Parse(Window().Replace("<em>", "<em><span>", StringComparison.Ordinal)
            .Replace("</em>", "</span></em>", StringComparison.Ordinal));
        Check(nested.Outcome == "parsed_time_window", "nested endpoint text is supported");

        var full = WorkDetailParser.Parse(Detail(Window("2026-11-02 09:15", "2026-11-12 23:59")));
        Check(full.AnswerWindow is { Start: { Year: 2026 }, End: { Year: 2026 }, Resolution: "time_zone_unconfirmed", DeadlineUtc: null },
            "explicit years are retained while time zone remains unresolved");
        var mixed = WorkDetailParser.Parse(Window("2026-11-02 09:15", "11-12 23:59"));
        Check(mixed.AnswerWindow is { Start: { Year: 2026 }, End: { Year: null }, Resolution: "year_unconfirmed" },
            "one explicit year is not silently propagated to the other endpoint");
        var crossYear = WorkDetailParser.Parse(Window("12-31 22:00", "01-01 01:00"));
        Check(crossYear.AnswerWindow is { Start: { Year: null }, End: { Year: null }, DeadlineUtc: null },
            "yearless cross-year window is retained without guessing either year");
        Check(WorkDetailParser.Parse(Window("2026-11-12 23:59", "2026-11-02 09:15")).Outcome == "invalid_time_window",
            "reversed complete dates are not used as a valid window");
        Check(WorkDetailParser.Parse(Window("02-29 09:15", "03-01 09:15")).Outcome == "parsed_time_window",
            "Feb 29 remains possible when the year is unknown");

        foreach (var badStart in new[] { "11-31 09:15", "13-02 09:15", "11-02 24:00", "11-02 09:60", "2025-02-29 09:15", "11-02 09:15:30", "未知" })
            Check(WorkDetailParser.Parse(Window(badStart)).Outcome == "invalid_time", "invalid or unsupported time is reported");
        Check(WorkDetailParser.Parse(Window(end: "11-31 23:59")).Outcome == "invalid_time", "invalid end is also rejected");

        var missingEndpoint = WorkDetailParser.Parse("<p class=\"rightBord\">作答时间: <em>11-02 09:15</em></p>");
        Check(missingEndpoint.Outcome == "time_structure_unrecognized" && missingEndpoint.AnswerWindow is null,
            "missing endpoint is not inferred");
        Check(WorkDetailParser.Parse(Window().Replace(" 至 ", " 至少 ", StringComparison.Ordinal)).Outcome == "time_structure_unrecognized",
            "unrecognized window wording is not interpreted as a deadline");
        Check(WorkDetailParser.Parse(Window() + Window()).Outcome == "ambiguous_answer_window",
            "multiple windows are not silently resolved by choosing the first");
        Check(WorkDetailParser.Parse(Window().Replace("作答时间", "提交时间", StringComparison.Ordinal)).Outcome == "schema_unrecognized",
            "a submission timestamp is not treated as the permitted answer window");
        var unrelated = Detail(Window()) + "<div class=\"questions\">" + Window("01-01 00:00", "02-01 00:00") + "</div>";
        Check(WorkDetailParser.Parse(unrelated).AnswerWindow?.Start.Month == 11,
            "question content outside the detail header does not replace the answer window");
        Check(WorkDetailParser.Parse("<html><form>请登录</form></html>").Outcome == "schema_unrecognized",
            "login HTML is not an empty or valid task detail");
        const string submittedPrompt = "<div class='workHead'></div><div class='workCen'><div class='subSucess'>提交成功</div>"
            + "<p class='dpy'>教师限制查看</p><p class='subTime'>提交时间：2026-10-20 12:00</p></div>";
        var prompt = WorkDetailParser.Parse(submittedPrompt);
        Check(prompt.Outcome == "submitted_prompt_without_time_window" && prompt.AnswerWindow is null
            && prompt.DisplayedWindowText.Length == 0, "submitted prompt keeps submission time out of the deadline");
        Check(WorkDetailParser.Parse(submittedPrompt.Replace("提交时间", "截止时间", StringComparison.Ordinal)).Outcome == "schema_unrecognized",
            "a changed prompt is not silently treated as a recognized metadata result");
        Check(WorkDetailParser.Parse("<p class='subTime'>提交时间：2026-10-20 12:00</p>").Outcome == "schema_unrecognized",
            "unrelated submission text alone does not establish a work prompt");
        Check(WorkDetailParser.Parse(new string('x', WorkDetailParser.MaximumBytes + 1)).Outcome == "response_too_large",
            "oversized HTML is rejected");

        Console.WriteLine($"PASS: {count} synthetic work-detail and year-ambiguity checks; no account accessed.");
    }
}
