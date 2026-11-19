namespace AccessProbe;

// Only verified results, counts and diagnostic categories belong in a connection report.
// A QR image, browser navigation or cookie's presence does not prove that login succeeded.
public sealed record ConnectionReport(string PlatformId)
{
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool SessionAvailable { get; init; } = true;
    public bool LoginRequired { get; init; }
    public bool AccountVerified { get; init; }
    public bool SessionSaved { get; init; } = true;
    public bool CatalogCovered { get; init; }
    public int Courses { get; init; }
    public int SelectedCourses { get; init; }
    public int Tasks { get; init; }
    public bool CatalogOnly { get; init; }
    public bool SelectionConfirmed { get; init; }
    public bool TaskListsCovered { get; init; }
    public bool SupportedTaskListsCovered { get; init; }
    public int StatusNeedsReview { get; init; }
    public int SmartOtherTasks { get; init; }
    public IReadOnlyList<string> ProblemCodes { get; init; } = [];
    public IReadOnlyList<string> ProblemOutcomes { get; init; } = [];
}

public sealed record ConnectionFeedback(string Title, string Steps, string Explanation, string NextStep,
    bool Warning = false, bool CanChooseCourses = false)
{
    public string Text => Title + "\n" + Explanation + "\n" + NextStep;
    public const string CheckLabel = "我已扫码，检查接入";
    public const string ChooseLabel = "选择课程并同步";

    public static ConnectionFeedback Waiting() => new("等待官网确认登录",
        "① 官网登录：待确认    ② 课程列表：待读取    ③ 任务：待选课",
        "扫码后还需要在官方 App 中确认授权，并等待应用内官网返回。是否接入，以本应用的官网验证结果为准。",
        "确认后点击“" + CheckLabel + "”；程序也会自动检查。短信或账号登录完成后同样可以检查。");

    public static ConnectionFeedback Checking(bool accountVerified = false) => new(
        accountVerified ? "登录已确认，正在读取课程" : "正在确认接入，请稍候…",
        accountVerified ? "① 官网登录：已确认    ② 课程列表：读取中    ③ 任务：待读取"
            : "① 官网登录：检查中    ② 课程列表：待读取    ③ 任务：待选课",
        "正在使用本应用中的当前登录，只读检查官网账号和课程列表。",
        "请保持此页面；检查结束会显示结果和下一步操作。");

    public static ConnectionFeedback PageFailed(string outcome) => new("官网页面加载未完成",
        "① 官网登录：本次尚未确认    ② 课程列表：待读取    ③ 任务：待读取",
        Reason([outcome]) + "页面加载失败无法证明扫码失败或登录已过期。",
        outcome == "environment_failed" ? "请重新打开连接页；仍失败时检查系统网页组件和应用数据目录是否可用。"
            : "重新打开官网登录页面；页面恢复后再检查接入。", true);

    public static ConnectionFeedback Interrupted(bool cancelled, bool accountVerified = false) => new(
        cancelled ? "本次接入检查已停止" : "本次接入检查未完成",
        "① 官网登录：" + (accountVerified ? "先前已确认，本次未完成" : "本次尚未确认") + "    ② 课程列表：未完整读取    ③ 任务：待读取",
        cancelled ? "页面更新或本次操作停止了检查，已有任务和课程选择保留。"
            : accountVerified ? "先前的官网账号检查已通过，本次重新检查未完成；已有登录和课程选择保留。"
                : "本次未能取得完整的官网验证结果，尚不能确认接入状态。",
        "等待当前官网页面加载完成后，点击“" + CheckLabel + "”重试。", true);

    public static bool HasOnlyUncoveredZhihuishuOtherTasks(ConnectionReport report) => report.PlatformId == "zhihuishu"
        && report.SupportedTaskListsCovered && !report.TaskListsCovered && report.SmartOtherTasks > 0
        && report.ProblemCodes.Count > 0 && report.ProblemCodes.All(code => code == "ZHS006")
        && report.ProblemOutcomes.Count > 0 && report.ProblemOutcomes.All(outcome => outcome == "smart_other_tasks_unverified");

    public static ConnectionFeedback From(ConnectionReport report)
    {
        var verified = report.AccountVerified && !report.LoginRequired;
        var reason = Reason(report.ProblemOutcomes);
        var ptaAnswersNeedReview = report.PlatformId == "pta" && report.SupportedTaskListsCovered
            && !report.TaskListsCovered && report.StatusNeedsReview > 0 && report.ProblemCodes.Count > 0
            && report.ProblemCodes.All(code => code == "PTA003");
        var smartCoverageNote = HasOnlyUncoveredZhihuishuOtherTasks(report);
        var taskProgress = report.CatalogOnly ? report.SelectionConfirmed ? "待读取" : "待选课"
            : smartCoverageNote ? "支持范围已同步 " + report.Tasks + " 条"
            : ptaAnswersNeedReview ? "题目集已读取，答卷待核对"
            : report.TaskListsCovered && !report.LoginRequired ? "已同步 " + report.Tasks + " 条"
            : "已读取 " + report.Tasks + " 条，覆盖未完整确认";
        var steps = "① 官网登录：" + (verified ? "已确认" : "待确认")
            + "    ② 课程列表：" + (report.CatalogCovered ? report.Courses + " 门" : report.Courses > 0 ? "部分 " + report.Courses + " 门" : "未完整读取")
            + "\n③ 任务：" + taskProgress;
        var choose = verified && report.SessionSaved && report.Courses > 0 && report.PlatformId != "uai";
        ConnectionFeedback Make(string title, string explanation, string next, bool warning = false)
            => new(title, steps, explanation, next, warning, choose);
        if (!report.SessionAvailable)
            return Make("尚未接入", "本应用还没有可供验证的官网登录。", "点击“连接平台”，在应用内官网登录后检查接入。", true);
        if (report.LoginRequired)
            return Make("尚未接入：官网仍要求登录",
                "官网尚未认可本应用的当前登录。仅凭这个结果，无法判断是授权尚未返回还是会话已失效。",
                "已扫码时，请核对官方 App 中的授权确认，等待应用内官网返回后再检查；仍失败可点击“刷新登录页”重新加载官网。", true);
        if (!verified)
            return Make("接入尚未确认", reason.Length > 0 ? reason : "本次官网返回未能确认当前账号，尚不能判断扫码是否成功。",
                Retry(report.ProblemOutcomes, false), true);
        if (!report.SessionSaved)
            return Make("登录已确认，保存未完成", "官网已确认当前登录，但本应用未能保存登录状态，重启后可能需要重新登录。",
                "请检查本应用的数据目录权限或设备存储空间，然后重试检查接入。", true);
        if (report.PlatformId == "uai")
            return Make("官网账号已确认", "U 校园 AI 版当前支持官网身份与概要预览，自动课程和任务同步尚未接入。",
                "可以返回平台页查看概要，或手动新增任务。");
        if (!report.CatalogCovered || report.CatalogOnly && report.ProblemCodes.Count > 0)
            return Make("登录已确认，课程读取未完成",
                (reason.Length > 0 ? reason : "课程列表或分页尚未完整确认。")
                    + (report.Courses > 0 ? "已读取 " + report.Courses + " 门课程，当前列表可能不完整。" : "这次不能视为没有课程。"),
                Retry(report.ProblemOutcomes, true), true);
        if (report.Courses == 0)
            return Make("已接入，官网当前没有课程", "官网已确认登录，并完整返回空课程列表；此时首页没有该平台任务是正常的。",
                "若官网实际有课，请核对官网学生课程页和所在站点后刷新课程。");
        if (!report.SelectionConfirmed || report.SelectedCourses == 0)
            return Make(report.SelectionConfirmed ? "已接入，尚未选择同步课程" : "已接入，待选择课程",
                "已读取 " + report.Courses + " 门课程。选择并保存课程后，任务才会进入首页。",
                "点击“" + ChooseLabel + "”，勾选需要记录的课程并保存。");
        if (report.CatalogOnly)
            return Make("已接入，课程已就绪", "已读取 " + report.Courses + " 门课程，当前选择 " + report.SelectedCourses + " 门；本次只检查登录与课程。",
                "点击“" + ChooseLabel + "”继续同步，随后可在首页查看任务。");
        if (ptaAnswersNeedReview)
            return Make("已接入，题目集已读取，答卷待核对",
                $"已将 {report.Tasks} 条 PTA 题目集同步到首页；{report.StatusNeedsReview} 条答卷状态或个人截止时间未获官网明确确认。登录和题目集读取正常；需要检查答卷状态时，使用“重试同步”。已有任务与人工状态保留。",
                "需要复查答卷状态或个人截止时间时，请使用“重试同步”；已同步的题目集可在首页查看。");
        if (smartCoverageNote)
        {
            var explanation = $"已读取 {report.Tasks} 条支持范围内的作业、考试并保存到首页。";
            if (report.SmartOtherTasks > 0)
                explanation += $"智慧课程另有 {report.SmartOtherTasks} 条其他学习任务暂未覆盖。";
            var supplementalIssues = report.ProblemCodes.Count(code => code != "ZHS006");
            if (supplementalIssues > 0)
                explanation += $"另有 {supplementalIssues} 类补充信息待核对，详情见同步详情；不影响已读取的任务列表。";
            return Make("已接入，支持范围任务已同步", explanation,
                "可返回首页查看已读取任务；未覆盖内容和补充信息可在智慧树官网及同步详情中核对。");
        }
        if (!report.TaskListsCovered && !ptaAnswersNeedReview)
            return Make("已接入，任务同步未完成", (reason.Length > 0 ? reason : "所选课程的任务范围尚未完整读取。")
                + "本次已读取 " + report.Tasks + " 条任务，已有记录保留。", "稍后重试“同步已选课程”；可在官网核对未完成的任务范围。", true);
        return Make("已接入，任务已同步", report.Tasks == 0
                ? "本轮已完整读取所选课程支持的任务范围，官网没有返回任务。"
                : "已读取 " + report.SelectedCourses + " 门所选课程，共 " + report.Tasks + " 条任务，已汇入首页。",
            "返回首页查看任务；答题和提交仍在官网完成。");
    }

    public static ConnectionFeedback ForPlatformManagement(ConnectionReport report, string refreshLabel = "刷新课程列表")
    {
        var feedback = From(report);
        string Refresh(string prefix) => prefix + "点击“" + refreshLabel + "”重新检查。";
        var next = !report.SessionAvailable || report.LoginRequired
            ? Refresh("先打开本平台的官网登录页完成登录和授权确认，返回平台管理后")
            : !report.AccountVerified ? Refresh("按上述原因处理后，")
            : !report.SessionSaved ? feedback.NextStep
            : report.PlatformId == "uai" ? "可以查看本页概要，或手动新增任务。"
            : !report.CatalogCovered || report.CatalogOnly && report.ProblemCodes.Count > 0
                ? Refresh("按上述原因处理后，") + "已有登录和课程选择保留。"
            : report.Courses == 0 ? Refresh("若官网实际有课，请核对官网学生课程页和所在站点后，")
            : !report.SelectionConfirmed || report.SelectedCourses == 0
                ? "在课程列表中勾选需要记录的课程，点击“保存选择并采集”，任务才会进入首页。"
            : report.CatalogOnly ? "点击“同步已选课程”，随后可在首页查看任务。"
            : report.PlatformId == "pta" && report.SupportedTaskListsCovered && !report.TaskListsCovered
                ? "题目集已读取；可在任务列表查看内容，需要刷新个人答卷状态时点击“重试同步”。"
            : report.PlatformId == "zhihuishu" && HasOnlyUncoveredZhihuishuOtherTasks(report)
                ? "支持的作业和考试已同步；未覆盖的学习资源或其他任务请在官网查看。"
            : feedback.NextStep;
        return feedback with { NextStep = next };
    }

    public static string Reason(IEnumerable<string> outcomes)
    {
        var values = outcomes.ToHashSet(StringComparer.Ordinal);
        if (values.Contains("certificate_failed")) return "官网证书验证失败，请检查设备时间和网络。";
        if (values.Contains("environment_failed")) return "应用的官网浏览组件未能准备就绪。";
        if (values.Overlaps(["rate_limited", "request_limit"])) return "官网限制了请求频率或本轮已达到读取上限，请稍后重试。";
        if (values.Contains("timeout")) return "官网响应超时，本次读取未完成。";
        if (values.Overlaps(["network_error", "network_failed"])) return "连接官网失败，请检查网络后重试。";
        if (values.Overlaps(["http_error", "http_failed", "temporarily_unavailable"])) return "官网服务暂时未能完成请求，请稍后重试。";
        if (values.Overlaps(["forbidden", "course_access_denied"])) return "官网拒绝了此项读取，请在官网核对课程访问权限。";
        if (values.Overlaps(["verification_required", "captcha_required"])) return "官网要求完成额外验证，请在应用内官网处理验证码或安全确认。";
        if (values.Overlaps(["unsupported_redirect", "blocked_navigation"])) return "官网登录返回的跳转尚未完成或未适配。";
        if (values.Overlaps(["csrf_rejected", "csrf_missing", "course_credentials_unavailable", "session_unavailable"])) return "官网当前页面的登录授权尚未就绪，请返回学生课程主页后重试。";
        if (values.Overlaps(["course_session_expired", "session_check_unavailable"])) return "账号验证与课程服务的确认结果不同，课程服务尚未恢复。";
        if (values.Overlaps(["script_failed", "style_failed", "blank_page", "renderer_failed"])) return "官方登录页面未能正常显示，请重新打开官网；持续空白时请更新系统网页组件。";
        return values.Count > 0 ? "官网返回的结构、分页或部分字段尚未识别，当前读取范围未完整确认。" : "";
    }

    private static string Retry(IReadOnlyList<string> outcomes, bool verified)
    {
        if (outcomes.Any(value => value is "rate_limited" or "request_limit" or "temporarily_unavailable"))
            return verified ? "请稍后重试读取课程，已有登录和课程选择保留。" : "请稍后点击“" + CheckLabel + "”，核对接入结果。";
        if (outcomes.Any(value => value is "verification_required" or "captcha_required" or "csrf_rejected" or "course_credentials_unavailable" or "unsupported_redirect"))
            return "请在应用内官网完成额外确认并返回学生课程主页，然后重试检查接入。";
        return verified ? "重试读取课程；已有登录和课程选择保留。" : "检查网络或等待官网加载完成后，点击“" + CheckLabel + "”重试。";
    }
}

public static class ConnectionReports
{
    public static ConnectionReport From(ChaoxingSyncSummary summary, CourseRecordingSettings recording, bool catalogOnly, bool saved = true)
        => Create("chaoxing", summary.LoginRequired, summary.AccountVerified, summary.CourseListCovered, summary.Courses,
            summary.SelectedCourses ?? summary.Courses, summary.Tasks, catalogOnly, recording, saved,
            summary.Problems.Count == 0 && summary.DetailsDeferred == 0 && summary.CoursesRead == (summary.SelectedCourses ?? summary.Courses),
            summary.Problems.Select(problem => problem.Code), summary.Problems.Select(problem => problem.Outcome));

    public static ConnectionReport From(RainSyncSummary summary, CourseRecordingSettings recording, bool catalogOnly, bool saved = true)
        => Create(summary.PlatformId, summary.LoginRequired, summary.AccountVerified, summary.CourseListCovered, summary.Courses,
            summary.SelectedCourses ?? summary.Courses, summary.Tasks, catalogOnly, recording, saved,
            summary.Problems.Count == 0 && summary.CoursesRead == (summary.SelectedCourses ?? summary.Courses),
            summary.Problems.Select(problem => problem.Code), summary.Problems.Select(problem => problem.Outcome));

    public static ConnectionReport From(MoocSyncSummary summary, CourseRecordingSettings recording, bool catalogOnly, bool saved = true)
        => Create("mooc", summary.LoginRequired, summary.AccountVerified, summary.CourseListCovered, summary.Courses,
            summary.SelectedCourses ?? summary.Courses, summary.Tasks, catalogOnly, recording, saved,
            summary.Problems.Count == 0 && summary.CoursesRead == (summary.SelectedCourses ?? summary.Courses),
            summary.Problems.Select(problem => problem.Code), summary.Problems.Select(problem => problem.Outcome));

    public static ConnectionReport From(XuetangSyncSummary summary, CourseRecordingSettings recording, bool catalogOnly, bool saved = true)
        => Create("xuetang", summary.LoginRequired, summary.AccountVerified, summary.CourseListCovered, summary.Courses,
            summary.SelectedCourses, summary.Tasks, catalogOnly, recording, saved,
            summary.Problems.Count == 0 && summary.CoursesRead == summary.SelectedCourses,
            summary.Problems.Select(problem => problem.Code), summary.Problems.Select(problem => problem.Outcome));

    public static ConnectionReport From(MetadataSyncSummary summary, CourseRecordingSettings recording, bool catalogOnly, bool saved = true)
        => Create(summary.PlatformId, summary.LoginRequired, summary.AccountVerified, summary.CourseListCovered, summary.Courses,
            summary.SelectedCourses, summary.Tasks, catalogOnly, recording, saved,
            summary.PlatformId == "pta"
                ? summary.Problems.Count == 0 && summary.FullTaskListsCovered && summary.StatusNeedsReview == 0
                : summary.Problems.Count == 0 && summary.FullTaskListsCovered && summary.CoursesRead == summary.SelectedCourses
                    && (summary.PlatformId != "ucampus" || summary.LearningUnitListsCovered),
            summary.PlatformId == "pta" ? summary.TaskListCoursesRead == summary.SelectedCourses
                : summary.FullTaskListsCovered && (summary.PlatformId != "ucampus" || summary.LearningUnitListsCovered),
            summary.StatusNeedsReview, summary.SmartOtherTasks,
            summary.Problems.Select(problem => problem.Code), summary.Problems.Select(problem => problem.Outcome));

    private static ConnectionReport Create(string platform, bool loginRequired, bool accountVerified, bool catalogCovered, int courses,
        int selected, int tasks, bool catalogOnly, CourseRecordingSettings recording, bool saved, bool tasksCovered,
        IEnumerable<string> codes, IEnumerable<string> outcomes) => Create(platform, loginRequired, accountVerified, catalogCovered,
            courses, selected, tasks, catalogOnly, recording, saved, tasksCovered, tasksCovered, 0, 0, codes, outcomes);

    private static ConnectionReport Create(string platform, bool loginRequired, bool accountVerified, bool catalogCovered, int courses,
        int selected, int tasks, bool catalogOnly, CourseRecordingSettings recording, bool saved, bool tasksCovered,
        bool supportedTaskListsCovered, int statusNeedsReview, int smartOtherTasks,
        IEnumerable<string> codes, IEnumerable<string> outcomes) => new(platform)
    {
        LoginRequired = loginRequired, AccountVerified = accountVerified, CatalogCovered = catalogCovered,
        Courses = courses, SelectedCourses = !recording.SelectionConfirmed ? 0
            : catalogOnly ? recording.Courses.Count(course => CourseRecordingRules.IsSelected(recording, course.Key)) : selected,
        Tasks = tasks, CatalogOnly = catalogOnly,
        SelectionConfirmed = recording.SelectionConfirmed, SessionSaved = saved,
        TaskListsCovered = !catalogOnly && !loginRequired && catalogCovered && tasksCovered,
        SupportedTaskListsCovered = !catalogOnly && !loginRequired && catalogCovered && supportedTaskListsCovered,
        StatusNeedsReview = Math.Clamp(statusNeedsReview, 0, 100000), SmartOtherTasks = Math.Clamp(smartOtherTasks, 0, 100000),
        ProblemCodes = codes.Distinct().ToArray(), ProblemOutcomes = outcomes.Where(value => value.Length > 0).Distinct().ToArray()
    };
}
