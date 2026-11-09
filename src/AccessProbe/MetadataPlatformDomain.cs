using System.Text.Json.Serialization;

namespace AccessProbe;

public sealed record MetadataPlatform(string Id, string Name, string CodePrefix, string Host, string HomePath, string CoverageText)
{
    public static readonly MetadataPlatform Ucampus = new("ucampus", "U 校园", "UC", "u.unipus.cn", "/index.html/",
        "采集旧版 u.unipus.cn 已选班课的作业、测试，以及普通课件每单元的截止时间与必学进度。必学项逐项完成后移出待办；未完成时按最早有效截止时间排列。普通个人作业、测试点击时核对官方签名入口；单元入口核对课件版本与目录后打开。特殊课件、小组、互评和未确认的学习活动明确标为待核对。");
    public static readonly MetadataPlatform Fif = new("fif", "Fi 口语", "FIF", "www.fifedu.com", "/iplat/html/home/home.html",
        "FiF 暂不适配；已有手动任务保留，可继续手动记录。");
    public static readonly MetadataPlatform Zhihuishu = new("zhihuishu", "智慧树", "ZHS", "onlineweb.zhihuishu.com", "/onlinestuh5",
        "先列出翻转课、共享课、智慧课程及 AI 智课／智能体课，保存课程选择后采集。新版智慧课程读取未完成和已完成作业、考试，并核对官网任务总数；入口打开该课程的官方任务列表，按任务名核对。资源学习、智能体任务、旧版智慧课程及 AI 智课的任务暂未覆盖，会明确提示。共享课覆盖已确认的作业、考试及机房补考；线下安排缺少标识时不导入。互评、小组及未知状态标为待核对；首页提醒消失不代表完成。");
    public static readonly MetadataPlatform Pta = new("pta", "PTA", "PTA", "pintia.cn", "/course-class",
        "先列出教学班级及教学题目集关联的课程，保存课程选择后采集题目集。保留开放时间、个人答题截止时间和官方答卷状态；仅明确结束答题的答卷标为已完成，分数或题目集已关闭不代表完成。一直可用的题目集显示无截止时间；互评、特殊考试及未知状态需在官网核对。公共练习库、PAT 题库和逐题作答暂未覆盖。");
    public static readonly IReadOnlyList<MetadataPlatform> All = [Ucampus, Fif, Zhihuishu, Pta];
    public static readonly IReadOnlyList<MetadataPlatform> Enabled = [Ucampus, Zhihuishu, Pta];
    public bool AdaptationEnabled => Id != "fif";
    public static MetadataPlatform Get(string id) => All.FirstOrDefault(platform => platform.Id == id)
        ?? throw new TaskValidationException("SYNC003", "该平台未接入此同步方式。");
    public Uri Home => new("https://" + Host + HomePath);
    public Uri Login => Id switch
    {
        "ucampus" => new("https://u.unipus.cn/user/comm/login?school_id="),
        "fif" => new("https://www.fifedu.com/iplat/fifLogin/index.html?v=5.4.4"),
        "pta" => new("https://pintia.cn/auth/login?redirect=" + Uri.EscapeDataString(Home.AbsoluteUri)),
        _ => new("https://login.zhihuishu.com/?origin=zhs&service=" + Uri.EscapeDataString(Home.AbsoluteUri))
    };
    public bool IsCookieDomain(string host) => Id switch
    {
        "ucampus" => host is "unipus.cn" or "sso.unipus.cn" or "u.unipus.cn" or "ucontent.unipus.cn",
        "fif" => host is "fifedu.com" or "www.fifedu.com" or "cycore.fifedu.com" or "oral.fifedu.com",
        "zhihuishu" => host == "zhihuishu.com" || host.EndsWith(".zhihuishu.com", StringComparison.Ordinal),
        "pta" => host is "pintia.cn" or "passport.pintia.cn",
        _ => false
    };
    public bool IsLoginNavigation(string address) => address == "about:blank" || TaskRules.IsSafeLink(address)
        && Uri.TryCreate(address, UriKind.Absolute, out var uri) && (IsCookieDomain(uri.IdnHost)
            || Id == "ucampus" && uri.IdnHost is "open.weixin.qq.com" or "captcha.unipus.cn"
            || Id == "zhihuishu" && (uri.IdnHost is "open.weixin.qq.com" or "open.weixin.qq.com.cn" || IsZhihuishuSsoNavigation(uri)));
    public bool IsLoginFrameNavigation(string address)
    {
        if (IsLoginNavigation(address)) return true;
        if (Id != "pta" || !TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Port != 443 || uri.Fragment.Length != 0) return false;
        // TCaptcha.js and its frame loader use these verifier documents inside the official login page.
        return uri.IdnHost is "turing.captcha.qcloud.com" or "ca.turing.captcha.qcloud.com"
            && uri.AbsolutePath is "/template/placeholder_v2.html" or "/template/new_slide_placeholder.html"
                or "/template/drag_ele.html" or "/template/drag_ele_global.html";
    }
    private bool IsZhihuishuSsoNavigation(Uri uri)
    {
        // The current official login wraps its return address in a Polymas SSO service.
        // Allow those navigation hops without treating Polymas cookies as course credentials.
        if (uri.IdnHost != "cloudapi.polymas.com" || uri.Fragment.Length != 0
            || uri.AbsolutePath is not ("/oauth/login/ssoLogin" or "/oauth/login/checkLogin")) return false;
        var fields = RainAddressPolicy.Query(uri);
        var returnField = uri.AbsolutePath == "/oauth/login/ssoLogin" ? "redirect" : "url";
        return fields is not null && fields.TryGetValue(returnField, out var target) && TaskRules.IsSafeLink(target)
            && Uri.TryCreate(target, UriKind.Absolute, out var destination) && IsCookieDomain(destination.IdnHost);
    }
    public bool IsMetadataDocument(string address) => TaskRules.IsSafeLink(address)
        && Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.IdnHost == Host && (Id == "ucampus" ? uri.AbsolutePath == "/user/student/index"
            : uri.AbsolutePath == HomePath) || Id == "zhihuishu" && uri.IdnHost == "onlinestuh5.zhihuishu.com" && uri.AbsolutePath == "/");
    // Routing, page-return and load-balancer cookies can change without a new login.
    // Keep unknown cookies conservative so an unrecognized authentication cookie cannot mix accounts.
    public bool IsAccountCookie(MetadataSessionCookie cookie) => IsLegacyAccountCookie(cookie)
        && (Id != "zhihuishu" || cookie.Name != "acw_tc")
        && (Id != "ucampus" || cookie.Name is not ("hostUrl" or "stu_return_from_content" or "SSOExpireTime" or "service"));
    public bool IsLegacyAccountCookie(MetadataSessionCookie cookie) => (Id != "pta" || cookie.Name != "JSESSIONID")
        && !cookie.Name.StartsWith("Hm_lpvt_", StringComparison.Ordinal)
        && !cookie.Name.StartsWith("Hm_lvt_", StringComparison.Ordinal)
        && !cookie.Name.StartsWith("_ga_", StringComparison.Ordinal)
        && cookie.Name is not ("HMACCOUNT" or "HMACCOUNT_BFESS" or "sensorsdata2015jssdkcross" or "_ga" or "_gid" or "_gat" or "_bl_uid" or "__snaker__id" or "errXHRLog");
    public TaskSource Source => Id switch { "ucampus" => TaskSource.Ucampus, "fif" => TaskSource.Fif, "pta" => TaskSource.Pta, _ => TaskSource.Zhihuishu };
}

public sealed record MetadataSyncSettings
{
    public bool Connected { get; init; }
    public bool AutomaticSyncEnabled { get; init; } = true;
    public DateTimeOffset? LastSyncAtUtc { get; init; }
    public string LastSyncOutcome { get; init; } = "not_started";
    public MetadataSyncSummary? LastSummary { get; init; }
}
public sealed record MetadataSyncProblem(string Code, string Course, string Message);
public sealed record MetadataSyncSummary(string PlatformId, int Courses, int CoursesRead, int SelectedCourses,
    int Tasks, int Added, int Updated, bool LoginRequired, bool CourseListCovered, IReadOnlyList<MetadataSyncProblem> Problems)
{
    public string CoverageScope { get; init; } = "unverified";
    public int TasksWithDeadline { get; init; }
    public int TasksWithLink { get; init; }
    public int StatusNeedsReview { get; init; }
    public int UnsupportedRows { get; init; }
    public int DuplicateRows { get; init; }
    public int Requests { get; init; }
    public int CatalogPages { get; init; }
    public int ApplicationsDiscovered { get; init; }
    public int SharedCourseReminders { get; init; }
    public bool ReminderListCovered { get; init; }
    public int FlipCourses { get; init; }
    public int SharedCourses { get; init; }
    public int SmartCourses { get; init; }
    public int AgentCourses { get; init; }
    public int SmartOtherTasks { get; init; }
    public bool AccountVerified { get; init; }
    public bool FullTaskListsCovered { get; init; }
    public int RoomExamTasks { get; init; }
    public int OfflineExamTasks { get; init; }
    public int MakeupExamTasks { get; init; }
    public int UnidentifiedExamRows { get; init; }
    public int? TaskListCoursesRead { get; init; }
    public int TeachingDirectoryFallbackCourses { get; init; }
    public int LearningUnits { get; init; }
    public int LearningUnitsWithDeadline { get; init; }
    public int RequiredLearningUnitsIncomplete { get; init; }
    public bool LearningUnitListsCovered { get; init; }
}
public sealed record MetadataRequest(string Operation, int Page = 1, int Bucket = 0)
{
    [JsonIgnore] public MetadataCourse? Course { get; init; }
    [JsonIgnore] public string SchoolId { get; init; } = "";
    [JsonIgnore] public string ProblemSetId { get; init; } = "";
    [JsonIgnore] public string AccountId { get; init; } = "";
    [JsonIgnore] public string UnitId { get; init; } = "";
    public override string ToString() => nameof(MetadataRequest);
}
public sealed record MetadataResponse(string Outcome, int? StatusCode, [property: JsonIgnore] string Json = "")
{
    [JsonIgnore] public string? AccountId { get; init; }
    [JsonIgnore] public Uri? SessionRedirect { get; init; }
    public int AdditionalRequests { get; init; }
    public override string ToString() => nameof(MetadataResponse);
}
public interface IPlatformMetadataReader
{
    Task<MetadataResponse> ReadAsync(MetadataRequest request, CancellationToken cancellation);
}
public sealed record PlatformCollectedTask(string PlatformId, [property: JsonIgnore] string AccountId,
    [property: JsonIgnore] string CourseId, string CourseKind, [property: JsonIgnore] string RemoteId,
    string Kind, string Title, string CourseName, TaskState? State, string PlatformStatusText, string StateEvidence,
    string StatusExplanation, DateTimeOffset? BeginUtc, DateTimeOffset? DeadlineUtc, string TimeText,
    [property: JsonIgnore] string? Link)
{
    [JsonIgnore] public int MetadataVersion { get; init; } = 1;
    public string ExamMode { get; init; } = "";
    public bool NoDeadline { get; init; }
    public decimal? ProgressPercent { get; init; }
    public string LearningEvidence { get; init; } = "";
    public override string ToString() => nameof(PlatformCollectedTask);
}
public sealed record MetadataCourse([property: JsonIgnore] string Id, string Name, string Kind, string Detail)
{
    [JsonIgnore] public string ApiCourseId { get; init; } = Id;
    [JsonIgnore] public string RecruitId { get; init; } = "";
    [JsonIgnore] public UcampusCourseContext? Ucampus { get; init; }
    [JsonIgnore] public ZhihuishuSmartCourseContext? Smart { get; init; }
    public override string ToString() => nameof(MetadataCourse);
}

public sealed record ZhihuishuSmartCourseContext(string ClassId, string EntryFamily)
{
    public bool SupportsTaskLists => EntryFamily == "pro";
    public override string ToString() => nameof(ZhihuishuSmartCourseContext);
}

public sealed record ZhihuishuSmartTaskType(int Type, int Count, int Unfinished);
public sealed record ZhihuishuSmartTaskTypes(string Outcome, IReadOnlyList<ZhihuishuSmartTaskType> Types);

public sealed record UcampusCourseContext(string ClassId, string EccId, string SchoolHint, string CourseType)
{
    public string ContentId { get; init; } = "";
    public string Version { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string HomeworkAddress { get; init; } = "";
    public string ListSchoolId { get; init; } = "";
    public string ExamAddress { get; init; } = "";
    public string ExamListSchoolId { get; init; } = "";
    public override string ToString() => nameof(UcampusCourseContext);
}
public sealed record UcampusCourseResult(string Outcome, [property: JsonIgnore] MetadataCourse? Course = null);
public sealed record UcampusUnitLeaf(string Id, string Route, string Micro, string TabType);
public sealed record UcampusLearningWindow(bool Valid, DateTimeOffset? BeginUtc, DateTimeOffset? DeadlineUtc, string Evidence);
public sealed record UcampusUnitDefinition(string Id, string Name, IReadOnlyList<UcampusUnitLeaf> Leafs,
    IReadOnlyList<string> Micros, UcampusLearningWindow Window, string StateEvidence)
{
    public override string ToString() => nameof(UcampusUnitDefinition);
}
public sealed record UcampusUnitCatalog(string Outcome, string PublishVersion, IReadOnlyList<UcampusUnitDefinition> Units);

public sealed record MetadataCatalog(string Outcome, IReadOnlyList<MetadataCourse> Courses, int Page, int TotalPages,
    int Rows, int UnsupportedRows, int DuplicateRows)
{
    public int? TotalRows { get; init; }
}
public sealed record MetadataTaskBatch(string Outcome, IReadOnlyList<PlatformCollectedTask> Tasks, int UnsupportedRows,
    int DuplicateRows, IReadOnlyList<string> Issues)
{
    public int TotalRows { get; init; }
    public int TotalPages { get; init; }
    public int Rows { get; init; }
    public bool ListComplete { get; init; }
    public int UnidentifiedExamRows { get; init; }
}
public sealed record MetadataDiscovery(string Outcome, int Applications);
