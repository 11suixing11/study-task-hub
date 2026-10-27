using System.Globalization;
using System.Text.Json;

namespace AccessProbe;

public sealed record RainCatalog(string Outcome, IReadOnlyList<RainClassroom> Courses, bool Covered, int DuplicateRows = 0);
public sealed record RainTaskBatch(string Outcome, IReadOnlyList<CollectedTask> Tasks, IReadOnlyList<string> Issues,
    bool? HasMore = null, bool HasLeafContent = false);
public sealed record RainSchedule(string Outcome, IReadOnlyDictionary<string, JsonElement> Leaves);

// Only whitelisted course/task metadata is projected. No problem/answer/detail APIs are requested.
public static class RainClassroomParser
{
    public static string EnvelopeOutcome(string json)
    {
        using var document = Document(json);
        return document is null ? "invalid_json" : Envelope(document.RootElement);
    }

    private static string Envelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "unrecognized_response";
        var signaled = false; var failed = false;
        foreach (var name in new[] { "errcode", "error_code", "code" })
        {
            if (!root.TryGetProperty(name, out var code) || code.ValueKind == JsonValueKind.Null) continue;
            signaled = true;
            if (Number(code) != 0) failed = true;
        }
        if (root.TryGetProperty("success", out var success))
        {
            signaled = true;
            if (success.ValueKind != JsonValueKind.True) failed = true;
        }
        if (failed)
        {
            var message = Text(root, "errmsg", "msg", "message");
            if (message.Contains("登录", StringComparison.Ordinal) || message.Contains("not login", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)) return "login_required";
            return "api_error";
        }
        return signaled && root.TryGetProperty("data", out _) ? "ok" : "unrecognized_response";
    }

    public static string? AccountId(string json)
    {
        using var document = Document(json);
        if (document is null || Envelope(document.RootElement) != "ok") return null;
        var data = Field(document.RootElement, "data");
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 1) data = data[0];
        var id = Id(data, "user_id");
        return RainAddressPolicy.IsId(id) ? id : null;
    }

    public static RainCatalog Catalog(string json)
    {
        using var document = Document(json);
        if (document is null) return new("invalid_json", [], false);
        var outcome = Envelope(document.RootElement);
        if (outcome != "ok") return new(outcome, [], false);
        var data = Field(document.RootElement, "data");
        var list = Field(data, "list");
        if (list.ValueKind != JsonValueKind.Array) return new("unrecognized_catalog", [], false);
        if (list.GetArrayLength() > 500) return new("catalog_limit", [], false);
        var courses = new Dictionary<string, RainClassroom>(StringComparer.Ordinal);
        var covered = true; var duplicates = 0;
        foreach (var row in list.EnumerateArray())
        {
            // identity=2 returns classrooms with a nested course; some campus deployments group classrooms.
            var classrooms = Field(row, "classrooms");
            if (classrooms.ValueKind == JsonValueKind.Array)
            {
                foreach (var classroom in classrooms.EnumerateArray()) Add(classroom, Text(row, "name", "course_name"));
            }
            else Add(row, "");
        }
        if (courses.Count > 200) return new("course_limit", courses.Values.Take(200).ToArray(), false, duplicates);
        return new(covered ? "ok" : "partial_catalog", courses.Values.ToArray(), covered, duplicates);

        void Add(JsonElement row, string parentName)
        {
            var course = ParseClassroom(row, parentName);
            if (course is null) { covered = false; return; }
            if (!courses.TryAdd(course.Id, course)) duplicates++;
        }
    }

    public static RainClassroom? Classroom(string json, string expectedId, RainClassroom? catalogRoom = null)
    {
        using var document = Document(json);
        if (document is null || Envelope(document.RootElement) != "ok") return null;
        if (catalogRoom is not null && catalogRoom.Id != expectedId) return null;
        var course = ParseClassroom(Field(document.RootElement, "data"), catalogRoom?.CourseName ?? "");
        if (course?.Id != expectedId) return null;
        return catalogRoom is null ? course : course with
        {
            CourseId = course.CourseId.Length > 0 ? course.CourseId : catalogRoom.CourseId,
            Sign = course.Sign.Length > 0 ? course.Sign : catalogRoom.Sign,
            UniversityId = course.UniversityId.Length > 0 ? course.UniversityId : catalogRoom.UniversityId,
            PlatformId = course.PlatformId.Length > 0 ? course.PlatformId : catalogRoom.PlatformId,
            HasChapterContent = course.HasChapterContent ?? catalogRoom.HasChapterContent
        };
    }

    private static RainClassroom? ParseClassroom(JsonElement row, string parentName)
    {
        if (row.ValueKind != JsonValueKind.Object || row.TryGetProperty("leaf_type", out _)
            || row.TryGetProperty("section_leaf_list", out _)) return null;
        var id = Id(row, "classroom_id", "id");
        var nested = Field(row, "course");
        var courseName = Text(row, "course_name");
        if (courseName.Length == 0) courseName = Text(nested, "name");
        if (courseName.Length == 0) courseName = parentName;
        var name = Text(row, "name", "classroom_name");
        if (name.Length == 0) name = courseName;
        if (courseName.Length == 0) courseName = name;
        if (!RainAddressPolicy.IsId(id) || name.Length == 0 || name.Length > 200 || courseName.Length > 200) return null;
        var courseId = Id(row, "course_id");
        if (courseId.Length == 0) courseId = Id(nested, "id");
        var sign = Text(row, "course_sign");
        if (sign.Length > 0 && !RainAddressPolicy.IsToken(sign)) return null;
        var university = Id(row, "university_id", "uv_id");
        var platform = Id(row, "platform_id");
        var chapter = Bool(row, "has_chapter_content") ?? (sign.Length > 0 ? true : Bool(row, "is_pro"));
        return new(name, courseName, id, courseId, sign, university, platform, chapter);
    }

    public static RainSchedule Schedule(string json)
    {
        using var document = Document(json);
        if (document is null) return new("invalid_json", new Dictionary<string, JsonElement>());
        var outcome = Envelope(document.RootElement);
        var schedules = Field(Field(document.RootElement, "data"), "leaf_schedules");
        if (outcome != "ok" || schedules.ValueKind != JsonValueKind.Object)
            return new(outcome == "ok" ? "unrecognized_schedule" : outcome, new Dictionary<string, JsonElement>());
        if (schedules.EnumerateObject().Take(10001).Count() > 10000)
            return new("task_limit", new Dictionary<string, JsonElement>());
        return new("ok", schedules.EnumerateObject().Where(property => RainAddressPolicy.IsId(property.Name))
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal));
    }

    public static RainTaskBatch Chapter(string json, RainPlatform platform, string host, string accountId,
        RainClassroom room, RainSchedule schedule)
    {
        using var document = Document(json);
        if (document is null) return new("invalid_json", [], []);
        var outcome = Envelope(document.RootElement);
        if (outcome != "ok") return new(outcome, [], []);
        var chapters = Field(Field(document.RootElement, "data"), "course_chapter");
        if (chapters.ValueKind != JsonValueKind.Array) return new("unrecognized_chapter", [], []);
        var tasks = new List<CollectedTask>(); var issues = new HashSet<string>(StringComparer.Ordinal);
        var visited = 0;
        foreach (var chapter in chapters.EnumerateArray()) Walk(chapter, Id(chapter, "id"), 0, false);
        return new(issues.Contains("tree_shape") || issues.Contains("tree_limit") ? "partial_tasks" : "ok",
            tasks, issues.ToArray(), HasLeafContent: true);

        void Walk(JsonElement node, string chapterId, int depth, bool locked)
        {
            if (++visited > 10000 || depth > 12) { issues.Add("tree_limit"); return; }
            if (node.ValueKind != JsonValueKind.Object) { issues.Add("tree_shape"); return; }
            locked |= Bool(node, "is_locked") == true;
            var ownChapter = Id(node, "chapter_id");
            if (ownChapter.Length > 0) chapterId = ownChapter;
            var leafType = Number(Field(node, "leaf_type"));
            if (leafType is 5 or 6)
            {
                if (locked) return; // show_unpublished=0 and is_locked never expose future/unpublished tasks.
                var id = Id(node, "id"); var title = Text(node, "name", "title");
                if (!RainAddressPolicy.IsId(id) || title.Length is 0 or > 300) { issues.Add("invalid_task"); return; }
                var type = leafType == 6 ? "exercise" : "exam";
                var leafSchedule = schedule.Leaves.GetValueOrDefault(id);
                var state = ReadLeafState(node, leafSchedule, leafType == 5);
                var times = Times(node, "score_deadline", "start_time");
                if (times.Invalid) issues.Add("invalid_time");
                var link = RainTaskNavigation.LeafLink(platform, host, room.Id, id, type, chapterId);
                tasks.Add(Row(platform, host, accountId, room, id, type, title, state, times, link));
                return;
            }
            var hasContainer = false;
            foreach (var name in new[] { "section_leaf_list", "leaf_list" })
            {
                if (!node.TryGetProperty(name, out var children)) continue;
                hasContainer = true;
                if (children.ValueKind != JsonValueKind.Array) { issues.Add("tree_shape"); continue; }
                foreach (var child in children.EnumerateArray()) Walk(child, chapterId, depth + 1, locked);
            }
            if (leafType is null && !hasContainer) issues.Add("tree_shape");
        }
    }

    public static RainTaskBatch Activities(string json, RainPlatform platform, string host, string accountId,
        RainClassroom room, RainSchedule schedule)
    {
        using var document = Document(json);
        if (document is null) return new("invalid_json", [], []);
        var outcome = Envelope(document.RootElement);
        if (outcome != "ok") return new(outcome, [], []);
        var data = Field(document.RootElement, "data");
        var activities = Field(data, "activities");
        var more = Bool(data, "has_more");
        if (activities.ValueKind != JsonValueKind.Array || more is null)
            return new("unrecognized_activities", [], []);
        if (activities.GetArrayLength() > 1000) return new("task_limit", [], []);
        var tasks = new List<CollectedTask>(); var issues = new HashSet<string>(StringComparer.Ordinal);
        var leafContent = false;
        foreach (var activity in activities.EnumerateArray())
        {
            var type = Number(Field(activity, "type"));
            if (type is null) { issues.Add("activity_shape"); continue; }
            if (type == 15) { leafContent = true; continue; }
            if (type is not (4 or 5 or 19 or 20)) continue;
            if (Bool(activity, "is_locked") == true) continue;
            var title = Text(activity, "title");
            var content = Field(activity, "content");
            if (Bool(content, "is_third_party") == true) { issues.Add("external_task"); continue; }
            var modern = type is 19 or 20;
            leafContent |= modern;
            var id = modern ? Id(content, "leaf_id") : Id(activity, "courseware_id");
            if (!RainAddressPolicy.IsId(id) || title.Length is 0 or > 300) { issues.Add("invalid_task"); continue; }
            var kind = type == 4 ? "quiz" : type == 19 ? "exercise" : "exam";
            var state = modern ? ReadLeafState(content, schedule.Leaves.GetValueOrDefault(id), type == 20)
                : ReadLegacyState(activity);
            var times = modern ? Times(content, "score_d", "start_time") : Times(activity, "deadline", "start_time");
            if (times.Invalid) issues.Add("invalid_time");
            var link = modern ? RainTaskNavigation.LeafLink(platform, host, room.Id, id, kind, "")
                : RainTaskNavigation.LegacyLink(platform, host, room.Id, id, kind, state.State == TaskState.Completed);
            tasks.Add(Row(platform, host, accountId, room, id, kind, title, state, times, link));
        }
        return new(issues.Count > 0 ? "partial_tasks" : "ok", tasks, issues.ToArray(), more, leafContent);
    }

    private sealed record StateValue(TaskState? State, string Text, string Evidence, string Explanation, decimal? Progress, string Score);
    private static StateValue ReadLegacyState(JsonElement row)
    {
        var explicitValue = ExplicitState(row);
        var progress = Progress(Field(row, "progress"), Field(row, "problem_count"));
        var score = Score(row);
        if (explicitValue is not null) return new(explicitValue.Value.State, explicitValue.Value.Text, "explicit", "", progress, score);
        // Official /logs/learn renderer and quiz navigation: 0 unanswered, 1 answering, 2 submitted, 3 graded.
        return Number(Field(row, "status")) switch
        {
            0 => new(TaskState.Pending, "未作答", "explicit", "", progress, score),
            1 => new(TaskState.Pending, "作答中", "explicit", "", progress, score),
            2 => new(TaskState.Completed, "已提交 / 待批阅", "explicit", "", progress, score),
            3 => new(TaskState.Completed, "已批阅", "explicit", "", progress, score),
            _ => new(null, "状态待核对", "unknown", "平台未返回可识别的任务状态", progress, score)
        };
    }

    private static StateValue ReadLeafState(JsonElement leaf, JsonElement schedule, bool exam)
    {
        var progress = Progress(Field(schedule, "done"), Field(schedule, "total"));
        var score = Score(schedule);
        var explicitValue = ExplicitState(schedule) ?? ExplicitState(leaf);
        if (explicitValue is not null) return new(explicitValue.Value.State, explicitValue.Value.Text, "explicit", "", progress, score);
        if (exam)
        {
            // Exam schedule codes belong to the exam renderer; never apply them to exercises.
            var status = Number(Field(schedule, "status"));
            if (status == 1) return new(TaskState.Pending, "未开始", "explicit", "", progress, score);
            if (status == 2) return new(TaskState.Pending, "作答中", "explicit", "", progress, score);
            if (status is 3 or 4 or 5) return new(TaskState.Completed, status == 3 ? "已提交 / 待批阅" : "已批阅", "explicit", "", progress, score);
        }
        if (progress is { } percent)
        {
            if (percent == 100)
                return new(TaskState.Completed, "已完成（平台题目进度）", "platform_progress",
                    "平台课程页按完成题数显示已完成；提交记录请在作业页核对", percent, score);
            return new(TaskState.Pending, percent == 0 ? "未开始" : "作答中", "platform_progress", "按平台题目进度同步", percent, score);
        }
        return new(null, "状态待核对", "unknown", "平台未返回可识别的状态或题目进度", null, score);
    }

    private static (TaskState State, string Text)? ExplicitState(JsonElement row)
    {
        var text = Text(row, "status_text", "state_name");
        if (ChaoxingTaskMetadata.ReadState(text) is { } state) return (state, text);
        if (Bool(row, "is_submitted") == true) return (TaskState.Completed, "已提交");
        if (Bool(row, "is_completed") == true) return (TaskState.Completed, "已完成");
        return null;
    }

    private sealed record TimeValue(DateTimeOffset? Begin, DateTimeOffset? Deadline, string Text, bool Invalid);
    private static TimeValue Times(JsonElement row, string deadlineName, string beginName)
    {
        var deadlineValue = Field(row, deadlineName); var beginValue = Field(row, beginName);
        var deadline = Time(deadlineValue); var begin = Time(beginValue);
        var invalid = deadline is null && HasTimeValue(deadlineValue) || begin is null && HasTimeValue(beginValue)
            || deadline is not null && begin > deadline;
        if (begin > deadline && deadline is not null) begin = null;
        var text = deadline is { } end ? "截止时间：" + end.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "（北京时间）"
            : "平台未提供截止时间";
        return new(begin, deadline, text, invalid);
    }

    public static DateTimeOffset? Time(JsonElement value)
    {
        var number = Number(value);
        if (number is > 0)
        {
            try
            {
                var milliseconds = number.Value < 100000000000m ? number.Value * 1000m : number.Value;
                var result = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)decimal.Round(milliseconds, 0)));
                return result.Year is >= 2000 and <= 2100 ? result : null;
            }
            catch (ArgumentOutOfRangeException) { return null; }
            catch (OverflowException) { return null; }
        }
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()!.Trim();
        if (DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)
            && (text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-'))
            return SupportedTime(offset);
        if (DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return SupportedTime(new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(8)));
        return null;
    }

    private static DateTimeOffset? SupportedTime(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.Year is >= 2000 and <= 2100 ? utc : null;
    }

    private static bool HasTimeValue(JsonElement value) => value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        && !(value.ValueKind == JsonValueKind.String && value.GetString() == "") && Number(value) != 0;

    private static CollectedTask Row(RainPlatform platform, string host, string accountId, RainClassroom room,
        string id, string kind, string title, StateValue state, TimeValue time, string? link) =>
        new(platform.Id, host, accountId, room.Id, id, kind, title, room.CourseName, state.State,
            state.Text, state.Evidence, state.Explanation,
            time.Deadline is null ? DeadlineKind.Unknown : DeadlineKind.Known, time.Begin, time.Deadline,
            time.Text, state.Progress, state.Score, link);

    private static decimal? Progress(JsonElement done, JsonElement total)
    {
        var count = Number(done); var maximum = Number(total);
        if (count is null || maximum is null || maximum <= 0 || count < 0 || count > maximum || maximum > 1000000) return null;
        return decimal.Round(100m * count.Value / maximum.Value, 2);
    }

    private static string Score(JsonElement row)
    {
        var score = Number(Field(row, "score"));
        return score is >= 0 and <= 1000000 ? score.Value.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static JsonDocument? Document(string json)
    {
        if (json.Length > RainAddressPolicy.MaximumBytes) return null;
        try { return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 40 }); }
        catch (JsonException) { return null; }
    }
    private static JsonElement Field(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(name, out var value) ? value : default;
    private static string Text(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Field(node, name);
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
        }
        return "";
    }
    private static string Id(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Field(node, name);
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
            if (RainAddressPolicy.IsId(text)) return text;
        }
        return "";
    }
    private static decimal? Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }
    private static bool? Bool(JsonElement node, string name)
    {
        var value = Field(node, name);
        return value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false
            : Number(value) == 1 ? true : Number(value) == 0 ? false : null;
    }
}
