using System.Globalization;
using System.Text.Json;

namespace AccessProbe;

public sealed record XuetangCatalogPage(string Outcome, IReadOnlyList<XuetangCourse> Courses,
    int Rows, int DuplicateRows, int UnsupportedCourses, int? Total);
public sealed record XuetangSchedule(string Outcome, IReadOnlyDictionary<string, decimal> Leaves);
public sealed record XuetangResetTimes(string Outcome, IReadOnlyDictionary<string, DateTimeOffset> Deadlines);
public sealed record XuetangTaskBatch(string Outcome, IReadOnlyList<XuetangTask> Tasks, int DuplicateRows, IReadOnlyList<string> Issues);

public static class XuetangParser
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int MaximumNodes = 10000;
    public static string EnvelopeOutcome(string json)
    {
        using var document = Document(json);
        return document is null ? "invalid_json" : Envelope(document.RootElement);
    }
    private static string Envelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "unrecognized_envelope";
        var code = Number(Field(root, "code")); var error = Number(Field(root, "error_code"));
        if (code is 50008 or 50012 or 50014 || error == 500300) return "login_required";
        return Field(root, "success").ValueKind switch
        { JsonValueKind.True => "ok", JsonValueKind.False => "platform_error", _ => "unrecognized_envelope" };
    }
    public static string LoginOutcome(string json)
    {
        using var document = Document(json);
        if (document is null) return "invalid_json";
        var outcome = Envelope(document.RootElement);
        if (outcome != "ok") return outcome;
        return Field(Field(document.RootElement, "data"), "is_login").ValueKind switch
        { JsonValueKind.True => "ok", JsonValueKind.False => "login_required", _ => "unrecognized_login" };
    }
    public static string? AccountId(string json)
    {
        using var document = Document(json);
        if (document is null || Envelope(document.RootElement) != "ok") return null;
        return Id(Field(document.RootElement, "data"), "id");
    }
    public static string? ProductSkuId(string json)
    {
        using var document = Document(json);
        if (document is null || Envelope(document.RootElement) != "ok") return null;
        // Only an explicit SKU in the metadata response can bind the read-only reset-record query.
        // Never derive it from classroom/product IDs or fetch exercise/question content to obtain it.
        return Id(Field(document.RootElement, "data"), "sku_id");
    }

    public static XuetangCatalogPage Catalog(string json, int status, bool stowed = false)
    {
        using var document = Document(json);
        if (document is null) return new("invalid_json", [], 0, 0, 0, null);
        var outcome = Envelope(document.RootElement);
        if (outcome != "ok") return new(outcome, [], 0, 0, 0, null);
        var data = Field(document.RootElement, "data"); var list = Field(data, "product_list");
        if (list.ValueKind != JsonValueKind.Array) return new("unrecognized_catalog", [], 0, 0, 0, null);
        if (list.GetArrayLength() > MaximumNodes) return new("course_limit", [], list.GetArrayLength(), 0, 0, null);
        int? total = null;
        if (data.TryGetProperty("count", out var count))
        {
            var value = Number(count);
            if (value is null || value < list.GetArrayLength() || value > MaximumNodes || value != decimal.Truncate(value.Value))
                return new("unrecognized_catalog", [], list.GetArrayLength(), 0, 0, null);
            total = (int)value;
        }
        var courses = new Dictionary<string, XuetangCourse>(StringComparer.Ordinal); var duplicates = 0; var unsupported = 0; var invalid = false;
        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) { invalid = true; continue; }
            var ownStatus = Number(Field(row, "status"));
            if (ownStatus is < 1 or > 3 || ownStatus is { } s && s != decimal.Truncate(s)
                || !stowed && ownStatus is { } known && known != status) { invalid = true; continue; }
            // The site's multi-course product opens /program/.../progress rather than a single classroom.
            if (Number(Field(row, "property")) == 1) { unsupported++; continue; }
            var classroom = Id(row, "classroom_id"); var product = Text(row, "sign"); var course = Text(row, "course_sign");
            var name = Text(row, "name").Trim();
            if (classroom is null || !XuetangPlatform.IsSign(product) || !XuetangPlatform.IsSign(course) || name.Length is 0 or > 200
                || stowed && ownStatus is null) { invalid = true; continue; }
            var value = new XuetangCourse(name, classroom, product, course, (int)(ownStatus ?? status), stowed, Id(row, "sku_id") ?? "");
            var key = classroom + "\n" + product + "\n" + course;
            if (courses.TryGetValue(key, out var previous))
            { duplicates++; if (previous != value) invalid = true; }
            else courses.Add(key, value);
        }
        return new(invalid ? "partial_catalog" : "ok", courses.Values.ToArray(), list.GetArrayLength(), duplicates, unsupported, total);
    }

    public static XuetangSchedule Schedule(string json)
    {
        using var document = Document(json);
        var leaves = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (document is null) return new("invalid_json", leaves);
        var outcome = Envelope(document.RootElement);
        if (outcome != "ok") return new(outcome, leaves);
        var values = Field(Field(document.RootElement, "data"), "leaf_schedules");
        if (values.ValueKind != JsonValueKind.Object) return new("unrecognized_schedule", leaves);
        if (values.EnumerateObject().Take(MaximumNodes + 1).Count() > MaximumNodes) return new("task_limit", leaves);
        var invalid = false;
        foreach (var value in values.EnumerateObject())
        {
            var fraction = Number(value.Value);
            if (!XuetangPlatform.IsId(value.Name) || fraction is null or < 0 or > 1 || !leaves.TryAdd(value.Name, fraction.Value)) invalid = true;
        }
        return new(invalid ? "partial_schedule" : "ok", leaves);
    }

    public static XuetangResetTimes ResetTimes(string json)
    {
        using var document = Document(json);
        var values = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (document is null) return new("invalid_json", values);
        var outcome = Envelope(document.RootElement);
        if (outcome != "ok") return new(outcome, values);
        var rows = Field(document.RootElement, "data");
        if (rows.ValueKind != JsonValueKind.Array) return new("unrecognized_reset_times", values);
        if (rows.GetArrayLength() > MaximumNodes) return new("task_limit", values);
        var invalid = false;
        foreach (var row in rows.EnumerateArray())
        {
            var id = Id(row, "leaf_id"); var deadline = Time(Field(row, "reset_deadline"), seconds: true);
            if (id is null || deadline is null || values.TryGetValue(id, out var previous) && previous != deadline) { invalid = true; continue; }
            values[id] = deadline.Value;
        }
        return new(invalid ? "partial_reset_times" : "ok", values);
    }

    public static XuetangTaskBatch Chapter(string json, string accountId, XuetangCourse course,
        XuetangSchedule schedule, XuetangResetTimes resetTimes)
    {
        using var document = Document(json);
        if (document is null) return new("invalid_json", [], 0, []);
        var outcome = Envelope(document.RootElement);
        if (outcome != "ok") return new(outcome, [], 0, []);
        var chapters = Field(Field(document.RootElement, "data"), "course_chapter");
        if (chapters.ValueKind != JsonValueKind.Array) return new("unrecognized_chapter", [], 0, []);
        var tasks = new Dictionary<string, XuetangTask>(StringComparer.Ordinal); var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var issues = new HashSet<string>(StringComparer.Ordinal); var visited = 0; var duplicates = 0;
        foreach (var chapter in chapters.EnumerateArray()) Walk(chapter, 0, false);
        if (schedule.Outcome != "ok") issues.Add("schedule_incomplete");
        return new(issues.Count > 0 ? "partial_tasks" : "ok", tasks.Values.ToArray(), duplicates, issues.ToArray());

        void Walk(JsonElement node, int depth, bool hidden)
        {
            if (++visited > MaximumNodes || depth > 12) { issues.Add("tree_limit"); return; }
            if (node.ValueKind != JsonValueKind.Object) { issues.Add("tree_shape"); return; }
            hidden |= Field(node, "is_show").ValueKind == JsonValueKind.False;
            var type = Number(Field(node, "leaf_type"));
            if (type is { } leafType)
            {
                if (hidden) return;
                if (leafType is not (5 or 6))
                { if (leafType is < 0 or > 6 || leafType != decimal.Truncate(leafType)) issues.Add("unknown_leaf_type"); return; }
                if (Field(node, "is_show").ValueKind != JsonValueKind.True) { issues.Add("visibility_unknown"); return; }
                var id = Id(node, "id"); var title = Text(node, "name").Trim();
                if (id is null || title.Length is 0 or > 300) { issues.Add("invalid_task"); return; }
                if (conflicts.Contains(id)) { duplicates++; return; }
                var kind = leafType == 5 ? "exam" : "exercise";
                decimal? percent = schedule.Leaves.TryGetValue(id, out var fraction) ? fraction * 100 : null;
                TaskState? state = percent is { } progress ? progress == 100 ? TaskState.Completed : TaskState.Pending : null;
                var status = percent == 100 ? "已完成（平台学习进度）" : percent == 0 ? "未开始" : percent is not null ? "进行中（平台学习进度）" : "状态待核对";
                var evidence = percent is not null ? "leaf_schedules=" + fraction.ToString(CultureInfo.InvariantCulture) : "unknown";
                var explanation = percent is not null ? "按学堂课程页学习进度显示；提交、批阅与考试有效性请在官方任务页核对。" : "平台未返回此任务的可识别学习进度。";
                var begin = Time(Field(node, "start_time"));
                var scoreTime = Field(node, "score_deadline"); var endTime = Field(node, "end_time");
                DateTimeOffset? deadline = null;
                var timeText = "start_time=" + TimeText(Field(node, "start_time")) + "；score_deadline=" + TimeText(scoreTime) + "；end_time=" + TimeText(endTime);
                if (InvalidTime(Field(node, "start_time")) || InvalidTime(scoreTime) || InvalidTime(endTime)) issues.Add("invalid_time");
                if (resetTimes.Outcome == "ok")
                {
                    if (resetTimes.Deadlines.TryGetValue(id, out var reset))
                    { deadline = reset; timeText += "；reset_deadline=" + reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "（秒）"; }
                    else
                    {
                        var score = Time(scoreTime); var end = Time(endTime);
                        // A scoring cutoff can differ from the last time the task remains accessible.
                        // Keep both in the evidence and use the earlier explicit cutoff for reminders.
                        deadline = score is { } scoring && end is { } closing ? (scoring <= closing ? scoring : closing) : score ?? end;
                    }
                }
                else { issues.Add("reset_times_unverified"); timeText += "；延期记录未确认，截止时间待核对"; }
                if (InvalidTime(scoreTime) || InvalidTime(endTime)) deadline = null;
                if (begin is { } start && deadline is { } limit && start > limit)
                { begin = null; deadline = null; issues.Add("invalid_time_window"); }
                var link = XuetangTaskNavigation.Link(course.ProductSign, course.CourseSign, course.ClassroomId, id, kind);
                if (link is null) { issues.Add("invalid_link"); return; }
                var task = new XuetangTask(accountId, course.ClassroomId, course.ProductSign, course.CourseSign, id, kind,
                    title, course.Name, state, status, evidence, explanation, begin, deadline, timeText, percent, link);
                if (tasks.TryGetValue(id, out var previous))
                {
                    duplicates++;
                    if (previous != task) { tasks.Remove(id); conflicts.Add(id); issues.Add("conflicting_task"); }
                }
                else tasks.Add(id, task);
                return;
            }
            var found = false;
            foreach (var key in new[] { "section_leaf_list", "leaf_list" })
            {
                if (!node.TryGetProperty(key, out var children)) continue;
                found = true;
                if (children.ValueKind != JsonValueKind.Array) { issues.Add("tree_shape"); continue; }
                foreach (var child in children.EnumerateArray()) Walk(child, depth + 1, hidden);
            }
            if (!found) issues.Add("tree_shape");
        }
    }

    private static JsonDocument? Document(string json)
    {
        if (json.Length > MaximumBytes) return null;
        try { return JsonDocument.Parse(json, new() { MaxDepth = 32 }); } catch (JsonException) { return null; }
    }
    private static JsonElement Field(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) ? value : default;
    private static string Text(JsonElement root, string name) => Field(root, name).ValueKind == JsonValueKind.String
        ? Field(root, name).GetString() ?? "" : "";
    private static string? Id(JsonElement root, string name)
    {
        var value = Field(root, name); var id = value.ValueKind switch
        { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
        return XuetangPlatform.IsId(id) ? id : null;
    }
    private static decimal? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
    private static bool InvalidTime(JsonElement value) => value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
        && !(Number(value) == 0) && Time(value) is null;
    private static DateTimeOffset? Time(JsonElement value, bool seconds = false)
    {
        var number = Number(value);
        if (number is null or <= 0 || number != decimal.Truncate(number.Value)) return null;
        try
        {
            var date = seconds ? DateTimeOffset.FromUnixTimeSeconds(checked((long)number.Value)) : DateTimeOffset.FromUnixTimeMilliseconds(checked((long)number.Value));
            return date.Year is >= 2000 and <= 2100 ? date : null;
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException) { return null; }
    }
    private static string TimeText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetRawText() + "（毫秒）", JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "未提供", _ => "格式未识别"
    };
}
