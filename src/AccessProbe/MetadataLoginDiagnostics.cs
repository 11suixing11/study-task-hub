using System.IO;
using System.Text.Json;

namespace AccessProbe;

public enum MetadataLoginStage { Preparation, CacheRefresh, MainNavigation, FrameNavigation, Popup, Resource, Certificate, Renderer, SessionCheck, SessionValidation }
public enum MetadataLoginResult { Started, Ready, Blocked, LoadingTimeout, NetworkFailed, Failed, ScriptUnavailable, LoginRequired, AccountUnknown, AccountMismatch, MetadataReady, PopupSuppressed, AccountReady, AwaitingLogin, SessionChanged }
public sealed record MetadataLoginEvent(DateTimeOffset AtUtc, MetadataLoginStage Stage, MetadataLoginResult Result, string Destination, int? Status)
{
    public ulong? NavigationId { get; init; }
}
public sealed record MetadataLoginReport(int Schema, string PlatformId, int Attempts, int BlockedNavigations, int FailedResources,
    int Cookies, bool AccountPresent, bool SessionPresent, bool StudentTokenPresent, IReadOnlyList<MetadataLoginEvent> Events)
{
    public MetadataLoginEvent? PageFailure { get; init; }
    public bool AccountConfirmed { get; init; }
    public IReadOnlyList<MetadataRequestDiagnostic> Requests { get; init; } = [];
    public string? SessionChangeReason { get; init; }
}
public sealed record MetadataRequestDiagnostic(DateTimeOffset AtUtc, string Operation, string Outcome, int? Status);

public sealed class MetadataLoginDiagnostics(MetadataPlatform platform, string path)
{
    private readonly object gate = new();
    private readonly List<MetadataLoginEvent> events = [];
    private readonly List<MetadataRequestDiagnostic> requests = [];
    private string? sessionChangeReason;
    private MetadataLoginEvent? pageFailure;
    private int attempts, blocked, failedResources, cookies;
    private bool account, session, token, sessionConfirmed, studentDocumentReady, accountConfirmed;
    public string PersistenceOutcome { get; private set; } = "not_saved";
    public string Path { get; } = path;
    public bool SessionConfirmed { get { lock (gate) return sessionConfirmed; } }
    public MetadataLoginReport Snapshot()
    {
        lock (gate) return new(1, platform.Id, attempts, blocked, failedResources, cookies, account, session, token, events.ToArray())
            { PageFailure = pageFailure, AccountConfirmed = accountConfirmed, Requests = requests.ToArray(), SessionChangeReason = sessionChangeReason };
    }
    public void RecordRequest(MetadataRequest request, MetadataResponse response)
    {
        if (!MetadataHttpReader.IsSupported(platform.Id, request)) return;
        var outcome = response.Outcome;
        if (outcome.Length is 0 or > 64 || outcome.Any(character => !char.IsAsciiLetterLower(character) && character != '_')) outcome = "unrecognized_response";
        lock (gate)
        {
            requests.Add(new(DateTimeOffset.UtcNow, request.Operation, outcome, response.StatusCode is >= 100 and <= 599 ? response.StatusCode : null));
            if (requests.Count > 16) requests.RemoveAt(0);
            Save();
        }
    }
    public void RecordSessionChange(string? reason)
    {
        lock (gate)
        {
            sessionChangeReason = reason is "profile_changed" or "cookie_writeback_pending" or "restore_pending"
                or "restore_login_changed" or "response_account_changed" ? reason : "generation_changed";
            Record(MetadataLoginStage.SessionValidation, MetadataLoginResult.SessionChanged);
        }
    }
    public void Record(MetadataLoginStage stage, MetadataLoginResult result, string? address = null, int? status = null, ulong? navigationId = null)
    {
        lock (gate)
        {
            if (stage == MetadataLoginStage.Preparation && result == MetadataLoginResult.Started)
            { attempts++; pageFailure = null; sessionChangeReason = null; sessionConfirmed = studentDocumentReady = accountConfirmed = false; }
            if (stage == MetadataLoginStage.MainNavigation)
                studentDocumentReady = result == MetadataLoginResult.Ready && platform.Id == "ucampus"
                    && platform.IsMetadataDocument(address ?? "");
            if (stage == MetadataLoginStage.SessionCheck)
            {
                sessionConfirmed = result == MetadataLoginResult.MetadataReady;
                accountConfirmed = result is MetadataLoginResult.MetadataReady or MetadataLoginResult.AccountReady;
            }
            if (result is MetadataLoginResult.Blocked or MetadataLoginResult.PopupSuppressed) blocked++;
            if (stage == MetadataLoginStage.Resource && result is MetadataLoginResult.ScriptUnavailable or MetadataLoginResult.NetworkFailed) failedResources++;
            events.Add(new(DateTimeOffset.UtcNow, stage, result, Classify(address), status is >= 100 and <= 599 ? status : null) { NavigationId = navigationId });
            if (stage == MetadataLoginStage.MainNavigation && result == MetadataLoginResult.Ready && navigationId is not null
                && pageFailure is { Stage: MetadataLoginStage.MainNavigation, Result: MetadataLoginResult.LoadingTimeout } timeout
                && timeout.NavigationId == navigationId) pageFailure = null;
            if (stage != MetadataLoginStage.SessionCheck && result is MetadataLoginResult.Blocked or MetadataLoginResult.LoadingTimeout
                or MetadataLoginResult.NetworkFailed or MetadataLoginResult.Failed or MetadataLoginResult.ScriptUnavailable)
            {
                // A later popup cannot replace a failure in the login document, frame, or script.
                if (pageFailure is null || result != MetadataLoginResult.LoadingTimeout && (stage != MetadataLoginStage.Popup || pageFailure.Stage == MetadataLoginStage.Popup)
                    || result == MetadataLoginResult.LoadingTimeout && pageFailure is { Stage: MetadataLoginStage.MainNavigation, Result: MetadataLoginResult.LoadingTimeout })
                    pageFailure = events[^1];
            }
            // Keep recovery in the diagnostic owner so closing the connection window cannot leave a stale popup failure.
            if (studentDocumentReady && sessionConfirmed
                && pageFailure is { Stage: MetadataLoginStage.Popup, Result: MetadataLoginResult.Blocked }) pageFailure = null;
            if (events.Count > 24) events.RemoveAt(0); Save();
        }
    }
    public void ObserveSession(IEnumerable<MetadataSessionCookie> values, bool tokenPresent)
    {
        lock (gate)
        {
            var current = values.Where(cookie => MetadataSessionStore.IsValid(platform, cookie) && !cookie.IsExpired(DateTimeOffset.UtcNow)).ToArray();
            var nextAccount = platform.Id switch
            { "zhihuishu" => MetadataPlatformParser.ZhihuishuAccount(current) is not null, "pta" => accountConfirmed, _ => MetadataPlatformParser.UcampusAccount(current) is not null };
            var nextSession = current.Any(cookie => platform.Id switch
            { "zhihuishu" => cookie.Name == "CASTGC", "pta" => platform.IsAccountCookie(cookie), _ => cookie.Name is "student_user" or "JSESSIONID" });
            var nextToken = platform.Id == "zhihuishu" && tokenPresent;
            if (PersistenceOutcome == "saved" && cookies == current.Length && account == nextAccount && session == nextSession && token == nextToken) return;
            cookies = current.Length; account = nextAccount; session = nextSession; token = nextToken; Save();
        }
    }
    public string Describe()
    {
        MetadataLoginReport report; MetadataLoginEvent? last;
        lock (gate) { report = Snapshot(); last = pageFailure ?? report.Events.LastOrDefault(); }
        if (last is null) return "尚未记录登录诊断。";
        var result = last.Result switch
        {
            MetadataLoginResult.Blocked => "官方跳转尚未适配",
            MetadataLoginResult.LoadingTimeout => "页面加载超时",
            MetadataLoginResult.NetworkFailed => "页面或资源请求失败",
            MetadataLoginResult.ScriptUnavailable => "登录脚本未能加载，请重新加载登录页",
            MetadataLoginResult.LoginRequired => "平台尚未确认登录",
            MetadataLoginResult.AccountUnknown => "账号归属尚未确认",
            MetadataLoginResult.AccountMismatch => "会话账号发生变化",
            MetadataLoginResult.MetadataReady => "账号和课程读取已确认",
            MetadataLoginResult.AccountReady => "登录已确认，课程目录尚未完整读取",
            MetadataLoginResult.AwaitingLogin => "正在等待官方登录完成并返回学生主页",
            MetadataLoginResult.SessionChanged => "会话在读取期间更新，已停止旧请求",
            MetadataLoginResult.PopupSuppressed => "登录已确认，登录后的弹窗已拦截",
            MetadataLoginResult.Failed => "登录环境或页面未完成加载",
            MetadataLoginResult.Ready => "官方页面已加载",
            _ => "正在准备或加载官方登录"
        };
        return $"最近登录诊断：{result}。已拦截跳转 {report.BlockedNavigations} 次、失败资源 {report.FailedResources} 项。"
            + (platform.Id == "zhihuishu" ? $"账号信息{(report.AccountPresent ? "已出现" : "尚未出现")}，登录验证{(report.AccountConfirmed ? "已通过" : "待确认")}。" : "")
            + (report.Requests.LastOrDefault() is { Outcome: not "ok" } request
                ? "最近读取：" + MetadataFailureText.Describe(request.Outcome, request.Status) + "。" : "");
    }
    private string Classify(string? address)
    {
        if (address is null) return "none";
        if (address == "about:blank") return "blank";
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return "unsupported";
        if (!platform.IsLoginFrameNavigation(address)) return "unsupported";
        return uri.IdnHost switch
        {
            "login.zhihuishu.com" => "login_center",
            "passport.zhihuishu.com" => "passport",
            "cloudapi.polymas.com" => "sso_return",
            "onlineweb.zhihuishu.com" or "onlinestuh5.zhihuishu.com" => "student_home",
            "open.weixin.qq.com" or "open.weixin.qq.com.cn" => "wechat",
            "sso.unipus.cn" => "unipus_login",
            "u.unipus.cn" => "legacy_student",
            "pintia.cn" => "pta_student",
            "passport.pintia.cn" => "pta_passport",
            "turing.captcha.qcloud.com" or "ca.turing.captcha.qcloud.com" => "captcha",
            _ => "official_other"
        };
    }
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            File.Move(temporary, Path, true); PersistenceOutcome = "saved";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { PersistenceOutcome = "unavailable"; }
    }
}
