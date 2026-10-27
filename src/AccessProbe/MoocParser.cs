using System.Globalization;
using System.Text.Json;

namespace AccessProbe;

public static class MoocParser
{
    public static string EnvelopeOutcome(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var code = Integer(Get(root, "code"));
            if (code == 0) return Has(root, "result") ? "ok" : "unrecognized_envelope";
            if (code == -2) return "login_required";
            if (code == -1002) return "csrf_rejected";
            return code is null ? "unrecognized_envelope" : "rpc_error";
        }
        catch (JsonException) { return "invalid_json"; }
    }

    public static MoocCatalogPage Catalog(string json)
    {
        var outcome = EnvelopeOutcome(json);
        if (outcome != "ok") return new(outcome, [], 0, null, null, null, 0, []);
        using var document = JsonDocument.Parse(json);
        var result = Get(document.RootElement, "result");
        var list = result.ValueKind == JsonValueKind.Array ? result
            : Has(result, "list") ? Get(result, "list") : Get(result, "result");
        if (list.ValueKind != JsonValueKind.Array) return new("unrecognized_catalog", [], 0, null, null, null, 0, []);
        var issues = new List<string>(); var duplicates = 0;
        var courses = new Dictionary<string, MoocCourse>(StringComparer.Ordinal);
        foreach (var row in list.EnumerateArray())
        {
            var term = Get(row, "termPanel"); var school = Get(row, "schoolPanel");
            var cid = Id(Get(row, "id")) ?? Id(Get(term, "courseId"));
            var tid = Id(Get(term, "id")) ?? Id(Get(row, "termId"));
            var name = Text(Get(row, "name")); var mode = Integer(Get(row, "mode"));
            var shortName = Text(Get(school, "shortName"));
            if (shortName.Length == 0) shortName = Text(Get(school, "schoolSN"));
            if (cid is null || tid is null || name.Length is 0 or > 200
                || Id(Get(term, "courseId")) is { } owner && owner != cid)
            { issues.Add("course_identity_unrecognized"); continue; }
            if (mode is not (0 or 10 or 15 or 20)) { issues.Add("course_mode_unrecognized"); continue; }
            if (!MoocPlatform.IsSchool(shortName)) issues.Add("course_route_unrecognized");
            var course = new MoocCourse(name, cid, tid, shortName, mode.Value);
            if (!courses.TryAdd(cid + ":" + tid, course)) duplicates++;
        }
        var paging = Has(result, "query") ? Get(result, "query") : Get(result, "pagination");
        if (paging.ValueKind != JsonValueKind.Object) paging = Get(document.RootElement, "pagination");
        var pages = Integer(Has(paging, "totlePageCount") ? Get(paging, "totlePageCount") : Get(paging, "totalPageCount"));
        var count = Integer(Has(paging, "totleCount") ? Get(paging, "totleCount") : Get(paging, "totalCount"));
        var index = Integer(Get(paging, "pageIndex"));
        if (pages is null or < 0 || count is null or < 0 || index is < 1) issues.Add("pagination_unrecognized");
        if (pages == 0 && list.GetArrayLength() > 0 || count == 0 && list.GetArrayLength() > 0) issues.Add("pagination_inconsistent");
        return new(issues.Count == 0 ? "ok" : "partial_catalog", courses.Values.ToArray(), list.GetArrayLength(),
            pages, count, index, duplicates, issues.Distinct().ToArray());
    }

    public static MoocTaskBatch Term(string json, string accountId, MoocCourse course)
    {
        var outcome = EnvelopeOutcome(json);
        if (outcome != "ok") return new(outcome, [], 0, []);
        if (!MoocPlatform.IsId(accountId)) return new("account_unrecognized", [], 0, []);
        using var document = JsonDocument.Parse(json);
        var term = Get(document.RootElement, "result");
        if (Has(term, "mocTermDto")) term = Get(term, "mocTermDto");
        if (term.ValueKind != JsonValueKind.Object || Id(Get(term, "id")) != course.TermId)
            return new("term_identity_mismatch", [], 0, []);
        if (!new[] { "chapters", "homeworks", "quizs", "exams" }.Any(name => Get(term, name).ValueKind == JsonValueKind.Array))
            return new("unrecognized_term", [], 0, []);
        var issues = new List<string>(); var duplicates = 0;
        var tasks = new Dictionary<string, MoocTask>(StringComparer.Ordinal);
        var exams = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        ReadArray(term, "homeworks", row => Assignment(row, "homework"));
        ReadArray(term, "quizs", row => Assignment(row, "quiz"));
        ReadArray(term, "chapters", chapter =>
        {
            if (chapter.ValueKind != JsonValueKind.Object) { issues.Add("chapter_unrecognized"); return; }
            var recognized = Has(chapter, "homeworks") || Has(chapter, "quizs") || Has(chapter, "exam") || Has(chapter, "lessons");
            if (!recognized) { issues.Add("chapter_unrecognized"); return; }
            ReadArray(chapter, "homeworks", row => Assignment(row, "homework"));
            ReadArray(chapter, "quizs", row => Assignment(row, "quiz"));
            if (Get(chapter, "exam").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) Exam(Get(chapter, "exam"));
            ReadArray(chapter, "lessons", lesson =>
            {
                var type = Integer(Get(lesson, "contentType"));
                if (type == 2) Assignment(lesson, "quiz");
                else if (type == 3) Assignment(lesson, "homework");
                else if (type != 1 || Has(lesson, "test") && Get(lesson, "test").ValueKind == JsonValueKind.Object)
                    issues.Add("lesson_type_unrecognized");
            });
        });
        ReadArray(term, "exams", Exam);
        foreach (var (_, variants) in exams)
        {
            var exam = MergeObjects(variants);
            var eid = Id(Get(exam, "id"))!; var title = Text(Get(exam, "name"));
            var examType = Integer(Get(exam, "type"));
            // The official legacy exam card concatenates an explicit null type as "examType=null".
            // An absent or malformed type remains unrecognized.
            var platformNullExamType = examType is null
                && variants.Any(row => Has(row, "type") && Get(row, "type").ValueKind == JsonValueKind.Null)
                && variants.All(row => Get(row, "type").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
            if (title.Length == 0) { issues.Add("task_title_unrecognized"); continue; }
            var parts = 0;
            foreach (var (name, vo, kind) in new[] { ("objectTest", "objectTestVo", "exam_object"), ("subjectTest", "subjectTestVo", "exam_subject") })
            {
                var component = Get(exam, name); var state = Get(exam, vo);
                if (component.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    && state.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
                var joined = MergeObjects([component, state]);
                var id = Id(Get(joined, "id"));
                if (id is null || Id(Get(joined, "examId")) is { } owner && owner != eid)
                { issues.Add("exam_identity_unrecognized"); continue; }
                var testKind = kind == "exam_subject" && Integer(Get(joined, "type")) == 7 ? "exam_oj" : kind;
                var partTitle = title + (kind == "exam_object" ? " · 客观题" : " · 主观题");
                Add(MakeTask(accountId, course, joined, exam, id, eid, testKind, partTitle, examType, issues, platformNullExamType)); parts++;
            }
            if (parts == 0)
            {
                // A new or unknown exam contract is visible, never represented by a fabricated answering route.
                Add(Make(exam, default, eid, eid, "exam", title, examType) with { Link = null });
                issues.Add("exam_route_unrecognized");
            }
        }
        return new(issues.Count == 0 ? "ok" : "partial_tasks", tasks.Values.ToArray(), duplicates, issues.Distinct().ToArray());

        void ReadArray(JsonElement parent, string key, Action<JsonElement> read)
        {
            var array = Get(parent, key);
            if (array.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
            if (array.ValueKind != JsonValueKind.Array) { issues.Add(key + "_unrecognized"); return; }
            foreach (var item in array.EnumerateArray()) read(item);
        }
        void Assignment(JsonElement row, string kind)
        {
            if (row.ValueKind != JsonValueKind.Object) { issues.Add("task_unrecognized"); return; }
            var test = Get(row, "test");
            if (test.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) test = row;
            var id = Id(Get(test, "id")) ?? Id(Get(row, "contentId"));
            var title = Text(Get(test, "name"));
            if (title.Length == 0) title = Text(Get(row, "name"));
            if (id is null || title.Length is 0 or > 190) { issues.Add("task_identity_unrecognized"); return; }
            if (kind == "homework" && Integer(Get(test, "type")) == 7) kind = "homework_oj";
            Add(Make(test, default, id, "", kind, title, null));
        }
        void Exam(JsonElement exam)
        {
            var id = Id(Get(exam, "id"));
            if (id is null || exam.ValueKind != JsonValueKind.Object) { issues.Add("exam_identity_unrecognized"); return; }
            if (exams.TryGetValue(id, out var variants)) { variants.Add(exam); duplicates++; }
            else exams.Add(id, [exam]);
        }
        MoocTask Make(JsonElement test, JsonElement parent, string id, string eid, string kind, string title, int? examType)
            => MakeTask(accountId, course, test, parent, id, eid, kind, title, examType, issues);
        void Add(MoocTask task)
        {
            var key = task.Kind + ":" + task.RemoteId + ":" + task.ExamId;
            if (tasks.TryGetValue(key, out var current))
            {
                duplicates++;
                var strongest = Rank(task) >= Rank(current) ? task : current;
                tasks[key] = strongest with
                {
                    DeadlineUtc = task.DeadlineUtc ?? current.DeadlineUtc,
                    TimeText = task.DeadlineUtc is not null ? task.TimeText : current.TimeText,
                    BeginUtc = task.BeginUtc ?? current.BeginUtc, Link = task.Link ?? current.Link,
                    ScoreText = task.ScoreText.Length > 0 ? task.ScoreText : current.ScoreText
                };
            }
            else tasks.Add(key, task);
        }
    }

    // This official RPC returns a result array, including [] when no new exams were assigned.
    public static MoocTaskBatch NewExams(string json, string accountId, MoocCourse course)
    {
        var outcome = EnvelopeOutcome(json);
        if (outcome != "ok") return new(outcome, [], 0, []);
        if (!MoocPlatform.IsId(accountId)) return new("account_unrecognized", [], 0, []);
        using var document = JsonDocument.Parse(json);
        var rows = Get(document.RootElement, "result");
        if (rows.ValueKind != JsonValueKind.Array) return new("unrecognized_new_exams", [], 0, []);
        var issues = new List<string>(); var duplicates = 0;
        var exams = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            var id = Id(Get(row, "id")); var title = Text(Get(row, "name"));
            if (id is null || title.Length is 0 or > 190) { issues.Add("new_exam_identity_unrecognized"); continue; }
            if (HasOwnerMismatch(row, "termId", course.TermId) || HasOwnerMismatch(row, "courseId", course.Id))
            { issues.Add("new_exam_owner_mismatch"); continue; }
            if (exams.TryGetValue(id, out var variants)) { variants.Add(row); duplicates++; }
            else exams.Add(id, [row]);
        }
        var tasks = exams.Select(pair =>
        {
            var exam = MergeObjects(pair.Value);
            return MakeTask(accountId, course, exam, default, pair.Key, pair.Key, "exam_new", Text(Get(exam, "name")), null, issues);
        }).ToArray();
        return new(issues.Count == 0 ? "ok" : "partial_tasks", tasks, duplicates, issues.Distinct().ToArray());

        static bool HasOwnerMismatch(JsonElement row, string field, string expected)
            => Get(row, field).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && Id(Get(row, field)) != expected;
    }

    public static MoocTaskBatch MergeTaskBatches(MoocTaskBatch term, MoocTaskBatch newExams)
    {
        // A placeholder from the legacy term RPC can describe the same new exam.
        var confirmed = newExams.Tasks.Select(task => task.ExamId).ToHashSet(StringComparer.Ordinal);
        var legacy = term.Tasks.Where(task => !(task.Kind.StartsWith("exam", StringComparison.Ordinal) && confirmed.Contains(task.ExamId))).ToArray();
        var tasks = legacy.Concat(newExams.Tasks).ToArray();
        var issues = term.Issues.Concat(newExams.Issues)
            .Where(issue => issue is not ("exam_route_unrecognized" or "task_route_unrecognized") || tasks.Any(task => task.Link is null))
            .Distinct().ToArray();
        return new(issues.Length == 0 && (term.Outcome is "ok" or "partial_tasks") && newExams.Outcome == "ok" ? "ok" : "partial_tasks",
            tasks, term.DuplicateRows + newExams.DuplicateRows + term.Tasks.Count - legacy.Length, issues);
    }

    private static MoocTask MakeTask(string accountId, MoocCourse course, JsonElement test, JsonElement parent,
        string id, string eid, string kind, string title, int? examType, List<string> issues, bool platformNullExamType = false)
    {
        var score = Decimal(Get(test, "userScore")); var total = Decimal(Get(test, "totalScore")) ?? Decimal(Get(parent, "totalScore"));
        var tries = Integer(Get(test, "usedTryCount"));
        var submit = Integer(Has(test, "userSubmitStatus") ? Get(test, "userSubmitStatus") : Get(test, "submitStatus"));
        TaskState? state; string status; string evidence;
        // NewExamCard uses usedTryCount as the submission count; userScore may default to zero before any submission.
        if (kind == "exam_new" && tries == 0) { state = TaskState.Pending; status = "未提交"; evidence = "usedTryCount=0"; }
        else if (score is >= 0 && (kind != "exam_new" || tries > 0 || submit == 2))
        { state = TaskState.Completed; status = "已批改"; evidence = "userScore=" + Number(score.Value) + (kind == "exam_new" ? "；usedTryCount=" + tries : ""); }
        else if (submit == 2 || tries > 0) { state = TaskState.Completed; status = "已提交"; evidence = submit == 2 ? "userSubmitStatus=2" : "usedTryCount=" + tries; }
        else if (submit == 1) { state = TaskState.Pending; status = "草稿 / 未提交"; evidence = "userSubmitStatus=1"; }
        else if (tries == 0) { state = TaskState.Pending; status = "未提交"; evidence = "usedTryCount=0"; }
        else { state = null; status = "状态待核对"; evidence = "unknown"; }
        var deadlineValue = Has(test, "deadline") && Get(test, "deadline").ValueKind != JsonValueKind.Null
            ? Get(test, "deadline") : Get(parent, "deadline");
        var deadline = Date(deadlineValue);
        if (deadlineValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            && Text(deadlineValue) != "0" && deadline is null) issues.Add("deadline_unrecognized");
        var begin = Date(Get(test, "releaseTime")) ?? Date(Get(parent, "startTime"));
        if (begin is not null && deadline is not null && begin > deadline) { issues.Add("time_range_unrecognized"); begin = null; }
        var link = MoocTaskNavigation.Create(course, kind, id, eid, examType, platformNullExamType);
        if (link is null) issues.Add("task_route_unrecognized");
        var explanation = state is null ? "平台未返回明确提交状态、提交次数或成绩，请在官方任务页面核对。"
            : state == TaskState.Completed ? "依据官方提交状态或成绩记录已提交；如仍有互评要求，请在官方页面完成。"
            : "依据官方未提交或草稿状态记录待办。";
        var timeText = deadline is not null ? "deadline=" + Text(deadlineValue) + "；截止 "
            + deadline.Value.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "平台未提供可确认的任务截止时间";
        return new(accountId, course.Id, course.TermId, id, eid, kind, title, course.Name, state,
            status, evidence, explanation, begin, deadline, timeText,
            score is >= 0 && state == TaskState.Completed ? Number(score.Value) + (total is >= 0 ? "/" + Number(total.Value) : "") : "", link);
    }

    private static int Rank(MoocTask task) => task.ScoreText.Length > 0 ? 3 : task.State == TaskState.Completed ? 2 : task.State is not null ? 1 : 0;
    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static JsonElement MergeObjects(IEnumerable<JsonElement> rows)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in rows.Where(row => row.ValueKind == JsonValueKind.Object))
        {
            foreach (var field in row.EnumerateObject())
            {
                if (field.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                if (values.TryGetValue(field.Name, out var current))
                {
                    if (current.ValueKind == JsonValueKind.Object && field.Value.ValueKind == JsonValueKind.Object)
                    { values[field.Name] = MergeObjects([current, field.Value]); continue; }
                    if (field.Name is "usedTryCount" or "userSubmitStatus" or "submitStatus"
                        && Decimal(current) > Decimal(field.Value)) continue;
                    if (field.Name == "userScore" && Decimal(current) is >= 0) continue;
                }
                values[field.Name] = field.Value;
            }
        }
        return JsonSerializer.SerializeToElement(values);
    }

    public static JsonElement Get(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) ? value : default;
    private static bool Has(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out _);
    public static string Text(JsonElement value) => value.ValueKind is JsonValueKind.String ? (value.GetString() ?? "").Trim()
        : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
    private static string? Id(JsonElement value) => MoocPlatform.IsId(Text(value)) ? Text(value) : null;
    private static int? Integer(JsonElement value) => int.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static decimal? Decimal(JsonElement value) => decimal.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static DateTimeOffset? Date(JsonElement value)
    {
        if (!long.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp) || timestamp <= 0) return null;
        try
        {
            var result = timestamp >= 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp) : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return result.Year is >= 2000 and <= 2100 ? result : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
