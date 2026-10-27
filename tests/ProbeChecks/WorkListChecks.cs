using System.Net;
using System.Text.Json;
using AccessProbe;

public static class WorkListChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            count++;
            if (!condition) throw new Exception($"Work-list check {count} failed: {name}");
        }

        // All IDs and signatures below are synthetic; no real account HTML is stored in this fixture.
        const string page = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task";
        const string query = "?courseId=101&classId=202&cpi=303&workId=404&answerId=505&enc=SYNTHETIC_ONLY";
        const string target = page + query;
        static string Row(string address, string title = "示例作业", string status = "已完成", string time = "剩余98小时11分钟") =>
            $"""
            <li onclick="goTask(this);" data="{WebUtility.HtmlEncode(address)}" role="link">
                <div class="right-content">
                    <p class="overHidden2 fl">{WebUtility.HtmlEncode(title)}</p>
                    <p class="status fl">{WebUtility.HtmlEncode(status)}</p>
                    <a href="https://stat2-ans.chaoxing.com/study-knowledge/ans?relationId=404" class="insightBtn">智能分析</a>
                </div>
                <div class="time notOver"><img src="/endTime.png">{WebUtility.HtmlEncode(time)}</div>
            </li>
            """;

        var result = WorkListParser.Parse(Row(target, "示例作业 & 数据练习"));
        Check(result.Outcome == "parsed_partial" && result.Tasks.Count == 1, "observed row is parsed");
        var task = result.Tasks[0];
        Check(task.Title == "示例作业 & 数据练习", "title entities are decoded");
        Check(task.PlatformStatusText == "已完成", "platform status remains separate text");
        Check(task.DisplayedTimeText == "剩余98小时11分钟" && task.DeadlineUtc is null,
            "remaining time is not converted to an invented absolute deadline");
        Check(task.Location?.PageUri.OriginalString == target, "data URL is preserved, not the analysis href");
        Check(task.Location is { CourseId: "101", ClassId: "202", WorkId: "404", AnswerId: "505" },
            "observed locator fields are extracted");
        Check(task.LinkOutcome == "observed_from_list" && result.InvalidLinks == 0, "resolved link is reported");
        Check(result.Coverage == "provided_html_only_not_complete", "parsed rows do not claim complete coverage");
        Check(!JsonSerializer.Serialize(result).Contains("SYNTHETIC_ONLY", StringComparison.Ordinal),
            "signed URL is excluded from diagnostic serialization");
        Check(!task.ToString().Contains("SYNTHETIC_ONLY", StringComparison.Ordinal), "record display omits signature");

        var contaminated = WorkListParser.Parse(Row(target + "\" onkeydown=\"goTask(this);\""));
        Check(contaminated.Tasks.Count == 1 && contaminated.Tasks[0].Location is null && contaminated.InvalidLinks == 1,
            "HTML attributes pasted into a URL do not become a usable task link");

        foreach (var unsafeAddress in new[]
        {
            target.Replace("https://", "http://", StringComparison.Ordinal),
            target.Replace("mooc1.chaoxing.com", "mooc1.chaoxing.com.example", StringComparison.Ordinal),
            target.Replace("mooc1.chaoxing.com", "mooc1.xuexitong.com", StringComparison.Ordinal),
            target.Replace("https://", "https://user:pass@", StringComparison.Ordinal),
            target.Replace("mooc1.chaoxing.com", "mooc1.chaoxing.com:9443", StringComparison.Ordinal),
            target.Replace("/mooc-ans/mooc2/work/task", "/study-knowledge/ans", StringComparison.Ordinal),
            target + "#fragment",
            "javascript:goTask(this)"
        })
        {
            var parsed = WorkListParser.Parse(Row(unsafeAddress));
            Check(parsed.Tasks.Count == 1 && parsed.Tasks[0].Location is null && parsed.InvalidLinks == 1,
                "unsupported target stays visible with unresolved link");
        }

        foreach (var invalidQuery in new[]
        {
            query.Replace("workId=404&", string.Empty, StringComparison.Ordinal),
            query.Replace("courseId=101", "courseId=other", StringComparison.Ordinal),
            query.Replace("enc=SYNTHETIC_ONLY", "enc=", StringComparison.Ordinal),
            query + "&workId=999",
            query + "&%65nc=other",
            query.Replace("answerId=505", "answerId=bad", StringComparison.Ordinal)
        })
            Check(WorkListParser.Parse(Row(page + invalidQuery)).Tasks[0].Location is null,
                "missing, invalid or ambiguous locator is unresolved");

        var pending = WorkListParser.Parse(Row(target.Replace("answerId=505&", string.Empty, StringComparison.Ordinal),
            status: "未完成"));
        Check(pending.Tasks[0].Location is { AnswerId: null } && pending.Tasks[0].PlatformStatusText == "未完成",
            "optional answer identifier is handled without inferring user state");

        var encodedTarget = target.Replace("SYNTHETIC_ONLY", "SYNTHETIC%2BONLY%26VALUE", StringComparison.Ordinal);
        Check(WorkListParser.Parse(Row(encodedTarget)).Tasks[0].Location?.PageUri.OriginalString == encodedTarget,
            "opaque signature encoding is not rewritten");

        var duplicates = WorkListParser.Parse(Row(target) + Row(target.Replace("workId=404", "workId=405", StringComparison.Ordinal)));
        Check(duplicates.Tasks.Count == 2 && duplicates.Tasks[0].Location?.WorkId != duplicates.Tasks[1].Location?.WorkId,
            "same-title tasks are not merged");
        var noTitle = WorkListParser.Parse(Row(target, title: "  "));
        Check(noTitle.Outcome == "schema_unrecognized" && noTitle.MissingTitles == 1 && noTitle.Tasks.Count == 0,
            "missing row title is reported");
        var unknownMarkup = WorkListParser.Parse(Row(target).Replace("goTask(this);", "otherHandler(this);", StringComparison.Ordinal));
        Check(unknownMarkup.Outcome == "schema_unrecognized", "unknown click handler is not treated as verified markup");
        var missingLabels = WorkListParser.Parse(Row(target, status: "", time: ""));
        Check(missingLabels.Tasks[0].PlatformStatusText.Length == 0 && missingLabels.Tasks[0].DeadlineUtc is null,
            "missing status and deadline are not inferred");
        var login = WorkListParser.Parse("<html><body><form>请登录</form></body></html>");
        Check(login.Outcome == "schema_unrecognized" && login.Coverage == "not_verified",
            "login HTML is not a confirmed empty work list");
        Check(WorkListParser.Parse(new string('x', WorkListParser.MaximumBytes + 1)).Outcome == "response_too_large",
            "oversized supplied HTML is rejected");

        Console.WriteLine($"PASS: {count} synthetic work-list HTML and task-link checks; no account accessed.");
    }
}
