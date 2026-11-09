using System.Globalization;
using System.Text.Json;

namespace AccessProbe;

public static partial class MetadataPlatformParser
{
    public static bool UcampusContentSegment(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static bool UcampusLeafType(string value) => value is "task" or "ut" or "text" or "video" or "audio" or "check"
        or "discussion" or "Interaction" or "Vocabulary" or "exercise";
    public static bool ValidUcampusContentCourse(MetadataCourse? course) => ValidUcampusCourse(course)
        && course!.Ucampus!.ContentId is { Length: > 0 and <= 128 } content
        && content.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '+' or ':')
        && UcampusContentSegment(course.Ucampus.Version);
    private static bool UniqueObject(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
    private static bool TryRequired(JsonElement strategies, out bool required)
    {
        var value = Property(strategies, "required"); required = value.ValueKind == JsonValueKind.True;
        return UniqueObject(strategies) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
    private static string ProgressScalar(JsonElement state, string field) => Text(state, field) is "0" or "1" or "2" or "true" or "false" ? Text(state, field) : "unrecognized";
    private static string PublishVersion(JsonElement value, string field = "publish_version")
    {
        var text = Text(value, field);
        return text is { Length: > 0 and <= 18 } && text.All(char.IsAsciiDigit) && text.Any(character => character != '0') ? text.TrimStart('0') : "";
    }
    private static UcampusLearningWindow LearningWindow(JsonElement strategies)
    {
        var start = Property(strategies, "start_time"); var end = Property(strategies, "end_time");
        var startOk = LearningTime(start, out var begin); var endOk = LearningTime(end, out var deadline);
        return new(UniqueObject(strategies) && startOk && endOk && !(begin > deadline), begin, deadline,
            JsonSerializer.Serialize(new { start_time = TimeEvidence(start), end_time = TimeEvidence(end) }));
    }
    private static object? TimeEvidence(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null
        : LearningTime(value, out var date) && date is { } time ? time.ToUnixTimeSeconds() : "unrecognized";
    private static bool LearningTime(JsonElement value, out DateTimeOffset? date)
    {
        date = null; if (value.ValueKind == JsonValueKind.Null) return true;
        var text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is not { Length: > 0 and <= 10 } || !text.All(char.IsAsciiDigit)
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 946684800 or > 4102444799) return false;
        date = DateTimeOffset.FromUnixTimeSeconds(seconds); return true;
    }
    private static UcampusLearningWindow CombineWindows(params UcampusLearningWindow[] windows)
    {
        var begin = windows.Where(window => window.BeginUtc.HasValue).Select(window => window.BeginUtc).Max();
        var end = windows.Where(window => window.DeadlineUtc.HasValue).Select(window => window.DeadlineUtc).Min();
        return new(windows.All(window => window.Valid) && !(begin > end), begin, end, "");
    }
    public static UcampusUnitCatalog UcampusUnitsCatalog(string catalogJson, string progressJson, string infoJson, MetadataCourse course, string account)
    {
        UcampusUnitCatalog Failure(string outcome) => new(outcome, "", []);
        if (!ValidUcampusContentCourse(course) || !MetadataTaskNavigation.IsId(account)) return Failure("course_binding_mismatch");
        using var catalogDocument = Document(catalogJson); using var progressDocument = Document(progressJson); using var infoDocument = Document(infoJson);
        if (catalogDocument is null || progressDocument is null || infoDocument is null) return Failure("unrecognized_unit_catalog");
        var catalogRoot = catalogDocument.RootElement; var progressRoot = progressDocument.RootElement; var info = infoDocument.RootElement;
        if (!UniqueObject(catalogRoot) || !UniqueObject(progressRoot) || !UniqueObject(info)
            || Text(catalogRoot, "code") != "0" || Text(progressRoot, "code") != "0" || Text(info, "code") != "0") return Failure("unrecognized_unit_catalog");
        var catalog = Property(catalogRoot, "rt"); var progress = Property(progressRoot, "rt");
        if (!UniqueObject(catalog) || !UniqueObject(progress) || Text(catalog, "tutorialId") != course.Ucampus!.ContentId
            || Text(progress, "tutorialId") != course.Ucampus.ContentId || Text(progress, "open_id") != account) return Failure("course_binding_mismatch");
        var version = PublishVersion(catalog);
        if (version.Length == 0 || PublishVersion(progress) != version || PublishVersion(info) != version) return Failure("content_version_mismatch");
        var infoCourse = Property(info, "course");
        using var structureDocument = infoCourse.ValueKind == JsonValueKind.String ? Document(infoCourse.GetString()!) : null;
        var structure = structureDocument?.RootElement ?? infoCourse;
        if (!UniqueObject(structure) || Text(structure, "type") != "0") return Failure("unsupported_content_scope");
        var toc = Property(catalog, "toc"); var units = Property(progress, "units"); var structuralUnits = Property(structure, "units");
        if (toc.ValueKind != JsonValueKind.Array || !UniqueObject(units) || structuralUnits.ValueKind != JsonValueKind.Array
            || toc.GetArrayLength() > 200 || structuralUnits.GetArrayLength() != toc.GetArrayLength()) return Failure("unrecognized_unit_catalog");
        var definitions = new List<UcampusUnitDefinition>(); var seen = new HashSet<string>(StringComparer.Ordinal); var nodes = 0;
        foreach (var unit in toc.EnumerateArray())
        {
            var id = Text(unit, "id"); var name = Text(unit, "name").Trim(); var caption = Text(unit, "caption").Trim();
            if (!UniqueObject(unit) || !UcampusContentSegment(id) || Text(unit, "url") != id || Text(unit, "role") != "unit"
                || !ValidText(name, 240) || !seen.Add(id) || !units.TryGetProperty(id, out var unitProgress) || !UniqueObject(unitProgress))
                return Failure("unrecognized_unit_catalog");
            if (ValidText(caption, 160) && caption != name && name.Length + caption.Length < 260) name += " · " + caption;
            var leafs = new List<UcampusUnitLeaf>(); var micros = new List<string>(); var leafIds = new HashSet<string>(StringComparer.Ordinal);
            var routes = new HashSet<string>(StringComparer.Ordinal);
            if (!ReadChildren(unit, id, "", 0) || leafs.Count > 2000) return Failure("unrecognized_unit_catalog");
            var state = Property(unitProgress, "state"); var strategies = Property(unitProgress, "strategies");
            var evidence = JsonSerializer.Serialize(new { pass = ProgressScalar(state, "pass"), pass2 = ProgressScalar(state, "pass2"),
                perm = ProgressScalar(state, "perm"), state_required = ProgressScalar(state, "required"),
                strategy_required = ProgressScalar(strategies, "required") });
            definitions.Add(new(id, name, leafs, micros, LearningWindow(strategies), evidence));

            bool ReadChildren(JsonElement node, string route, string micro, int depth)
            {
                if (depth > 20 || ++nodes > 20000) return false;
                var children = Property(node, "children");
                if (children.ValueKind != JsonValueKind.Array) return false;
                foreach (var child in children.EnumerateArray())
                {
                    if (!UniqueObject(child) || !UcampusContentSegment(Text(child, "url"))) return false;
                    var childRoute = route + "/" + Text(child, "url"); if (!routes.Add(childRoute)) return false;
                    var role = Text(child, "role");
                    if (role is "group" or "link")
                    {
                        var leafId = Text(child, "id"); var tabType = Text(child, "tab_type");
                        if (!UcampusContentSegment(leafId) || !UcampusLeafType(tabType) || !leafIds.Add(leafId)) return false;
                        var nested = Property(child, "children"); if (nested.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                            && (nested.ValueKind != JsonValueKind.Array || nested.GetArrayLength() != 0)) return false;
                        leafs.Add(new(leafId, childRoute, micro, tabType));
                    }
                    else if (role is "node" or "section")
                    {
                        var nextMicro = micro;
                        if (depth == 0) { nextMicro = childRoute; micros.Add(childRoute); }
                        if (!ReadChildren(child, childRoute, nextMicro, depth + 1)) return false;
                    }
                    else return false;
                }
                return true;
            }
        }
        if (!seen.SetEquals(units.EnumerateObject().Select(field => field.Name))) return Failure("unit_catalog_changed");
        // Check the public course structure independently without reading question/group bodies.
        var structuralIds = structuralUnits.EnumerateArray().Select(unit => Text(unit, "id").Length > 0 ? Text(unit, "id") : Text(unit, "url")).ToArray();
        if (structuralIds.Distinct(StringComparer.Ordinal).Count() != seen.Count || !seen.SetEquals(structuralIds)) return Failure("unit_catalog_changed");
        return new("ok", version, definitions);
    }

    public static MetadataTaskBatch UcampusUnitProgress(string json, string account, MetadataCourse course, UcampusUnitCatalog catalog, UcampusUnitDefinition unit)
    {
        MetadataTaskBatch Failure(string outcome) => new(outcome, [], 1, 0, [outcome]);
        if (!ValidUcampusContentCourse(course) || !MetadataTaskNavigation.IsId(account) || catalog.Outcome != "ok"
            || !catalog.Units.Contains(unit)) return Failure("course_binding_mismatch");
        using var document = Document(json); if (document is null || !UniqueObject(document.RootElement) || Text(document.RootElement, "code") != "0")
            return Failure("unrecognized_unit_progress");
        var rt = Property(document.RootElement, "rt");
        if (!UniqueObject(rt) || Text(rt, "open_id") != account || Text(rt, "tutorialId") != course.Ucampus!.ContentId
            || Text(rt, "unit_id") != unit.Id || PublishVersion(rt) != catalog.PublishVersion) return Failure("unit_binding_mismatch");
        var leafs = Property(rt, "leafs"); var micros = Property(rt, "micros");
        if (!UniqueObject(leafs) || !UniqueObject(micros) || !unit.Leafs.Select(leaf => leaf.Id).ToHashSet(StringComparer.Ordinal).SetEquals(leafs.EnumerateObject().Select(field => field.Name))
            || !unit.Micros.ToHashSet(StringComparer.Ordinal).SetEquals(micros.EnumerateObject().Select(field => field.Name))) return Failure("unit_catalog_changed");
        var required = new List<(UcampusUnitLeaf Leaf, int? Pass, UcampusLearningWindow Window)>(); var evidence = new List<object>();
        var microWindows = new Dictionary<string, UcampusLearningWindow>(StringComparer.Ordinal); var requiredMicros = 0;
        foreach (var micro in micros.EnumerateObject())
        {
            var row = micro.Value; var strategies = Property(row, "strategies"); var state = Property(row, "state");
            if (!UniqueObject(row) || !UniqueObject(state) || !TryRequired(strategies, out var needed)) return Failure("unrecognized_unit_progress");
            if (needed) requiredMicros++;
            var window = LearningWindow(strategies); microWindows.Add(micro.Name, window);
            evidence.Add(new { kind = "chapter", id = micro.Name, required = needed, pass = ProgressScalar(state, "pass"), pass2 = ProgressScalar(state, "pass2"), time = window.Evidence });
        }
        foreach (var leaf in unit.Leafs)
        {
            var row = Property(leafs, leaf.Id); var strategies = Property(row, "strategies"); var state = Property(row, "state");
            // Catalog presentation types and progress types are independent in the official menu. Unvisited checkpoints can use text in progress.
            if (!UniqueObject(row) || !UniqueObject(state) || !TryRequired(strategies, out var needed) || !UcampusLeafType(Text(row, "tab_type")))
                return Failure("unrecognized_unit_progress");
            int? pass = Text(state, "pass") switch { "0" => 0, "1" => 1, "2" => 2, _ => null };
            var window = LearningWindow(strategies); var effective = leaf.Micro.Length > 0
                ? CombineWindows(unit.Window, microWindows[leaf.Micro], window) : CombineWindows(unit.Window, window);
            if (needed) required.Add((leaf, pass, effective));
            evidence.Add(new { kind = "item", id = leaf.Id, required = needed, catalogType = leaf.TabType, progressType = Text(row, "tab_type"),
                pass = ProgressScalar(state, "pass"), pass2 = ProgressScalar(state, "pass2"), time = window.Evidence });
        }
        var done = required.Count(item => item.Pass == 1); var unknown = required.Count(item => item.Pass is null);
        var incomplete = required.Where(item => item.Pass != 1).ToArray();
        var timesValid = unit.Window.Valid && required.All(item => item.Window.Valid);
        var consistency = !(required.Count == 0 && requiredMicros > 0);
        TaskState? status = unknown > 0 || !consistency ? null : required.Count == 0 ? TaskState.Ignored
            : done == required.Count ? TaskState.Completed : TaskState.Pending;
        var relevant = incomplete.Length > 0 ? incomplete : required.ToArray();
        var timed = relevant.Where(item => item.Window.DeadlineUtc.HasValue).OrderBy(item => item.Window.DeadlineUtc).ThenBy(item => item.Leaf.Route, StringComparer.Ordinal).ToArray();
        var deadline = timesValid ? timed.FirstOrDefault().Window?.DeadlineUtc ?? unit.Window.DeadlineUtc : null;
        var begin = timesValid ? timed.FirstOrDefault().Window?.BeginUtc ?? unit.Window.BeginUtc : null;
        var noDeadline = timesValid && deadline is null;
        if (!timesValid) status = status == TaskState.Completed ? TaskState.Completed : null;
        var entryLeaf = timed.FirstOrDefault().Leaf ?? incomplete.FirstOrDefault().Leaf ?? required.FirstOrDefault().Leaf ?? unit.Leafs.FirstOrDefault();
        var link = entryLeaf is not null ? MetadataTaskNavigation.UcampusUnit(course, account, unit.Id, entryLeaf.Route) : null;
        var progress = consistency && unknown == 0 && required.Count > 0 ? decimal.Round(done * 100m / required.Count, 2) : (decimal?)null;
        var statusText = required.Count > 0 ? $"必学已完成 {done}/{required.Count} 项" : "本单元没有必学项 · 选学";
        if (unknown > 0 || !consistency) statusText += " · 完成情况待核对";
        if (!timesValid) statusText += " · 时间待核对";
        var unitTime = "单元开放：" + LearningTimeText(unit.Window.BeginUtc, unit.Window.Valid) + "；单元截止：" + LearningTimeText(unit.Window.DeadlineUtc, unit.Window.Valid);
        var timeText = unitTime + (deadline != unit.Window.DeadlineUtc && deadline is not null
                ? (status == TaskState.Completed ? "；必学最早截止：" : "；未完必学最早截止：") + LearningTimeText(deadline) : "")
            + (timesValid ? noDeadline ? "；无截止时间" : "" : "；原始时间未完整识别");
        var explanation = status == TaskState.Completed ? "目录与进度逐项核对，全部必学项 pass=1；选学项和单元汇总状态不影响必学完成判断。"
            : status == TaskState.Ignored ? "目录与进度已完整核对，当前没有标为必学的项目；保留单元截止日期，默认不列为待办。"
            : status is null ? "必学完成情况或时间尚未完整确认，请在官网核对；不依据单元汇总状态猜测完成。"
            : "仍有必学项未完成；pass=0 或 pass=2 均保留待办，按未完必学项最早有效截止日期排序。";
        var task = new PlatformCollectedTask("ucampus", account, course.Id, "legacy", MetadataTaskNavigation.UcampusUnitId(course.Ucampus.ContentId, unit.Id),
            "unit", unit.Name + " · 单元学习", course.Name, status, statusText, status is null ? "unrecognized" : "explicit", explanation,
            begin, deadline, timeText, link)
        {
            MetadataVersion = 3, NoDeadline = noDeadline, ProgressPercent = progress,
            LearningEvidence = JsonSerializer.Serialize(new { contract = "ucampus-unit-progress-v2", publishVersion = catalog.PublishVersion,
                unitState = unit.StateEvidence, unitTime = unit.Window.Evidence, required = required.Count, completed = done, unknown, checkpoints = evidence })
        };
        var partial = status is null || !timesValid || link is null;
        return new(partial ? "partial_learning_unit" : "ok", [task], 0, 0, partial ? ["partial_learning_unit"] : [])
            { TotalRows = 1, Rows = 1, TotalPages = 1, ListComplete = true };
    }
    private static string LearningTimeText(DateTimeOffset? date, bool confirmed = true) => date is { } value
        ? value.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "（北京时间）" : confirmed ? "不限时" : "待核对";
}
