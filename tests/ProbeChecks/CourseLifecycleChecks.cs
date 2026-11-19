using System.Text.Json;
using System.Text.Json.Nodes;
using AccessProbe;
using Microsoft.Data.Sqlite;

internal static class CourseLifecycleChecks
{
    private static int checks;
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(8));

    public static void Run()
    {
        Rules(); ParserEvidence(); Storage(); CrossPage().GetAwaiter().GetResult();
        Console.WriteLine($"PASS: {checks} course lifecycle, trustworthy expiry, catalog renewal and retained-selection checks (synthetic only).");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Course lifecycle: " + label);
        checks++;
    }

    private static RecordingCourse Course(CourseLifecycle? lifecycle = null) => new(
        CourseRecordingRules.MoocKey("9001", "1001", "2001"), "2019 年历史课程", "已结束（仅名称文字）", false)
        { Lifecycle = lifecycle ?? new() };

    private static void Rules()
    {
        var unknown = Course();
        Check(!CourseLifecycleRules.IsEnded(unknown, Now), "course name, detail and unavailable catalog entry do not prove expiry");
        Check(CourseLifecycleRules.MatchesFilter(unknown, CourseListFilter.HideEnded, Now), "unknown courses stay visible by default");
        Check(!CourseLifecycleRules.MatchesFilter(unknown, CourseListFilter.EndedOnly, Now), "unknown course excluded from ended-only");
        var ended = Course(CourseLifecycleRules.FromOfficialState(CourseLifecycleState.Ended));
        Check(CourseLifecycleRules.IsEnded(ended, Now), "official ended state proves expiry");
        Check(!CourseLifecycleRules.MatchesFilter(ended, CourseListFilter.HideEnded, Now), "ended course hidden in default view");
        Check(CourseLifecycleRules.MatchesFilter(ended, CourseListFilter.All, Now), "show-all restores ended course");
        Check(CourseLifecycleRules.MatchesFilter(ended, CourseListFilter.EndedOnly, Now), "ended-only includes ended course");
        var date = Course(CourseLifecycleRules.FromOfficialEnd(Now));
        Check(!CourseLifecycleRules.IsEnded(date, Now.AddTicks(-1)), "course remains visible before exact end instant");
        Check(CourseLifecycleRules.IsEnded(date, Now), "course expires at official end instant");
        Check(CourseLifecycleRules.IsEnded(date, Now.ToOffset(TimeSpan.FromHours(-7))), "expiry compares instants across time zones");
        Check(CourseLifecycleRules.Explanation(date, Now).Contains("课程已于", StringComparison.Ordinal), "expired date explanation provided");
        Check(CourseLifecycleRules.Explanation(date, Now.AddMinutes(-1)).Contains("课程结束于", StringComparison.Ordinal), "future end explanation provided");
        foreach (var state in new[] { CourseLifecycleState.Ongoing, CourseLifecycleState.Upcoming })
        {
            var renewed = Course(CourseLifecycleRules.FromOfficialState(state) with
                { EndUtc = Now.AddYears(-1), EndEvidence = CourseLifecycleEvidence.OfficialEndTime });
            Check(!CourseLifecycleRules.IsEnded(renewed, Now), "fresh active state overrides obsolete end date");
        }
        var untrusted = Course(new() { EndUtc = Now.AddDays(-1) });
        Check(!CourseLifecycleRules.IsValid(untrusted.Lifecycle) && !CourseLifecycleRules.IsEnded(untrusted, Now), "date without official evidence is not used");
        Check(!CourseLifecycleRules.IsEnded(Course(new() { State = CourseLifecycleState.Ended }), Now), "ended state without official evidence is not used");
        Check(!CourseLifecycleRules.IsValid(new() { State = (CourseLifecycleState)99 }), "unknown enum rejected");
        Check(!CourseLifecycleRules.IsValid(CourseLifecycleRules.FromOfficialEnd(new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero))), "unsupported dates rejected");
        var oldJson = JsonSerializer.Serialize(new { unknown.Key, unknown.Name, unknown.Detail, unknown.Available });
        var legacy = JsonSerializer.Deserialize<RecordingCourse>(oldJson)!;
        Check(CourseLifecycleRules.IsValid(legacy.Lifecycle) && !CourseLifecycleRules.IsEnded(legacy, Now), "legacy JSON defaults to unknown lifecycle");
    }

    private static string MoocCatalog(object? end, bool includeEnd = true)
    {
        var term = new Dictionary<string, object?> { ["id"] = 2001, ["courseId"] = 1001 };
        if (includeEnd) term["endTime"] = end;
        return JsonSerializer.Serialize(new { code = 0, result = new {
            list = new[] { new { id = 1001, name = "2019 年课程", mode = 0, endTime = Now.AddYears(-5).ToUnixTimeMilliseconds(),
                learnedCount = 100, termPanel = term, schoolPanel = new { shortName = "PKU" } } },
            query = new { totlePageCount = 1, totleCount = 1, pageIndex = 1 }
        } });
    }

    private static void ParserEvidence()
    {
        var parsed = MoocParser.Catalog(MoocCatalog(Now.AddDays(-1).ToUnixTimeMilliseconds()));
        Check(parsed.Outcome == "ok" && parsed.Courses[0].EndUtc == Now.AddDays(-1), "MOOC official term end timestamp retained");
        Check(CourseLifecycleRules.IsEnded(CourseRecordingRules.Mooc("9001", parsed.Courses[0]).Course, Now), "MOOC term date reaches recording catalog");
        foreach (var invalid in new object?[] { null, 0L, -1L, Now.ToUnixTimeSeconds(), 1.5, "昨天", "2026-11-01", long.MaxValue })
        {
            var missing = MoocParser.Catalog(MoocCatalog(invalid));
            Check(missing.Courses.Count == 1 && missing.Courses[0].EndUtc is null
                && !CourseLifecycleRules.IsEnded(CourseRecordingRules.Mooc("9001", missing.Courses[0]).Course, Now),
                "invalid course term date is unknown without losing course");
        }
        Check(!CourseLifecycleRules.IsEnded(CourseRecordingRules.Mooc("9001", MoocParser.Catalog(MoocCatalog(null, false)).Courses[0]).Course, Now),
            "row task-like date and learned count do not substitute course term end");
        var conflict = JsonNode.Parse(MoocCatalog(Now.AddDays(-1).ToUnixTimeMilliseconds()))!;
        var duplicate = conflict["result"]!["list"]![0]!.DeepClone(); duplicate["termPanel"]!["endTime"] = Now.AddDays(1).ToUnixTimeMilliseconds();
        conflict["result"]!["list"]!.AsArray().Add(duplicate); conflict["result"]!["query"]!["totleCount"] = 2;
        var conflictingCatalog = MoocParser.Catalog(conflict.ToJsonString());
        Check(conflictingCatalog.Courses.Count == 1 && conflictingCatalog.Courses[0].EndUtc is null,
            "conflicting duplicate course dates stay visible instead of choosing past end");
        foreach (var state in new[] { 1, 2, 3 })
        {
            var json = JsonSerializer.Serialize(new { success = true, data = new {
                count = 1, product_list = new[] { new { classroom_id = 101, sign = "product", course_sign = "course", name = "合成课程", status = state } }
            } });
            var page = XuetangParser.Catalog(json, state);
            Check(page.Outcome == "ok" && CourseLifecycleRules.IsEnded(CourseRecordingRules.Xuetang("9001", page.Courses[0]).Course, Now) == (state == 3),
                "Xuetang official catalog state is mapped independently");
        }
        var stowed = CourseRecordingRules.Xuetang("9001", new("已收起", "101", "product", "course", 1, true)).Course;
        Check(!CourseLifecycleRules.IsEnded(stowed, Now), "stowed active course is not ended");
        string XuetangConflict(int first, int second) => JsonSerializer.Serialize(new { success = true, data = new
        {
            count = 2, product_list = new[] {
                new { classroom_id = "101", sign = "product", course_sign = "course", name = "冲突课程", status = first },
                new { classroom_id = "101", sign = "product", course_sign = "course", name = "冲突课程", status = second }
            }
        } });
        foreach (var statuses in new[] { (3, 1), (1, 3) })
        {
            var conflictPage = XuetangParser.Catalog(XuetangConflict(statuses.Item1, statuses.Item2), 0, true);
            Check(conflictPage.Outcome == "partial_catalog" && conflictPage.DuplicateRows == 1 && conflictPage.Courses.Count == 1
                && conflictPage.Courses[0].Status == statuses.Item1, "conflicting stowed states retain existing identity and duplicate counts");
            Check(CourseLifecycleRules.State(CourseRecordingRules.Xuetang("9001", conflictPage.Courses[0]).Course, Now) == CourseLifecycleState.Unknown,
                "conflicting stowed course state is unknown instead of automatically hidden");
            var bucketConflict = XuetangParser.Catalog(XuetangConflict(statuses.Item1, statuses.Item2), 3);
            Check(bucketConflict.Outcome == "partial_catalog" && bucketConflict.Courses.Count == 1 && bucketConflict.Courses[0].Status == 3
                && !CourseLifecycleRules.IsEnded(CourseRecordingRules.Xuetang("9001", bucketConflict.Courses[0]).Course, Now),
                "mismatched status in same course bucket suppresses ended evidence in both row orders");
        }
        var smartJson = JsonSerializer.Serialize(new { status = 200, rt = new[] { new {
            courseId = "101", classId = "202", courseName = "合成智慧课程", newFlag = true, endTime = "2026-11-01 23:59:59"
        } } });
        var smart = MetadataPlatformParser.ZhihuishuSmartCatalog(smartJson, 3).Courses[0];
        Check(smart.EndUtc == new DateTimeOffset(2026, 11, 1, 23, 59, 59, TimeSpan.FromHours(8)), "wisdom course end keeps official China time");
        Check(CourseLifecycleRules.IsEnded(CourseRecordingRules.Metadata("zhihuishu", "9001", smart).Course, Now), "wisdom official course date reaches recording catalog");
        var withoutDate = MetadataPlatformParser.ZhihuishuSmartCatalog(smartJson.Replace("2026-11-01 23:59:59", ""), 4).Courses[0];
        Check(!CourseLifecycleRules.IsEnded(CourseRecordingRules.Metadata("zhihuishu", "9001", withoutDate).Course, Now),
            "wisdom completed bucket alone does not prove course term ended");
    }

    private static void Storage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "course-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "tasks.db"); var store = new TaskStore(path);
        var ended = Course(CourseLifecycleRules.FromOfficialEnd(Now.AddDays(-1)));
        var initial = store.ObserveRecordingCourses("mooc", [new(ended, _ => false)], true, Now);
        var selected = store.SaveCourseRecording("mooc", false, [ended.Key], true, initial.Revision, Now);
        var manual = store.Create(new("已逾期作业", "mooc", "没有课程结束信息", DeadlineKind.Known, null, Now.AddDays(-1), null, null, "", "", null), Now);
        var before = store.LoadCourseRecording("mooc"); var tasksBefore = JsonSerializer.Serialize(store.LoadTasks());
        var visible = before.Courses.Where(course => CourseLifecycleRules.MatchesFilter(course, CourseListFilter.HideEnded, Now)).ToArray();
        Check(visible.Length == 0 && CourseRecordingRules.IsSelected(before, ended.Key), "filtering hides course without removing selected key");
        Check(JsonSerializer.Serialize(store.LoadTasks()) == tasksBefore && store.LoadTasks().Any(task => task.Id == manual.Id), "filter does not alter history or overdue tasks");
        Check(store.LoadCourseRecording("mooc").Revision == before.Revision, "filter does not mutate stored course revision");
        var loaded = new TaskStore(path).LoadCourseRecording("mooc");
        Check(CourseLifecycleRules.IsEnded(loaded.Courses[0], Now) && loaded.Courses[0].Lifecycle.ObservedAtUtc == Now, "ended evidence and observation survive reload");
        var renewed = store.ObserveRecordingCourses("mooc", [new(ended with { Lifecycle = new() }, _ => false)], false, Now.AddMinutes(1));
        Check(!CourseLifecycleRules.IsEnded(renewed.Courses[0], Now) && CourseRecordingRules.IsSelected(renewed, ended.Key), "fresh unknown clears old date and preserves selection");
        var extended = store.ObserveRecordingCourses("mooc", [new(ended with { Lifecycle = CourseLifecycleRules.FromOfficialEnd(Now.AddMonths(1)) }, _ => false)], true, Now.AddMinutes(2));
        Check(!CourseLifecycleRules.IsEnded(extended.Courses[0], Now), "later official end makes renewed course visible");
        var absent = store.ObserveRecordingCourses("mooc", [], true, Now.AddMinutes(3));
        Check(!absent.Courses[0].Available && !CourseLifecycleRules.IsEnded(absent.Courses[0], Now), "catalog absence is not expiry");
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open(); using var read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM settings WHERE key='course-recording:mooc';";
            var json = JsonNode.Parse((string)read.ExecuteScalar()!)!;
            foreach (var course in json["Courses"]!.AsArray()) course!.AsObject().Remove("Lifecycle");
            using var write = connection.CreateCommand(); write.CommandText = "UPDATE settings SET value=$value WHERE key='course-recording:mooc';";
            write.Parameters.AddWithValue("$value", json.ToJsonString()); write.ExecuteNonQuery();
        }
        var legacy = new TaskStore(path).LoadCourseRecording("mooc");
        Check(legacy.SelectedCourseKeys.Contains(ended.Key) && legacy.Courses.All(course => CourseLifecycleRules.IsValid(course.Lifecycle)), "existing stored catalog without lifecycle loads compatibly");
        Check(legacy.Courses.All(course => !CourseLifecycleRules.IsEnded(course, Now)), "legacy catalog never inferred expired from title or availability");
    }

    private static async Task CrossPage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "course-lifecycle-cross-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var store = new TaskStore(Path.Combine(directory, "tasks.db"));
        var reader = new CrossPageReader(); var summary = await new MoocSynchronizer(store, reader, "9001").RunAsync(catalogOnly: true);
        var recording = store.LoadCourseRecording("mooc");
        var duplicateKey = CourseRecordingRules.MoocKey("9001", "1001", "2001");
        Check(summary.CourseListCovered && summary.CatalogPages == 2 && summary.DuplicateRows == 1 && reader.Requests == 2,
            "cross-page course overlap finishes read-only catalog without extra requests");
        Check(recording.Courses.Single(course => course.Key == duplicateKey).Lifecycle.EndUtc is null
            && !CourseLifecycleRules.IsEnded(recording.Courses.Single(course => course.Key == duplicateKey), Now),
            "conflicting cross-page course end dates are unknown and visible");
        Check(recording.Courses.Count == 3 && !recording.SelectionConfirmed && store.LoadTasks().Count == 0,
            "cross-page date ambiguity retains courses and requires normal course confirmation");
    }

    private sealed class CrossPageReader : IMoocMetadataReader
    {
        public int Requests { get; private set; }
        public Task<MoocResponse> ReadAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken cancellation)
        {
            Requests++;
            if (path != MoocPlatform.CourseRpc) throw new InvalidOperationException("Course test requested task metadata.");
            var page = int.Parse(fields["p"]); var list = new List<object>();
            foreach (var course in page == 1 ? new[] { (1001, 2001), (1002, 2002) } : new[] { (1001, 2001), (1003, 2003) })
                list.Add(new { id = course.Item1, name = "合成课程" + course.Item1, mode = 0, schoolPanel = new { shortName = "PKU" },
                    termPanel = new { id = course.Item2, courseId = course.Item1, endTime = (page == 1 ? Now.AddDays(-1) : Now.AddDays(30)).ToUnixTimeMilliseconds() } });
            var json = JsonSerializer.Serialize(new { code = 0, result = new { list, query = new { totlePageCount = 2, totleCount = 4, pageIndex = page } } });
            return Task.FromResult(new MoocResponse("ok", 200, json));
        }
    }
}
