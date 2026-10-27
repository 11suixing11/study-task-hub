using System.Text.Json;
using AccessProbe;

public static class WorkPageSnapshotChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            count++;
            if (!condition) throw new Exception($"Work snapshot check {count} failed: {name}");
        }

        // Synthetic metadata only: neither a real account nor a real task URL is used.
        const string target = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task"
            + "?courseId=101&classId=202&cpi=303&workId=404&answerId=505&enc=SYNTHETIC%2BONLY%26VALUE";
        var row = $"<li data=\"{System.Net.WebUtility.HtmlEncode(target)}\" onclick=\"goTask(this);\">"
            + "<div class=\"right-content\"><p class=\"overHidden2\">示例 &amp; 作业</p>"
            + "<p class=\"status\">已完成</p></div><div class=\"time\">剩余48小时</div></li>";
        var list = WorkPageSnapshotReader.Parse(JsonSerializer.Serialize(new { kind = "list", markup = row, rowLimitReached = false }));
        Check(list is { Outcome: "parsed_partial", Detail: null, RowLimitReached: false }
            && list.List?.Tasks.Count == 1, "list snapshot dispatches to the list parser");
        Check(list.List!.Tasks[0].Title == "示例 & 作业" && list.List.Tasks[0].PlatformStatusText == "已完成",
            "snapshot JSON and HTML preserve platform metadata");
        Check(list.List.Tasks[0].Location?.PageUri.OriginalString == target, "signed target survives JSON and HTML exactly");
        Check(list.List.Tasks[0].DeadlineUtc is null, "relative label is not an exact deadline");
        Check(!JsonSerializer.Serialize(list).Contains("SYNTHETIC", StringComparison.Ordinal), "result serialization omits signed URL");
        var limited = WorkPageSnapshotReader.Parse(JsonSerializer.Serialize(new { kind = "list", markup = row, rowLimitReached = true }));
        Check(limited.RowLimitReached && limited.List?.Tasks.Count == 1, "truncated snapshot retains coverage warning");
        var missing = WorkPageSnapshotReader.Parse(JsonSerializer.Serialize(new
        {
            kind = "list", markup = row + row.Replace("示例 &amp; 作业", " ", StringComparison.Ordinal)
        }));
        Check(missing.List is { MissingTitles: 1 } && missing.List.Tasks.Count == 1, "missing title remains observable as partial coverage");
        var badLink = WorkPageSnapshotReader.Parse(JsonSerializer.Serialize(new
        {
            kind = "list", markup = row.Replace("mooc1.chaoxing.com", "example.invalid", StringComparison.Ordinal)
        }));
        Check(badLink.List is { InvalidLinks: 1 } && badLink.List.Tasks[0].Location is null, "unresolved link stays visible");

        const string detailHtml = "<div class=\"detailsHead\"><h2 class=\"mark_title\">示例作业</h2>"
            + "<div class=\"infoHead\"><p class=\"rightBord\">作答时间:<em>11-02 09:15</em>至<em>11-12 23:59</em></p></div></div>";
        var detail = WorkPageSnapshotReader.Parse(JsonSerializer.Serialize(new { kind = "detail", markup = detailHtml }));
        Check(detail is { Outcome: "parsed_time_window", List: null, RowLimitReached: false }, "detail snapshot dispatches to detail parser");
        Check(detail.Detail?.AnswerWindow is { Start: { Year: null }, End: { Year: null }, DeadlineUtc: null },
            "yearless detail remains unresolved");
        Check(detail.Detail?.AnswerWindow is { StartText: "11-02 09:15", EndText: "11-12 23:59" }, "compact time label survives snapshot");

        foreach (var outcome in new[] { "unsupported_page", "page_loading", "not_found", "response_too_large", "ambiguous_detail" })
        {
            var result = WorkPageSnapshotReader.Parse(JsonSerializer.Serialize(new { kind = outcome }));
            Check(result.Outcome == outcome && result.List is null && result.Detail is null, "known failure is not an empty task list");
        }
        foreach (var malformed in new[] { "null", "[]", "\"text\"", "{", "{}", "{\"kind\":4}", "{\"kind\":\"unknown\"}",
            "{\"kind\":\"list\"}", "{\"kind\":\"detail\",\"markup\":4}" })
            Check(WorkPageSnapshotReader.Parse(malformed).Outcome == "snapshot_unrecognized", "malformed snapshot is rejected");
        Check(WorkPageSnapshotReader.Parse("{\"kind\":\"list\",\"markup\":\"<form>请登录</form>\"}").Outcome == "schema_unrecognized",
            "login markup does not become confirmed empty work");
        Check(WorkPageSnapshotReader.Parse(JsonSerializer.Serialize(new { kind = "list", markup = new string('x', WorkListParser.MaximumBytes + 1) })).Outcome
            == "response_too_large", "decoded metadata size limit is enforced");
        Check(WorkPageSnapshotReader.Parse(new string('x', 4 * WorkListParser.MaximumBytes + 1)).Outcome == "response_too_large",
            "serialized snapshot size limit is enforced");
        Console.WriteLine($"PASS: {count} synthetic loaded-page snapshot checks; no account accessed.");
    }
}
