using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AccessProbe;

public static partial class MetadataPlatformParser
{
    public const int UcampusPageSize = 10;
    private static readonly Uri UcampusOrigin = new("https://u.unipus.cn/");
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(1);
    public static bool UcampusNumber(string? value, bool allowEmpty = false) => allowEmpty && value == ""
        || value is { Length: > 0 and <= 32 } && value.All(char.IsAsciiDigit);

    private static IDocument? UcampusHtml(string html) => Encoding.UTF8.GetByteCount(html) <= MaximumBytes
        && html.TrimStart().StartsWith('<') ? new HtmlParser().ParseDocument(html) : null;
    private static bool UcampusLoginHtml(IDocument document) => (document.Title ?? "").Contains("登录", StringComparison.Ordinal)
        || document.QuerySelector("form[action*='/user/comm/login'],form[action*='sso.unipus.cn']") is not null
        || document.Scripts.Any(script => Regex.IsMatch(script.TextContent,
            @"(?:location(?:\.href)?\s*=|location\.replace\s*\()\s*['""].*(?:/login|sso\.unipus\.cn)", RegexOptions.CultureInvariant, ScriptTimeout));

    public static string UcampusEnvelope(string operation, string body)
    {
        if (body.TrimStart().StartsWith('<'))
        {
            var html = UcampusHtml(body);
            if (html is null) return "unrecognized_response";
            if (UcampusLoginHtml(html) || body.Trim().StartsWith("<script>location='login'", StringComparison.Ordinal)) return "login_required";
            return operation is "courses" or "ucampus-course" or "ucampus-homework-entry" or "ucampus-exam-entry" ? "ok" : "unrecognized_response";
        }
        using var document = Document(body);
        if (document is null || document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return "unrecognized_response";
        var root = document.RootElement;
        if (Text(root, "code") == "401" || Text(root, "status") == "401"
            || Text(root, "msg").Contains("未登录", StringComparison.Ordinal) || Text(root, "message").Contains("未登录", StringComparison.Ordinal)) return "login_required";
        if (Text(root, "code") == "403" || Text(root, "status") == "403") return "course_access_denied";
        if (operation == "profile") return MetadataTaskNavigation.IsId(Text(root, "openId")) && ValidText(Text(root, "token"), 16384)
            && root.EnumerateObject().Count(field => field.Name is "openId" or "token") == 2 ? "ok" : "unrecognized_user";
        return operation is "ucampus-homework" or "ucampus-exam" or "ucampus-entry-signature" or "ucampus-content-info"
            or "ucampus-content-catalog" or "ucampus-content-progress" or "ucampus-unit-progress"
            && Text(root, "code") == "0" ? "ok" : "unrecognized_response";
    }
    public static string? UcampusAccount(IEnumerable<MetadataSessionCookie> cookies)
    {
        var address = new Uri(UcampusOrigin, "user/data/getToken");
        var values = cookies.Where(cookie => cookie.Name == "ucamUserId" && MetadataSessionStore.IsValid(MetadataPlatform.Ucampus, cookie)
            && cookie.AppliesTo(address) && !cookie.IsExpired(DateTimeOffset.UtcNow)).Select(cookie => cookie.Value).Distinct(StringComparer.Ordinal).ToArray();
        return values is [var value] && MetadataTaskNavigation.IsId(value) ? value : null;
    }
    public static string UcampusProfile(string json, string? account)
    {
        var outcome = UcampusEnvelope("profile", json); if (outcome != "ok") return outcome;
        if (!MetadataTaskNavigation.IsId(account)) return "unrecognized_user";
        using var document = Document(json);
        return Text(document!.RootElement, "openId") == account ? "ok" : "account_mismatch";
    }

    public static bool ValidUcampusCourse(MetadataCourse? course) => course is { Kind: "legacy", Ucampus: { } context }
        && UcampusNumber(course.ApiCourseId) && UcampusNumber(context.ClassId) && context.ClassId != "0"
        && UcampusNumber(context.EccId) && UcampusNumber(context.SchoolHint, true) && UcampusNumber(context.CourseType)
        && course.Id == MetadataTaskNavigation.UcampusCourseId(course.ApiCourseId, context);
    public static string UcampusCatalogAddress(MetadataCourse course)
    {
        if (!ValidUcampusCourse(course)) throw new ArgumentException("Unrecognized legacy course.");
        var context = course.Ucampus!;
        return "https://u.unipus.cn/user/student/mycourse/courseCatalog?courseId=" + course.ApiCourseId
            + "&school_id=" + context.SchoolHint + "&eccId=" + context.EccId + "&classId=" + context.ClassId + "&coursetype=" + context.CourseType;
    }
    public static MetadataCatalog UcampusCatalog(string html)
    {
        var document = UcampusHtml(html);
        if (document is null || UcampusLoginHtml(document)) return new(document is not null && UcampusLoginHtml(document) ? "login_required" : "unrecognized_catalog", [], 1, 0, 0, 0, 0);
        if (document.QuerySelector(".my_course_box .my_course_list") is null) return new("unrecognized_catalog", [], 1, 0, 0, 0, 0);
        var cards = document.QuerySelectorAll(".my_course_item.ite");
        if (cards.Length > 1000) return new("catalog_limit", [], 1, 0, cards.Length, cards.Length, 0);
        var courses = new Dictionary<string, MetadataCourse>(StringComparer.Ordinal); var conflicts = new HashSet<string>(); var invalid = 0; var duplicates = 0;
        foreach (var card in cards)
        {
            var fields = UcampusFields(card.QuerySelector(".hideurl")?.TextContent.Trim(), "/user/student/mycourse/courseCatalog");
            var title = card.QuerySelector(".my_course_name"); var name = (title?.GetAttribute("title") ?? title?.TextContent ?? "").Trim();
            if (fields is null || fields.Count != 5 || !fields.Keys.Order().SequenceEqual(new[] { "classId", "courseId", "coursetype", "eccId", "school_id" }.Order())
                || !ValidText(name, 200) || !card.HasAttribute("tutorialid") || card.GetAttribute("hastch") != "1") { invalid++; continue; }
            var context = new UcampusCourseContext(fields["classId"], fields["eccId"], fields["school_id"], fields["coursetype"]);
            var course = new MetadataCourse(MetadataTaskNavigation.UcampusCourseId(fields["courseId"], context), name, "legacy", "旧版班课 · 全部状态作业、测试列表")
                { ApiCourseId = fields["courseId"], Ucampus = context };
            if (!ValidUcampusCourse(course)) { invalid++; continue; }
            if (conflicts.Contains(course.Id)) { invalid++; continue; }
            if (courses.TryGetValue(course.Id, out var previous))
            {
                duplicates++; if (previous != course) { courses.Remove(course.Id); conflicts.Add(course.Id); invalid++; }
            }
            else courses.Add(course.Id, course);
        }
        // Promotional cards are excluded. An unfamiliar or truncated empty page is not an empty catalog.
        if (cards.Length == 0 && !document.QuerySelectorAll(".my_course_box").Any(node => node.TextContent.Contains("暂无", StringComparison.Ordinal)))
            return new("unrecognized_catalog", [], 1, 0, 0, 0, 0);
        return new(invalid > 0 ? "partial_catalog" : "ok", courses.Values.ToArray(), 1, 1, cards.Length, invalid, duplicates) { TotalRows = cards.Length };
    }
    public static IReadOnlyDictionary<string, string>? UcampusFields(string? address, string path)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsControl) || address.Contains('\\')
            || !Uri.TryCreate(UcampusOrigin, address, out var uri) || !TaskRules.IsSafeLink(uri.AbsoluteUri)
            || uri.IdnHost != "u.unipus.cn" || uri.AbsolutePath != path || uri.Fragment.Length > 0) return null;
        return RainAddressPolicy.Query(uri);
    }
    private static string? UcampusVariable(IDocument document, string name)
    {
        var values = document.Scripts.SelectMany(script => Regex.Matches(script.TextContent, @"\bvar\s+" + name
            + @"\s*=\s*['""](?<value>[A-Za-z0-9_+:-]*)['""]", RegexOptions.CultureInvariant, ScriptTimeout).Select(match => match.Groups["value"].Value)).Distinct().ToArray();
        return values is [var value] ? value : null;
    }
    public static UcampusCourseResult UcampusCourse(string html, MetadataCourse course)
    {
        if (!ValidUcampusCourse(course)) return new("unrecognized_course");
        var document = UcampusHtml(html); if (document is null) return new("unrecognized_course");
        if (UcampusLoginHtml(document)) return new("login_required");
        var context = course.Ucampus!; var content = UcampusVariable(document, "courseId");
        if (UcampusVariable(document, "dbCourseId") != course.ApiCourseId || UcampusVariable(document, "classId") != context.ClassId
            || content is not { Length: > 0 and <= 128 }) return new("course_binding_mismatch");
        var entries = document.QuerySelectorAll("a[href]").Select(link => link.GetAttribute("href"))
            .Where(link => UcampusFields(link, "/user/student/homework/index") is { } fields && !fields.ContainsKey("type")).Distinct().ToArray();
        if (entries is not [var entry]) return new("unrecognized_homework_entry");
        var fields = UcampusFields(entry, "/user/student/homework/index")!;
        if (fields.Count != 5 || fields.GetValueOrDefault("courseId") != course.ApiCourseId || fields.GetValueOrDefault("classId") != context.ClassId
            || fields.GetValueOrDefault("eccId") != context.EccId || fields.GetValueOrDefault("school_id") != context.SchoolHint
            || !UcampusNumber(fields.GetValueOrDefault("activationStatus"))) return new("course_binding_mismatch");
        var exams = document.QuerySelectorAll("a[href]").Select(link => link.GetAttribute("href"))
            .Where(link => UcampusFields(link, "/user/student/homework/index") is { } fields && fields.GetValueOrDefault("type") == "2").Distinct().ToArray();
        var examAddress = "";
        if (exams is [var exam])
        {
            var examFields = UcampusFields(exam, "/user/student/homework/index")!;
            if (examFields.Count != 6 || examFields.GetValueOrDefault("courseId") != course.ApiCourseId || examFields.GetValueOrDefault("classId") != context.ClassId
                || examFields.GetValueOrDefault("eccId") != context.EccId || examFields.GetValueOrDefault("school_id") != context.SchoolHint
                || !UcampusNumber(examFields.GetValueOrDefault("activationStatus"))) return new("course_binding_mismatch");
            examAddress = new Uri(UcampusOrigin, exam!).AbsoluteUri;
        }
        return new("ok", course with { Ucampus = context with { ContentId = content!, Version = UcampusVariable(document, "version") ?? "",
            HomeworkAddress = new Uri(UcampusOrigin, entry!).AbsoluteUri, ExamAddress = examAddress } });
    }
    public static UcampusCourseResult UcampusHomeworkEntry(string html, MetadataCourse course)
        => UcampusListEntry(html, course, false);
    public static UcampusCourseResult UcampusExamEntry(string html, MetadataCourse course)
        => UcampusListEntry(html, course, true);
    private static UcampusCourseResult UcampusListEntry(string html, MetadataCourse course, bool exam)
    {
        if (!ValidUcampusCourse(course) || UcampusFields(exam ? course.Ucampus!.ExamAddress : course.Ucampus!.HomeworkAddress, "/user/student/homework/index") is null) return new("unrecognized_homework_entry");
        var document = UcampusHtml(html); if (document is null) return new("unrecognized_homework_entry");
        if (UcampusLoginHtml(document)) return new("login_required");
        var routes = document.Scripts.SelectMany(script => Regex.Matches(script.TextContent,
            @"\$\.getJSON\(\s*['""](?<route>/user/student/homework/list\?[^'""\r\n]*)['""]", RegexOptions.CultureInvariant, ScriptTimeout)
            .Select(match => match.Groups["route"].Value)).ToArray();
        var fields = routes.Select(route => UcampusFields(route, "/user/student/homework/list"))
            .Where(value => value is not null && value.ContainsKey("newState")).ToArray();
        if (fields is not [var list] || list!.Count != (exam ? 7 : 5) || list.GetValueOrDefault("courseId") != course.ApiCourseId
            || !UcampusNumber(list.GetValueOrDefault("school_id")) || list.GetValueOrDefault("length") != "10"
            || list.GetValueOrDefault("type") != (exam ? "2" : "1") || list.GetValueOrDefault("newState") != ""
            || exam && (list.GetValueOrDefault("classId") != course.Ucampus!.ClassId || list.GetValueOrDefault("eccId") != course.Ucampus.EccId)) return new("unrecognized_homework_contract");
        return new("ok", course with { Ucampus = exam ? course.Ucampus! with { ExamListSchoolId = list["school_id"] } : course.Ucampus! with { ListSchoolId = list["school_id"] } });
    }
    private static int? UcampusSignedNumber(System.Text.Json.JsonElement row, string field) => int.TryParse(Text(row, field), NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture, out var number) ? number : null;
    private static DateTimeOffset? UcampusTime(System.Text.Json.JsonElement row, string field)
    {
        var text = Text(row, field);
        if (!DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local) || local.Year is < 2000 or > 2100) return null;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUniversalTime();
    }
    public static MetadataTaskBatch UcampusHomework(string json, string account, MetadataCourse course, int requestedPage)
        => UcampusTasks(json, account, course, requestedPage, "homework");
    public static MetadataTaskBatch UcampusExam(string json, string account, MetadataCourse course, int requestedPage)
        => UcampusTasks(json, account, course, requestedPage, "exam");
    private static MetadataTaskBatch UcampusTasks(string json, string account, MetadataCourse course, int requestedPage, string kind)
    {
        var exam = kind == "exam";
        var outcome = UcampusEnvelope("ucampus-" + kind, json);
        if (outcome != "ok") return new(outcome, [], 0, 0, []);
        if (!MetadataTaskNavigation.IsId(account) || !ValidUcampusCourse(course) || !UcampusNumber(exam ? course.Ucampus!.ExamListSchoolId : course.Ucampus!.ListSchoolId)) return new("unrecognized_course", [], 0, 0, []);
        using var document = Document(json); var rs = Property(document!.RootElement, "rs"); var list = Property(rs, "list");
        var total = Number(rs, "totalRow"); var pages = Number(rs, "totalPage"); var page = Number(rs, "pageNumber");
        if (list.ValueKind != System.Text.Json.JsonValueKind.Array || total is null or < 0 or > 100000 || pages is null || page != requestedPage
            || Number(rs, "pageSize") != UcampusPageSize || pages != (total + UcampusPageSize - 1) / UcampusPageSize
            || total > 0 && requestedPage > pages || total == 0 && requestedPage != 1
            || list.GetArrayLength() != Math.Min(UcampusPageSize, Math.Max(0, total.Value - (requestedPage - 1) * UcampusPageSize)))
            return new("unrecognized_tasks", [], 0, 0, []);
        var tasks = new Dictionary<string, PlatformCollectedTask>(); var conflicts = new HashSet<string>(); var invalid = 0; var duplicates = 0;
        var issues = new HashSet<string>();
        foreach (var row in list.EnumerateArray())
        {
            var id = Text(row, "homework_id"); var classId = Text(row, "class_id"); var student = Text(row, "stu_id");
            var title = Text(row, "title").Trim(); var type = Number(row, "type");
            if (!UcampusNumber(id) || !UcampusNumber(classId) || !UcampusNumber(student) || !ValidText(title, 300) || (exam ? type != 2 : type is not (1 or 3))) { invalid++; continue; }
            if (classId != course.Ucampus!.ClassId) { invalid++; issues.Add("class_binding_mismatch"); continue; }
            if (Text(row, "courseId") is { Length: > 0 } rowCourse && rowCourse != course.ApiCourseId) { invalid++; continue; }
            var stateValue = UcampusSignedNumber(row, "state"); var display = Number(row, "newState");
            var peer = Number(row, "peer_evaluation_flag") == 1; var peerEnabled = Number(row, "peer_evaluation_enable") == 1;
            var showEvaluation = Number(row, "show_evaluation_time") == 1;
            TaskState? state = stateValue switch { -1 => TaskState.Returned, 0 => TaskState.Pending, 2 when !peer => TaskState.Completed, _ => null };
            var label = display switch { 2 => "已结束", 3 => "进行中", 4 => "未提交", _ => "状态待核对" };
            if (stateValue == -1) label += " · 重做"; if (peer) label += " · 互评";
            label += $"（state={stateValue?.ToString(CultureInfo.InvariantCulture) ?? "未提供"}; newState={display?.ToString(CultureInfo.InvariantCulture) ?? "未提供"}）";
            var begin = UcampusTime(row, "start_time");
            var deadline = showEvaluation ? UcampusTime(row, "peer_evaluation_start_time") : peer && peerEnabled ? null : UcampusTime(row, "end_time");
            if (begin > deadline) { begin = deadline = null; issues.Add("invalid_time_order"); }
            if (deadline is null) issues.Add("deadline_unverified"); if (state is null) issues.Add("state_unverified");
            var link = (exam || type == 1) && !peer && stateValue is -1 or 0 or 2
                ? MetadataTaskNavigation.Ucampus(course, account, id, student, stateValue.Value == 2, kind) : null;
            if (link is null) issues.Add("entry_unavailable");
            var explanation = stateValue == -1 ? "官网标为重做，需重新处理。" : stateValue == 0 ? "官网提供" + (exam ? "测试" : "作业") + "入口，尚未完成。"
                : state == TaskState.Completed ? "官网返回成绩和" + (exam ? "测试" : "作业") + "详情状态。" : "官网状态尚不能确认整项任务完成，请在官网核对。";
            if (peer && peerEnabled) explanation += showEvaluation ? "截止取官网标注的提交截止及互评开始时间。" : "官网 end_time 为互评截止时间，提交截止尚未确认。";
            if (peer || type == 3) explanation += "互评或小组入口仍需在官网处理。";
            var task = new PlatformCollectedTask("ucampus", account, course.Id, "legacy", MetadataTaskNavigation.UcampusTaskId(id, classId, student), kind,
                title, course.Name, state, label, state is null ? "unknown" : "explicit", explanation, begin, deadline,
                "start_time=" + TimeEvidence(row, "start_time") + "; end_time=" + TimeEvidence(row, "end_time") + "; peer_evaluation_start_time="
                + TimeEvidence(row, "peer_evaluation_start_time") + $"; show_evaluation_time={(showEvaluation ? 1 : 0)}", link) { MetadataVersion = 2 };
            if (conflicts.Contains(task.RemoteId)) { invalid++; continue; }
            if (tasks.TryGetValue(task.RemoteId, out var previous))
            { duplicates++; if (previous != task) { tasks.Remove(task.RemoteId); conflicts.Add(task.RemoteId); invalid++; issues.Add("conflicting_task"); } }
            else tasks.Add(task.RemoteId, task);
        }
        return new(invalid > 0 || issues.Count > 0 ? "partial_tasks" : "ok", tasks.Values.ToArray(), invalid, duplicates, issues.ToArray())
            { TotalRows = total.Value, TotalPages = pages.Value, Rows = list.GetArrayLength(), ListComplete = total == 0 || requestedPage == pages };
    }
}
