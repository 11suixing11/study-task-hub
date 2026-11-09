using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccessProbe;

public sealed record PtaProblemSet(string Id, string Name, string TimeType, string Type, string Feature,
    DateTimeOffset? BeginUtc, DateTimeOffset? EndUtc, bool TimeVerified, string TimeText, string Stage)
{
    public override string ToString() => nameof(PtaProblemSet);
}
public sealed record PtaProblemSetPage(string Outcome, IReadOnlyList<PtaProblemSet> Items,
    IReadOnlyDictionary<string, IReadOnlyList<MetadataCourse>> CoursesBySet, int Rows, int TotalRows, int UnsupportedRows);
public sealed record PtaTaskResult(string Outcome, PlatformCollectedTask? Task);

public static partial class MetadataPlatformParser
{
    public const int PtaCoursePageSize = 12;
    public const int PtaSetPageSize = 20;
    public const int PtaTeachingPageSize = 30;
    public static bool PtaId(string? value) => value is { Length: > 0 and <= 24 } && value[0] is >= '1' and <= '9'
        && value.All(char.IsAsciiDigit);
    public static bool PtaCourseValid(MetadataCourse? course) => course is { Kind: "class" }
        && PtaId(course.Id) && course.Id == course.ApiCourseId;

    public static string PtaEnvelope(string operation, string json)
    {
        if (json.TrimStart().StartsWith('<')) return json.Contains("login", StringComparison.OrdinalIgnoreCase)
            ? "login_required" : "unrecognized_response";
        using var document = Document(json);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object) return "unrecognized_response";
        var root = document.RootElement; var error = Property(root, "error");
        if (error.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            return Text(error, "code") switch
            {
                "USER_NOT_LOGGED_IN" => "login_required",
                "PERMISSION_DENIED" => "course_access_denied",
                "NOT_FOUND" or "PROBLEM_SET_NOT_FOUND" => "entry_unavailable",
                _ => "platform_error"
            };
        if (operation != "profile") return "ok";
        if (PtaId(Text(Property(root, "user"), "id"))) return "ok";
        if (Property(root, "user").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            && new[] { "emailLogin", "omsLogin", "studentUserLogin", "phoneLogin" }
                .Any(field => Property(root, field).ValueKind is JsonValueKind.False or JsonValueKind.True)) return "login_required";
        return "unrecognized_user";
    }
    public static string? PtaAccount(string json)
    {
        if (PtaEnvelope("profile", json) != "ok") return null;
        using var document = Document(json); return Text(Property(document!.RootElement, "user"), "id");
    }

    public static MetadataCatalog PtaCatalog(string json, int page)
    {
        var outcome = PtaEnvelope("pta-courses", json);
        if (outcome != "ok") return new(outcome, [], page, 0, 0, 0, 0);
        using var document = Document(json); var root = document!.RootElement; var list = Property(root, "userGroups");
        if (!PtaPaging(root, list, page, PtaCoursePageSize, out var total)) return new("unrecognized_catalog", [], page, 0, 0, 0, 0);
        var courses = new Dictionary<string, MetadataCourse>(StringComparer.Ordinal); var invalid = 0; var duplicates = 0;
        foreach (var row in list.EnumerateArray())
        {
            var type = Text(row, "type");
            if (type is "NORMAL_USER" or "COLLABORATE_USER") continue;
            var course = PtaClass(row);
            if (course is null) { invalid++; continue; }
            if (!courses.TryAdd(course.Id, course)) { duplicates++; invalid++; }
        }
        return new(invalid > 0 ? "partial_catalog" : "ok", courses.Values.ToArray(), page, PtaPages(total, PtaCoursePageSize),
            list.GetArrayLength(), invalid, duplicates) { TotalRows = total };
    }

    public static PtaProblemSetPage PtaSets(string json, int page, bool teachingDirectory = false)
    {
        var outcome = PtaEnvelope(teachingDirectory ? "pta-teaching-sets" : "pta-course-sets", json);
        if (outcome != "ok") return new(outcome, [], new Dictionary<string, IReadOnlyList<MetadataCourse>>(), 0, 0, 0);
        using var document = Document(json); var root = document!.RootElement; var list = Property(root, "problemSets");
        var associations = Property(root, "userGroupsByProblemSetId");
        if (!PtaPaging(root, list, page, teachingDirectory ? PtaTeachingPageSize : PtaSetPageSize, out var total)
            || teachingDirectory && list.GetArrayLength() > 0 && associations.ValueKind != JsonValueKind.Object)
            return new("unrecognized_tasks", [], new Dictionary<string, IReadOnlyList<MetadataCourse>>(), 0, 0, 0);
        var items = new Dictionary<string, PtaProblemSet>(StringComparer.Ordinal);
        var groups = new Dictionary<string, IReadOnlyList<MetadataCourse>>(StringComparer.Ordinal); var invalid = 0;
        foreach (var row in list.EnumerateArray())
        {
            var item = PtaSet(row);
            if (item is null || !items.TryAdd(item.Id, item)) { invalid++; continue; }
            if (!teachingDirectory) continue;
            var members = Property(Property(associations, item.Id), "items");
            if (members.ValueKind != JsonValueKind.Array || members.GetArrayLength() is 0 or > 200) { invalid++; continue; }
            var classes = new Dictionary<string, MetadataCourse>(StringComparer.Ordinal);
            foreach (var member in members.EnumerateArray())
            {
                if (Text(member, "type") is "NORMAL_USER" or "COLLABORATE_USER") continue;
                var course = PtaClass(member);
                if (course is null || !classes.TryAdd(course.Id, course)) { invalid++; continue; }
            }
            if (classes.Count == 0) invalid++;
            groups[item.Id] = classes.Values.ToArray();
        }
        return new(invalid > 0 ? "partial_tasks" : "ok", items.Values.ToArray(), groups, list.GetArrayLength(), total, invalid);
    }
    private static MetadataCourse? PtaClass(JsonElement value)
    {
        var id = Text(value, "id"); var name = Text(value, "name").Trim();
        return PtaId(id) && ValidText(name, 300) && Text(value, "type") is "STUDENT_USER" or "LEGACY"
            ? new(id, name, "class", "教学课程 · 题目集") : null;
    }
    private static PtaProblemSet? PtaSet(JsonElement value)
    {
        var id = Text(value, "id"); var name = Text(value, "name").Trim();
        if (!PtaId(id) || !ValidText(name, 300)) return null;
        var timeType = Text(value, "timeType"); var begin = PtaTime(Property(value, "startAt")); var end = PtaTime(Property(value, "endAt"));
        var valid = timeType == "ALWAYS_AVAILABLE" || timeType is "FIXED_TIME" or "FIXED_DURATION" && begin is not null && end is not null && begin <= end;
        if (begin is not null && end is not null && begin > end) { begin = end = null; valid = false; }
        var evidence = "timeType=" + PtaScalar(value, "timeType") + "; startAt=" + PtaScalar(value, "startAt") + "; endAt=" + PtaScalar(value, "endAt");
        return new(id, name, timeType, Text(value, "type"), Text(value, "feature"), begin, end, valid, evidence, Text(value, "stage"));
    }
    private static bool PtaPaging(JsonElement root, JsonElement list, int page, int limit, out int total)
    {
        total = Number(root, "total") ?? -1;
        if (page is < 1 or > MetadataSynchronizer.MaximumPages || total is < 0 or > 1000000 || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() > limit || (long)(page - 1) * limit > total) return false;
        return list.GetArrayLength() == Math.Min(limit, Math.Max(0, total - (page - 1) * limit));
    }
    private static int PtaPages(int total, int limit) => Math.Max(1, (total + limit - 1) / limit);
    private static DateTimeOffset? PtaTime(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 100 } text
            || !Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            || time.Year is < 2000 or > 2100) return null;
        return time.ToUniversalTime();
    }
    private static string PtaScalar(JsonElement value, string field)
    {
        var element = Property(value, field);
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!, JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            JsonValueKind.Null => "null", JsonValueKind.Undefined => "未提供", _ => "字段无法识别"
        };
        return text.Length <= 256 && !text.Any(char.IsControl) ? text : "字段无法识别";
    }

    public static PtaTaskResult PtaTask(string json, string account, MetadataCourse course, PtaProblemSet listed)
    {
        if (!PtaId(account) || !PtaCourseValid(course) || !PtaId(listed.Id)) return new("account_mismatch", null);
        var outcome = PtaEnvelope("pta-exam", json);
        if (outcome != "ok") return new(outcome, PtaUnverifiedTask(account, course, listed));
        using var document = Document(json); var root = document!.RootElement; var set = PtaSet(Property(root, "problemSet"));
        if (set is null || set.Id != listed.Id) return new("task_mismatch", null);
        var exam = Property(root, "exam"); var hasExam = exam.ValueKind == JsonValueKind.Object;
        if (exam.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) return new("unrecognized_tasks", PtaUnverifiedTask(account, course, listed));
        if (hasExam && (Text(exam, "userId") != account || Text(exam, "problemSetId") != set.Id || !PtaId(Text(exam, "id"))))
            return new("account_mismatch", null);
        var globalStatus = Text(root, "status"); var examStatus = Text(exam, "status"); var ended = Property(exam, "ended");
        // PTA's protobuf JSON omits false boolean fields. An omitted ended flag cannot prove submission.
        var endedFalse = ended.ValueKind is JsonValueKind.False or JsonValueKind.Undefined;
        var regular = set.Type is "EXERCISE" or "RANDOM_GENERATION" && set.Feature is "" or "NONE" or "RANDOM";
        TaskState? state = null; var status = "状态待核对"; var explanation = "个人答卷状态尚未完整确认，请在官网核对。";
        if (regular && !hasExam && globalStatus is "PENDING" or "READY" or "END")
        { state = TaskState.Pending; status = globalStatus == "END" ? "未开始 · 题目集已关闭" : "尚未开始"; explanation = "官网未返回个人答卷；题目集开放或关闭不代表已完成。"; }
        else if (regular && hasExam && examStatus == "PROCESSING" && endedFalse)
        { state = TaskState.Pending; status = "答题中"; explanation = "个人答卷正在进行；已有分数或通过题数不代表结束答题。"; }
        else if (regular && hasExam && examStatus == "END" && ended.ValueKind == JsonValueKind.True)
        { state = TaskState.Completed; status = "已结束答题"; explanation = "个人答卷 status=END 且 ended=true；官网明确记录考生已结束答题，成绩以官网为准。"; }
        else if (regular && hasExam && examStatus == "END" && endedFalse)
        { status = "答题已停止 · 完成待核对"; explanation = "答卷已停止，但未提供考生主动结束标记；截止时间到达或已有成绩不能据此判断完成。"; }
        else if (!regular) explanation = "互评或特殊题目集的完整流程暂未覆盖，请在官网核对完成状态。";
        var deadline = set.TimeType == "ALWAYS_AVAILABLE" ? null : set.EndUtc;
        var timeVerified = set.TimeVerified;
        if (hasExam && endedFalse && set.TimeType is "FIXED_DURATION" or "FIXED_TIME")
        {
            var candidateEnd = PtaTime(Property(exam, "endAt"));
            if (candidateEnd is not null && (set.BeginUtc is null || candidateEnd >= set.BeginUtc)) deadline = candidateEnd;
            else { deadline = null; timeVerified = false; }
        }
        // exam.endAt becomes the actual finish time after ended=true; retain the scheduled set deadline then.
        if (!timeVerified) deadline = null;
        var timeText = set.TimeText + "; exam.startAt=" + PtaScalar(exam, "startAt") + "; exam.endAt=" + PtaScalar(exam, "endAt")
            + "; exam.delayedEndAt=" + PtaScalar(exam, "delayedEndAt") + "; deadlineSource="
            + (set.TimeType == "ALWAYS_AVAILABLE" ? "ALWAYS_AVAILABLE" : hasExam && endedFalse ? "exam.endAt" : "problemSet.endAt");
        var task = new PlatformCollectedTask("pta", account, course.Id, "class", set.Id, "problem-set", set.Name, course.Name, state,
            status + "（status=" + PtaScalar(root, "status") + "; exam.status=" + PtaScalar(exam, "status") + "; ended=" + PtaScalar(exam, "ended") + "）",
            state is null ? "unknown" : "explicit", explanation, set.BeginUtc, deadline, timeText, MetadataTaskNavigation.Pta(set.Id))
        { MetadataVersion = 2, NoDeadline = set.TimeType == "ALWAYS_AVAILABLE", ExamMode = set.Type };
        return new(state is null || !timeVerified ? "partial_tasks" : "ok", task);
    }
    public static PlatformCollectedTask PtaUnverifiedTask(string account, MetadataCourse course, PtaProblemSet set)
        => new("pta", account, course.Id, "class", set.Id, "problem-set", set.Name, course.Name, null, "个人答卷状态待核对", "unknown",
            "题目集已确认，个人答卷未能完整读取；请打开官网核对。", set.BeginUtc, set.TimeVerified && set.TimeType == "FIXED_TIME" ? set.EndUtc : null,
            set.TimeText + "; 个人截止时间待核对", MetadataTaskNavigation.Pta(set.Id))
        { MetadataVersion = 1, NoDeadline = set.TimeType == "ALWAYS_AVAILABLE", ExamMode = set.Type };
}
