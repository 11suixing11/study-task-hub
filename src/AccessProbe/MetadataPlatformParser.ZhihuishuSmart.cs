using System.Globalization;
using System.Text.Json;

namespace AccessProbe;

public static partial class MetadataPlatformParser
{
    public static MetadataCatalog ZhihuishuSmartCatalog(string json, int bucket)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "smart-courses", json);
        if (outcome != "ok") return new(outcome, [], 1, 0, 0, 0, 0);
        using var document = Document(json); var list = Property(document!.RootElement, "rt");
        if (bucket is not (3 or 4) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 10000)
            return new("unrecognized_catalog", [], 1, 0, 0, 0, 0);
        var courses = new Dictionary<string, MetadataCourse>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal); var invalid = 0; var duplicates = 0;
        foreach (var row in list.EnumerateArray())
        {
            // GetRawText preserves numeric identifiers beyond JavaScript's integer precision.
            var courseId = Text(row, "courseId"); var classId = Text(row, "classId"); var name = Text(row, "courseName").Trim();
            var newFlag = Property(row, "newFlag");
            if (!MetadataTaskNavigation.IsId(courseId) || !MetadataTaskNavigation.IsId(classId) || !ValidText(name, 200)
                || newFlag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { invalid++; continue; }
            var endText = Text(row, "endTime"); var end = ChinaTextTime(Property(row, "endTime"));
            var family = newFlag.ValueKind == JsonValueKind.False ? "legacy-ai"
                : endText.Length > 0 && end is null ? "unverified"
                : end is not null && end.Value.ToUnixTimeMilliseconds() < 1757433600000 ? "legacy-smart" : "pro";
            var id = MetadataTaskNavigation.SmartCourseId(courseId, classId);
            var className = Text(row, "className").Trim();
            var detail = "智慧课程 · " + (bucket == 3 ? "进行中" : "已完成")
                + (ValidText(className, 200) ? " · " + className : "")
                + (family == "pro" ? " · 作业、考试列表" : " · 此版本任务暂未覆盖");
            var course = new MetadataCourse(id, name, "smart", detail)
                { ApiCourseId = courseId, Smart = new(classId, family) };
            if (conflicts.Contains(id)) { invalid++; continue; }
            if (courses.TryGetValue(id, out var previous))
            { duplicates++; if (previous != course) { courses.Remove(id); conflicts.Add(id); invalid++; } }
            else courses[id] = course;
        }
        return new(invalid > 0 ? "partial_catalog" : "ok", courses.Values.ToArray(), 1, list.GetArrayLength() == 0 ? 0 : 1,
            list.GetArrayLength(), invalid, duplicates) { TotalRows = list.GetArrayLength() };
    }

    public static MetadataCatalog ZhihuishuAgentCatalog(string json, int page)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "agent-courses", json);
        if (outcome != "ok") return new(outcome, [], page, 0, 0, 0, 0);
        using var document = Document(json); var root = document!.RootElement; var data = Property(root, "data");
        var list = Property(data, "list"); var total = Number(data, "total"); var pages = Number(data, "totalPage");
        // The current official AI directory also uses a zeroed page/size for a confirmed empty account.
        if (page == 1 && Property(root, "success").ValueKind == JsonValueKind.True && Number(data, "page") == 0
            && Number(data, "size") == 0 && total == 0 && pages == 0 && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() == 0)
            return new("ok", [], 1, 0, 0, 0, 0) { TotalRows = 0 };
        if (Property(root, "success").ValueKind != JsonValueKind.True || page < 1 || Number(data, "page") != page
            || Number(data, "size") != SharedCoursePageSize || total is null or < 0 or > 50000
            || pages != (total.Value + SharedCoursePageSize - 1) / SharedCoursePageSize
            || pages > 0 && page > pages || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() != Math.Min(SharedCoursePageSize, Math.Max(0, total.Value - (page - 1) * SharedCoursePageSize)))
            return new("unrecognized_catalog", [], page, 0, 0, 0, 0);
        var courses = new Dictionary<string, MetadataCourse>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal); var invalid = 0; var duplicates = 0;
        foreach (var row in list.EnumerateArray())
        {
            var id = Text(row, "courseId"); var name = Text(row, "courseName").Trim(); var term = Text(row, "termName").Trim();
            if (!MetadataTaskNavigation.IsId(id) || !ValidText(name, 200)) { invalid++; continue; }
            var course = new MetadataCourse(id, name, "agent", "AI 智课／智能体课"
                + (ValidText(term, 100) ? " · " + term : "") + " · 任务暂未覆盖，请在官网核对");
            if (conflicts.Contains(id)) { invalid++; continue; }
            if (courses.TryGetValue(id, out var previous))
            { duplicates++; if (previous != course) { courses.Remove(id); conflicts.Add(id); invalid++; } }
            else courses[id] = course;
        }
        return new(invalid > 0 ? "partial_catalog" : "ok", courses.Values.ToArray(), page, pages!.Value,
            list.GetArrayLength(), invalid, duplicates) { TotalRows = total };
    }

    public static ZhihuishuSmartTaskTypes ZhihuishuSmartTypes(string json)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "smart-task-types", json);
        if (outcome != "ok") return new(outcome, []);
        using var document = Document(json); var list = Property(document!.RootElement, "data");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 100) return new("unrecognized_task_types", []);
        var types = new List<ZhihuishuSmartTaskType>(); var ordinary = new HashSet<int>();
        foreach (var row in list.EnumerateArray())
        {
            var type = Number(row, "taskType"); var count = Number(row, "count"); var unfinished = Number(row, "unfinished");
            if (type is null or < 0 or > 100 || count is null or < 0 or > 10000 || unfinished is null or < 0 || unfinished > count
                || type is 1 or 2 && !ordinary.Add(type.Value)) return new("unrecognized_task_types", []);
            types.Add(new(type.Value, count.Value, unfinished.Value));
        }
        return new("ok", types);
    }

    public static MetadataTaskBatch ZhihuishuSmartTasks(string json, string account, MetadataCourse course, string kind, int bucket)
    {
        var outcome = EnvelopeOutcome(MetadataPlatform.Zhihuishu, "smart-" + kind, json);
        if (outcome != "ok") return new(outcome, [], 0, 0, []);
        if (!MetadataTaskNavigation.IsSmartCourse(course) || !MetadataTaskNavigation.IsId(account)
            || kind is not ("homework" or "exam") || bucket is not (0 or 1)) return new("unrecognized_tasks", [], 0, 0, []);
        using var document = Document(json); var list = Property(document!.RootElement, "data");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 10000) return new("unrecognized_tasks", [], 0, 0, []);
        var issues = new HashSet<string>(StringComparer.Ordinal); var tasks = new List<PlatformCollectedTask>(); var invalid = 0;
        var type = kind == "homework" ? 1 : 2;
        foreach (var row in list.EnumerateArray())
        {
            var id = Text(row, "id"); var title = Text(row, "taskName").Trim();
            if (!MetadataTaskNavigation.IsId(id) || !ValidText(title, 300) || Text(row, "courseId") != course.ApiCourseId
                || Text(row, "classId") != course.Smart!.ClassId || Number(row, "taskType") != type)
            { invalid++; continue; }
            var status = Number(row, "status"); var examStatus = Number(row, "examStatus");
            var peer = Property(row, "isPeerReview").ValueKind;
            var mode = Number(row, "submitMode");
            TaskState? state = status is >= 0 and <= 2 ? examStatus switch
            {
                0 or 1 or 7 or 8 when bucket == 0 => TaskState.Pending,
                10 when bucket == 0 => TaskState.Returned,
                5 or 6 when bucket == 1 => TaskState.Completed,
                _ => null
            } : null;
            if (status == 0 && examStatus != 0 || status is 0 or 1 && examStatus == 7) state = null;
            // Peer review and group submission can require further work even after submitting.
            if (peer != JsonValueKind.False || mode is not (1 or 2) || mode == 2) state = null;
            var text = examStatus switch
            {
                0 => "未开始", 1 => "进行中", 5 => "完成", 6 => "已批阅", 7 => "逾期未提交", 8 => "可补交",
                10 => "退回", 11 => "未组队", 12 => "仅组长提交", 13 => "互评", 14 => "未通关", 15 => "去组队", _ => "状态待核对"
            };
            if (state is null) { issues.Add("state_unverified"); text += " · 待核对"; }
            var begin = ChinaTextTime(Property(row, "startTime")); var deadline = ChinaTextTime(Property(row, "endTime"));
            CheckTimes(ref begin, ref deadline, issues);
            var time = "startTime=" + TimeEvidence(row, "startTime") + "; endTime=" + TimeEvidence(row, "endTime")
                + "; latestSubmitTime=" + TimeEvidence(row, "latestSubmitTime") + "; peerReviewEndTime=" + TimeEvidence(row, "peerReviewEndTime");
            var explanation = "来自新版智慧课程的" + (bucket == 0 ? "未完成" : "已完成") + "列表；保留官网 examStatus、status 和时间。"
                + "入口打开此课程的官方任务列表，请按任务名核对。补交与互评时间独立保留，不替代原截止时间。";
            if (state is null) explanation += "状态、互评或小组提交尚需在官网确认。";
            tasks.Add(new("zhihuishu", account, course.Id, "smart", id, kind, title, course.Name, state,
                text + "（examStatus=" + (examStatus?.ToString(CultureInfo.InvariantCulture) ?? "未提供")
                    + "; status=" + (status?.ToString(CultureInfo.InvariantCulture) ?? "未提供") + "; bucket=" + bucket
                    + "; submitMode=" + Text(row, "submitMode") + "; isPeerReview=" + peer + "）",
                state is null ? "unknown" : "explicit", explanation, begin, deadline, time, MetadataTaskNavigation.SmartList(course, id, kind))
                { MetadataVersion = 2, ExamMode = kind == "exam" ? "online" : "" });
        }
        return UniqueTasks(tasks, invalid, issues) with { Rows = list.GetArrayLength(), TotalRows = list.GetArrayLength(), ListComplete = true };
    }
}
