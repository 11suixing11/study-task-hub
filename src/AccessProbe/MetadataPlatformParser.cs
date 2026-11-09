using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public static partial class MetadataPlatformParser
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static JsonDocument? Document(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) return null;
        try { return JsonDocument.Parse(json, new() { MaxDepth = 48 }); } catch (JsonException) { return null; }
    }
    public static string EnvelopeOutcome(MetadataPlatform platform, string operation, string json)
    {
        if (platform.Id == "fif") return "adaptation_disabled";
        if (platform.Id == "ucampus") return UcampusEnvelope(operation, json);
        if (platform.Id == "pta") return PtaEnvelope(operation, json);
        if (json.TrimStart().StartsWith('<'))
            return json.Contains("login", StringComparison.OrdinalIgnoreCase) || json.Contains("cas-server", StringComparison.OrdinalIgnoreCase)
                || json.Contains("未登录", StringComparison.Ordinal) ? "login_required" : platform.Id == "ucampus" ? "task_contract_unverified" : "unrecognized_response";
        using var document = Document(json); if (document is null || document.RootElement.ValueKind != JsonValueKind.Object) return "unrecognized_response";
        var root = document.RootElement;
        var code = Text(root, operation is "profile" or "shared-courses" or "agent-courses" or "smart-task-types" or "smart-homework" or "smart-exam" ? "code" : "status");
        var message = Text(root, "message");
        if (Property(Property(root, "rt"), "loginstatus").ValueKind == JsonValueKind.False
            || message.Contains("未登录", StringComparison.Ordinal) || message.Contains("登录过期", StringComparison.Ordinal)
            || message.Contains("登录超时", StringComparison.Ordinal)) return "login_required";
        // The student token and individual course permissions can fail while the CAS account remains signed in.
        // Ambiguous service codes must not invalidate the account's reusable Cookie archive.
        if (message.Contains("Missing JWT", StringComparison.OrdinalIgnoreCase)) return "course_credentials_unavailable";
        if (code == "401" || Text(root, "code") == "401" || Text(root, "status") == "401") return "login_required";
        if (code == "403" || Text(root, "code") == "403" || Text(root, "status") == "403") return "course_access_denied";
        if (operation == "shared-homework" && code == "200" && Text(root, "code") is "-1" or "-3") return "course_access_denied";
        if (Text(root, "code") is "-1" or "-3" || Text(root, "status") is "-1" or "-3") return "unrecognized_response";
        if (code != "200") return "unrecognized_response";
        return "ok";
    }
    public static string? ZhihuishuAccount(IEnumerable<MetadataSessionCookie> cookies)
    {
        var values = cookies.Where(cookie => cookie.Name == "CASLOGC" && cookie.AppliesTo(MetadataPlatform.Zhihuishu.Home))
            .Select(cookie => cookie.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length != 1) return null;
        using var document = Document(Uri.UnescapeDataString(values[0]));
        if (document is null) return null;
        var uuid = Text(document.RootElement, "uuid"); return MetadataTaskNavigation.IsId(uuid) ? uuid : null;
    }
    public static string ZhihuishuProfile(string json, string? accountId)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "profile", json); if (outcome != "ok") return outcome;
        using var document = Document(json); var value = Property(document!.RootElement, "result");
        if (value.ValueKind != JsonValueKind.Object || !MetadataTaskNavigation.IsId(accountId)) return "unrecognized_user";
        // CASLOGC.uuid is the account identifier used by the official student metadata client.
        // If the profile also reports uuid, it must agree; no other field is guessed as an identifier.
        var uuid = Text(value, "uuid"); return uuid.Length > 0 && uuid != accountId ? "account_mismatch" : "ok";
    }
    public static MetadataDiscovery Discovery(MetadataPlatform platform, string json)
    {
        if (platform.Id == "ucampus") { var catalog = UcampusCatalog(json); return new(catalog.Outcome, catalog.Courses.Count); }
        var outcome = EnvelopeOutcome(platform, "profile", json);
        return new(outcome == "ok" ? "task_contract_unverified" : outcome, 0);
    }
    public static MetadataCatalog ZhihuishuCatalog(string json, int requestedPage)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "courses", json); if (outcome != "ok") return new(outcome, [], requestedPage, 0, 0, 0, 0);
        using var document = Document(json); var rt = Property(document!.RootElement, "rt"); var list = Property(rt, "pageList");
        var page = Number(rt, "pageNum"); var total = Number(rt, "totalPages");
        // The official empty flip catalog uses pageNum=0 only when all totals are zero.
        if (requestedPage == 1 && page == 0 && total == 0 && Number(rt, "totalCount") == 0
            && Number(rt, "pageSize") == 20 && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() == 0)
            return new("ok", [], 1, 0, 0, 0, 0) { TotalRows = 0 };
        if (list.ValueKind != JsonValueKind.Array || page != requestedPage || total is null or < 0 or > 10000
            || total > 0 && page > total || total == 0 && list.GetArrayLength() > 0 || list.GetArrayLength() > 100)
            return new("unrecognized_catalog", [], requestedPage, total ?? 0, 0, 0, 0);
        var courses = new Dictionary<string, MetadataCourse>(StringComparer.Ordinal); var invalid = 0; var duplicates = 0;
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in list.EnumerateArray())
        {
            var id = Text(row, "courseId"); var name = Text(row, "courseName").Trim();
            if (!MetadataTaskNavigation.IsId(id) || !ValidText(name, 200)) { invalid++; continue; }
            if (conflicts.Contains(id)) { invalid++; continue; }
            var course = new MetadataCourse(id, name, "flip", "翻转课 · 课程内作业、考试列表");
            if (courses.TryGetValue(id, out var previous))
            {
                duplicates++; if (previous.Name != name) { courses.Remove(id); conflicts.Add(id); invalid++; }
            }
            else courses[id] = course;
        }
        return new(invalid > 0 ? "partial_catalog" : "ok", courses.Values.ToArray(), page!.Value, total!.Value,
            list.GetArrayLength(), invalid, duplicates) { TotalRows = Number(rt, "totalCount") };
    }
    public static MetadataTaskBatch ZhihuishuReminders(string json, string accountId, IReadOnlyDictionary<string, MetadataCourse> courses, DateTimeOffset now)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "reminders", json); if (outcome != "ok") return new(outcome, [], 0, 0, []);
        using var document = Document(json); var list = Property(document!.RootElement, "rt");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 10000) return new("unrecognized_tasks", [], 0, 0, []);
        var tasks = new Dictionary<string, PlatformCollectedTask>(StringComparer.Ordinal); var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var invalid = 0; var duplicates = 0; var issues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in list.EnumerateArray())
        {
            var type = Number(row, "type"); var id = Text(row, "id"); var courseId = Text(row, "courseId"); var title = Text(row, "title").Trim();
            if (type is not (1 or 2) || !MetadataTaskNavigation.IsId(id) || !MetadataTaskNavigation.IsId(courseId)
                || !courses.TryGetValue(courseId, out var course) || course.Kind != "flip" || !ValidText(title, 300)) { invalid++; continue; }
            var status = Number(row, "status"); var paper = Number(row, "paperType"); var examType = Number(row, "examType");
            TaskState? state = status == 4 ? TaskState.Returned : status == 1 || type == 2 && status == 2 ? TaskState.Pending : null;
            var statusText = status == 4 ? "退回重做" : status == 1 ? "尚未开始" : type == 2 && status == 2 ? "进行中" : "状态待核对";
            statusText += "（status=" + (status?.ToString(CultureInfo.InvariantCulture) ?? "未提供") + "）";
            var begin = Time(Property(row, "startTime")); var deadline = Time(Property(row, "endTime"));
            if (begin is not null && deadline is not null && begin > deadline) { begin = deadline = null; issues.Add("invalid_time_order"); }
            if (deadline is null) issues.Add("deadline_unverified"); if (state is null) issues.Add("state_unverified");
            var link = status is >= 1 and <= 4 && paper is 0 or 1
                ? MetadataTaskNavigation.Zhihuishu(courseId, id, type.Value, paper.Value, status.Value, examType, now) : null;
            if (link is null) issues.Add("entry_unavailable");
            var explanation = state == TaskState.Returned ? "官网首页将 status=4 标为退回重做，需重新处理。"
                : state == TaskState.Pending ? "来自官网近期提醒；尚未开始或考试进行中。" : "官网近期提醒未提供已核对的完成状态，请在官网确认。";
            if (type == 2 && examType is not (null or 0 or 1)) explanation += "该考试形式不提供已核对的线上任务入口。";
            var timeText = "startTime=" + TimeEvidence(row, "startTime") + "; endTime=" + TimeEvidence(row, "endTime");
            var task = new PlatformCollectedTask("zhihuishu", accountId, courseId, "flip", id, type == 1 ? "homework" : "exam",
                title, course.Name, state, statusText, state is null ? "unknown" : "explicit", explanation, begin, deadline, timeText, link);
            var key = courseId + "\n" + task.Kind + "\n" + id;
            if (conflicts.Contains(key)) { invalid++; continue; }
            if (tasks.TryGetValue(key, out var previous))
            {
                duplicates++; if (previous != task) { tasks.Remove(key); conflicts.Add(key); invalid++; issues.Add("conflicting_task"); }
            }
            else tasks[key] = task;
        }
        return new(invalid > 0 || issues.Count > 0 ? "partial_tasks" : "ok", tasks.Values.ToArray(), invalid, duplicates, issues.ToArray());
    }
    public static int? SharedReminderCount(string json)
    {
        if (EnvelopeOutcome(MetadataPlatform.Zhihuishu, "shared-reminders", json) != "ok") return null;
        using var document = Document(json); var list = Property(Property(document!.RootElement, "rt"), "data");
        return list.ValueKind == JsonValueKind.Array && list.GetArrayLength() <= 10000 ? list.GetArrayLength() : null;
    }
    public static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) ? field : default;
    public static string Text(JsonElement value, string name)
    {
        var field = Property(value, name); return field.ValueKind == JsonValueKind.String ? field.GetString()!
            : field.ValueKind == JsonValueKind.Number ? field.GetRawText() : "";
    }
    public static int? Number(JsonElement value, string name) => int.TryParse(Text(value, name), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static bool ValidText(string text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl);
    private static string TimeEvidence(JsonElement row, string field)
    {
        var text = Text(row, field);
        return text.Length <= 256 && !text.Any(char.IsControl) ? text : "时间字段无法识别";
    }
    private static DateTimeOffset? Time(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms < 946684800000 || ms > 4102444800000) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }
}
