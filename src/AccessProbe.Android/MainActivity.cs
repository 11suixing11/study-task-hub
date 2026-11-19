using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AccessProbe;
using System.Text.Json;
using View = Android.Views.View;
using Color = Android.Graphics.Color;
using Path = System.IO.Path;
using OperationCanceledException = System.OperationCanceledException;

namespace AccessProbe.Mobile;

[Activity(Label = "聚合平台", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask,
    EnableOnBackInvokedCallback = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public sealed partial class MainActivity : Activity
{
    private TaskStore store = null!;
    private MobileVault vault = null!;
    private MobileSync sync = null!;
    private LinearLayout page = null!;
    private TextView status = null!;
    private CancellationTokenSource? work;
    private WebView? web;
    private OfficialLoginClient? loginClient;
    private MobileLoginDiagnostics? loginDiagnostics;
    private string? loginPlatform;
    private string? selectedPlatform;
    private bool busy;
    private bool alive;
    private bool resumed;
    private bool checkingLogin;
    private bool taskEditorOpen;
    private bool syntheticUi;
    private CancellationTokenSource? loginLifetime;
    private CancellationTokenSource? periodic;
    private int viewRevision;
    private string? checkedLoginCookieBinding;
    private int checkedLoginNavigation;

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state); alive = true; RegisterSystemBack();
        if (Intent?.GetBooleanExtra("synthetic_performance_test", false) == true)
        {
            // The physical-device fixture is a separate application with its own
            // files, keystore alias and network-disabled build manifest.
            if (PackageName != "cn.yuki.learningaggregator.performance") { Finish(); return; }
            syntheticUi = true; _ = RunMobilePerformanceChecks(); return;
        }
        if (Intent?.GetBooleanExtra("synthetic_notification_test", false) == true)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            syntheticUi = true; WebView.SetDataDirectorySuffix("synthetic-notification");
            _ = RunMobileNotificationChecks(); return;
        }
        if (Intent?.GetStringExtra("synthetic_back_navigation_test") is { } backMode)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            syntheticUi = true; WebView.SetDataDirectorySuffix("synthetic-back-navigation");
            _ = PrepareBackNavigationChecks(backMode); return;
        }
        if (Intent?.GetBooleanExtra("synthetic_self_test", false) == true)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            _ = RunSelfTest(); return;
        }
        if (Intent?.GetStringExtra("synthetic_improvement_ui_test") is { } improvementMode)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            syntheticUi = true; _ = RunImprovementUiChecks(improvementMode); return;
        }
        if (Intent?.GetBooleanExtra("synthetic_course_filter_ui_test", false) == true)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            syntheticUi = true; _ = RunCourseFilterUiChecks(); return;
        }
        if (Intent?.GetStringExtra("synthetic_qr_test") is { } qrMode)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            syntheticUi = true; WebView.SetDataDirectorySuffix("synthetic-qr");
            OpenSyntheticUi(); CleanupOrphanedLoginQr(); _ = RunQrChecks(qrMode); return;
        }
        if (Intent?.GetBooleanExtra("synthetic_login_test", false) == true)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            syntheticUi = true; WebView.SetDataDirectorySuffix("synthetic-login");
            _ = RunLoginNavigationChecks(); return;
        }
        if (Intent?.GetStringExtra("synthetic_login_recovery_test") is { } recoveryMode)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            syntheticUi = true; WebView.SetDataDirectorySuffix("synthetic-login-recovery");
            _ = RunLoginRecoveryWebChecks(recoveryMode); return;
        }
        if (Intent?.GetBooleanExtra("synthetic_ui_test", false) == true)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            syntheticUi = true;
            OpenSyntheticUi(); CleanupOrphanedLoginQr(); Dashboard(); return;
        }
        BeginInitializeMobileData();
    }

    private void ScheduleReminders()
    {
        if (!syntheticUi) MobileReminderScheduler.Schedule(this);
    }

    private async Task ForegroundSyncLoop(CancellationToken token)
    {
        try
        {
            await Task.Delay(1500, token);
            while (!token.IsCancellationRequested)
            {
                if (resumed && !busy && !taskEditorOpen && !mobileNotificationEditing && web is null)
                {
                    var syncViewRevision = viewRevision;
                    var plan = await GetMobileUiData().LoadAutomaticSyncPlanAsync(token);
                    foreach (var platform in plan.Enabled ? plan.Platforms : [])
                    {
                        if (token.IsCancellationRequested || !resumed || web is not null || busy || taskEditorOpen || mobileNotificationEditing || syncViewRevision != viewRevision) break;
                        await RunSync(platform, false, false);
                    }
                    if (plan.Enabled) RefreshSyncView(syncViewRevision);
                }
                await Task.Delay(TimeSpan.FromMinutes(30), token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSync(string platform, bool catalogOnly, bool redraw = true)
    {
        if (busy) return; busy = true; work = new(TimeSpan.FromMinutes(10)); var token = work.Token;
        var syncViewRevision = viewRevision;
        var syncStatus = status;
        bool CurrentView() => alive && viewRevision == syncViewRevision && status == syncStatus && web is null && !mobileNotificationEditing;
        status.Text = "正在读取 " + PlatformCatalog.Name(platform) + (catalogOnly ? "课程…" : "任务…");
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => sync.RunAsync(platform, catalogOnly,
                new Progress<string>(message => RunOnUiThread(() => { if (busy && CurrentView()) syncStatus.Text = message; })), token), token);
            if (!alive) return;
            if (CurrentView())
            {
                if (redraw) RefreshSyncView(syncViewRevision);
                status.Text = result.Connection is { } connection
                    ? ConnectionFeedback.ForPlatformManagement(connection, platform == "uai" ? "校验官网概要" : "刷新课程").Text : result.Message;
            }
            ScheduleReminders();
        }
        catch (OperationCanceledException) { if (CurrentView()) syncStatus.Text = "本次同步已停止，已有任务保留。"; }
        catch { if (CurrentView()) syncStatus.Text = "读取未完成，请检查网络后重试。已有任务保留。"; }
        finally { busy = false; work.Dispose(); work = null; SchedulePendingMobileNotificationTask(); }
    }
    private void RefreshSyncView(int expectedRevision)
    {
        if (!alive || web is not null || taskEditorOpen || mobileNotificationEditing || viewRevision != expectedRevision) return;
        if (rootPage == "tasks" && selectedPlatform is null && dashboardList is not null)
        {
            ReloadDashboardSnapshot(); return;
        }
        var message = status.Text; var scrollY = (page.Parent as ScrollView)?.ScrollY ?? 0;
        if (selectedPlatform is { } platform) Platform(platform);
        else if (rootPage == "platforms") PlatformsPage();
        else if (rootPage == "settings") SettingsPage();
        else Dashboard();
        status.Text = message;
        if (page.Parent is ScrollView scroll)
        {
            var refreshedRevision = viewRevision; var pendingLoad = mobilePageLoad;
            _ = RestoreScroll();
            async Task RestoreScroll()
            {
                await pendingLoad;
                if (alive && viewRevision == refreshedRevision)
                    scroll.Post(() => { if (alive && viewRevision == refreshedRevision) scroll.ScrollTo(0, scrollY); });
            }
        }
    }
    private async Task Login(string platform)
    {
        if (syntheticUi) { status.Text = "隔离验收模式：官网登录由独立测试数据代替。"; return; }
        if (busy || checkingLogin) { status.Text = "请等待当前同步或登录检查结束。"; return; }
        loginLifetime?.Cancel(); loginLifetime?.Dispose(); loginLifetime = new();
        CreateLoginPage(platform);
        var loginWeb = web!;
        try
        {
            if (platform is "xuetang" or "zhihuishu") loginWeb.ClearCache(true);
            status.Text = "正在恢复已保存的登录…";
            var saved = await Task.Run(() => vault.LoadForRevalidation(platform));
            if (!alive || web != loginWeb) return;
            if (saved is not null) await MobilePlatforms.RestoreBrowserAsync(saved);
            if (!alive || web != loginWeb) return;
            web!.LoadUrl(MobilePlatforms.Login(platform).AbsoluteUri); status.Text = "请在本页完成官方登录，再确认会话并读取课程。";
            _ = MonitorLogin(platform, loginLifetime.Token);
        }
        catch { if (alive && web == loginWeb) { status.Text = "登录环境恢复未完成，请重新打开连接窗口。"; ShowLoginPageFailure("environment_failed"); } }
    }
    private void CreateLoginPage(string platform)
    {
        checkedLoginCookieBinding = null; checkedLoginNavigation = -1;
        loginDiagnostics = platform is "xuetang" or "zhihuishu" or "ucampus" ? new(platform,
            Path.Combine(MobileData.Root(this), syntheticUi ? "synthetic-login-recovery-test" : "diagnostics", platform + "-login.json")) : null;
        loginDiagnostics?.Record("preparation", "started");
        loginPlatform = platform; Layout("连接 " + PlatformCatalog.Name(platform), "登录 → 读取课程 → 选课同步", () => CloseLogin(platform));
        BuildConnectionCard(platform);
        Text(page, "使用下方官网表单登录；打开官方 App 后的登录不会自动共享给本应用。", 13);
        if (MobilePasswordlessLogin.SupportsQr(platform))
        {
            Pair(page, "官网扫码登录", async () => await PrepareOfficialQr(platform), "保存登录二维码", async () => await SaveOfficialQr(platform, false), true);
            Text(page, "也可用另一台设备扫描官网二维码。确认后，以本页官网会话校验结果为准。", 12);
        }
        else Text(page, MobilePasswordlessLogin.Guidance(platform), 13);
        Button(page, "继续应用内官网确认", () => web?.LoadUrl(MobileTaskRouting.Home(platform)), false);
        if (platform == "mooc") Button(page, "打开官网首页", () => web?.LoadUrl(MoocPlatform.Home.AbsoluteUri), false);
        if (platform == "xuetang") Text(page, "点击学堂在线官网右上角“登录”打开表单，完成后再点击上方确认按钮。", 13);
        web = new WebView(this); web.Settings.JavaScriptEnabled = true; web.Settings.DomStorageEnabled = true;
        web.Settings.AllowFileAccess = false; web.Settings.AllowContentAccess = false; web.Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        web.Settings.UseWideViewPort = true; web.Settings.LoadWithOverviewMode = true;
        web.Settings.SetSupportZoom(true); web.Settings.BuiltInZoomControls = true; web.Settings.DisplayZoomControls = false;
        web.Settings.SetSupportMultipleWindows(true); web.Settings.JavaScriptCanOpenWindowsAutomatically = true;
        if (platform is "xuetang" or "zhihuishu") web.Settings.CacheMode = CacheModes.NoCache;
        web.Settings.UserAgentString = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        Android.Webkit.CookieManager.Instance!.SetAcceptCookie(true); Android.Webkit.CookieManager.Instance.SetAcceptThirdPartyCookies(web, true);
        var loginStatus = status; var diagnostics = loginDiagnostics;
        Action<string> loginMessage = message => RunOnUiThread(() => { if (alive && status == loginStatus) loginStatus.Text = message; });
        Action<string> pageFailure = outcome => RunOnUiThread(() => { if (alive && status == loginStatus) ShowLoginPageFailure(outcome); });
        loginClient = new OfficialLoginClient(platform, loginMessage, LoginPageStarted, diagnostics, pageFailure);
        web.SetWebViewClient(loginClient);
        web.SetWebChromeClient(new OfficialLoginChromeClient(this, platform, address => web?.LoadUrl(address), loginMessage, diagnostics, pageFailure));
        page.AddView(web, new LinearLayout.LayoutParams(-1, Dp(650)));
        Button(page, "重新打开官方登录", async () => await ReopenOfficialLogin(platform), false);
        if (platform is "xuetang" or "zhihuishu" or "ucampus")
            Button(page, "清理本平台登录后重试", () => ConfirmResetOfficialLogin(platform), false);
        if (MobilePasswordlessLogin.SupportsQr(platform)) Text(page, "二维码过期时请在官网刷新后重新保存。不支持相册识别时可用另一台设备扫码；也可选择官网短信登录。退出连接页时会清理本应用保存的二维码。", 12);
    }
    private void CloseLogin(string platform)
    {
        qrNavigationRevision++; DeleteSavedLoginQr(); awaitingOfficialApp = false; pendingQrSave = null;
        loginLifetime?.Cancel(); if (checkingLogin) work?.Cancel(); web?.StopLoading(); web?.Destroy(); web = null; loginClient = null; loginPlatform = null; loginDiagnostics = null; Platform(platform);
        connectionTitle = connectionSteps = connectionExplanation = connectionNext = connectionDetails = null;
        connectionCheck = connectionContinue = null; connectionPageProblem = null; loginReport = null; loginPageFailure = null;
    }
    private async Task MonitorLogin(string platform, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && web is not null && loginPlatform == platform)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
                if (resumed && !busy) await CaptureLogin(platform, false);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task CaptureLogin(string platform, bool manual = true)
    {
        if (web is null) return;
        if (checkingLogin || busy) { if (manual) status.Text = "接入检查正在进行，结束后会显示结果，请稍候。"; return; }
        var currentWeb = web; var navigation = qrNavigationRevision;
        checkingLogin = true; SetConnectionBusy(true, manual);
        try { await CaptureLoginCore(platform, manual); }
        catch (OperationCanceledException) { if (alive && web == currentWeb && qrNavigationRevision == navigation) { ShowLoginFeedback(ConnectionFeedback.Interrupted(true, loginReport?.AccountVerified == true && !loginReport.LoginRequired)); status.Text = ""; } }
        catch { if (alive && web == currentWeb && qrNavigationRevision == navigation) { ShowLoginFeedback(ConnectionFeedback.Interrupted(false, loginReport?.AccountVerified == true && !loginReport.LoginRequired)); status.Text = ""; } }
        finally { checkingLogin = false; if (alive && web == currentWeb) SetConnectionBusy(false); SchedulePendingMobileNotificationTask(); }
    }
    private async Task CaptureLoginCore(string platform, bool manual)
    {
        if (web is null) return;
        if (!manual && loginClient?.FailureOutcome is { } pageFailure)
        { ShowLoginPageFailure(pageFailure); return; }
        var loginWeb = web;
        var navigationRevision = qrNavigationRevision;
        if (!TaskRules.IsSafeLink(loginWeb.Url) || !MobilePlatforms.Allowed(platform, loginWeb.Url ?? ""))
        { if (manual) ShowWaitingForLogin("请先返回下方本平台的官方登录页面，再检查接入。"); return; }
        // The main-site login dialog may already have an anonymous sessionid. Wait for the official course return.
        if (!manual && platform == "xuetang" && new Uri(loginWeb.Url!).AbsolutePath != XuetangPlatform.Home.AbsolutePath)
        { if (loginReport is not null) ShowConnectionReport(loginReport); else ShowWaitingForLogin(); return; }
        string? account = null; string? pageToken = null;
        var cookies = await MobilePlatforms.ReadBrowserCookiesAsync(platform, loginWeb);
        if (!alive || web != loginWeb || navigationRevision != qrNavigationRevision) return;
        loginDiagnostics?.Observe(cookies);
        if (!manual && platform != "uai" && !HasLoginEvidence(platform, cookies)) { ShowWaitingForLogin(); return; }
        var cookieBinding = MobileSessionRules.CookieFingerprint(platform, cookies);
        if (!manual && cookieBinding == checkedLoginCookieBinding && navigationRevision == checkedLoginNavigation
            && (loginReport?.AccountVerified == true || DateTimeOffset.UtcNow - lastLoginCheck < TimeSpan.FromSeconds(30)))
        { if (loginReport is not null) ShowConnectionReport(loginReport); return; }
        long? schoolId = null;
        if (platform == "uai")
        {
            if (!UaiPortal.IsPortalDocument(loginWeb.Url))
            { ShowWaitingForLogin("请完成登录，等待 U 校园 AI 版返回官网首页后检查接入。"); return; }
            var snapshot = await Evaluate(loginWeb, "(() => { if(location.origin!=='https://uai.unipus.cn') return null; try { const s=JSON.parse(localStorage.getItem('PORTAL_STATE_PERSISTENT')||'{}'); const t=s.tokenInfo&&s.tokenInfo.jwt||s.Authorization; return typeof t==='string'?JSON.stringify({token:t,school:typeof s.schId==='number'?s.schId:null}):null; } catch { return null; } })()");
            if (!alive || web != loginWeb || navigationRevision != qrNavigationRevision) return;
            try
            {
                var json = JsonSerializer.Deserialize<string>(snapshot);
                if (json is not null)
                {
                    using var parsed = JsonDocument.Parse(json);
                    pageToken = parsed.RootElement.GetProperty("token").GetString();
                    if (parsed.RootElement.GetProperty("school") is { ValueKind: JsonValueKind.Number } school && school.TryGetInt64(out var id)) schoolId = id;
                }
            }
            catch (JsonException) { }
            if (UaiPortal.TokenIdentity(pageToken) is not { } identity)
            { ShowWaitingForLogin("U 校园 AI 版官网尚未返回本应用可校验的登录，请在下方官网完成授权后重试。"); return; }
            account = identity.OpenId;
            var repeat = await Evaluate(loginWeb, "(() => { if(location.origin!=='https://uai.unipus.cn') return null; try { const s=JSON.parse(localStorage.getItem('PORTAL_STATE_PERSISTENT')||'{}'); return s.tokenInfo&&s.tokenInfo.jwt||s.Authorization||null; } catch { return null; } })()");
            if (!alive || web != loginWeb || navigationRevision != qrNavigationRevision) return;
            if (JsonSerializer.Deserialize<string>(repeat) != pageToken) return;
        }
        if (platform == "mooc")
        {
            if (!Uri.TryCreate(web.Url, UriKind.Absolute, out var address) || address.Host != MoocPlatform.Host || address.AbsolutePath != "/home.htm")
            { ShowWaitingForLogin("请先点击“打开官网首页”，等待 MOOC 返回首页后再检查接入。"); return; }
            var snapshot = await Evaluate(loginWeb, "(() => { if(location.origin!=='https://www.icourse163.org' || location.pathname!=='/home.htm') return ''; const u=window.webUser; return u ? String(u.id||'') : ''; })()");
            if (!alive || web != loginWeb || navigationRevision != qrNavigationRevision) return;
            try { account = JsonSerializer.Deserialize<string>(snapshot); } catch (JsonException) { }
            if (!MoocPlatform.IsId(account)) { ShowWaitingForLogin("MOOC 官网尚未确认账号，请完成本页登录后重试。"); return; }
        }
        if (platform is "ucampus" or "zhihuishu")
        {
            account = MobileSessionRules.Account(platform, cookies);
            if (account is null) { ShowWaitingForLogin("官网尚未返回可校验的登录，请确认官方授权并等待返回学生课程主页。"); return; }
        }
        if (platform == "zhihuishu")
        {
            if (!MetadataPlatform.Zhihuishu.IsMetadataDocument(web.Url ?? ""))
            { ShowWaitingForLogin("请等待智慧树返回学生课程主页，再检查接入。"); return; }
            var snapshot = await Evaluate("(() => { const read = w => { try { const l=w.location; if (l.origin==='https://onlineweb.zhihuishu.com' && l.pathname==='/onlinestuh5' || l.origin==='https://onlinestuh5.zhihuishu.com' && l.pathname==='/') return w.localStorage.getItem('token'); } catch {} return null; }; return read(window) || Array.from(window.frames).map(read).find(Boolean) || null; })()");
            if (!alive || web != loginWeb || navigationRevision != qrNavigationRevision) return;
            try { pageToken = JsonSerializer.Deserialize<string>(snapshot); } catch (JsonException) { }
            if (pageToken is { Length: > 16384 } || pageToken is not null && pageToken.Any(char.IsControl)) pageToken = null;
        }
        var after = await MobilePlatforms.ReadBrowserCookiesAsync(platform, loginWeb);
        if (!alive || web != loginWeb || navigationRevision != qrNavigationRevision || !TaskRules.IsSafeLink(loginWeb.Url)
            || !MobilePlatforms.Allowed(platform, loginWeb.Url ?? "") || MobileSessionRules.CookieFingerprint(platform, after) != cookieBinding) return;
        cookies = after;
        var previous = await Task.Run(() => vault.LoadForRevalidation(platform));
        if (!alive || web != loginWeb || navigationRevision != qrNavigationRevision) return;
        if (platform == "xuetang" && previous is not null && MobileSessionRules.CookieFingerprint(platform, previous.Cookies) == cookieBinding)
            account = previous.AccountId;
        if (cookies.Length == 0 && platform != "uai") { ShowWaitingForLogin("下方官网尚未返回本应用可校验的登录。已扫码时请确认官方授权，等待页面返回后再检查。"); return; }
        var loginSnapshot = new MobileSession(Guid.NewGuid().ToString("N"), platform, DateTimeOffset.UtcNow, cookies, account, pageToken,
            pageToken is null ? null : cookieBinding, cookies.Select(cookie => cookie.Key).ToArray(), schoolId);
        await Task.Run(() => vault.Save(loginSnapshot));
        if (!alive || web != loginWeb || navigationRevision != qrNavigationRevision) return;
        Android.Webkit.CookieManager.Instance!.Flush();
        busy = true; work = new(TimeSpan.FromMinutes(10));
        SetConnectionBusy(true);
        status.Text = "正在确认官网会话并读取课程…";
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => sync.RunAsync(platform, true,
                new Progress<string>(message => RunOnUiThread(() => { if (alive && web == loginWeb && navigationRevision == qrNavigationRevision && busy) status.Text = message; })), work.Token,
                verifiedBrowserAccount: platform == "mooc" ? account : null), work.Token);
            if (!alive || web != loginWeb || navigationRevision != qrNavigationRevision) return;
            loginDiagnostics?.Complete(result);
            checkedLoginCookieBinding = cookieBinding; checkedLoginNavigation = navigationRevision;
            lastLoginCheck = DateTimeOffset.UtcNow;
            if (result.Connection is { } connection) ShowConnectionReport(connection);
            else status.Text = result.Message;
        }
        finally { busy = false; work.Dispose(); work = null; }
    }
    private static bool HasLoginEvidence(string platform, IReadOnlyList<MetadataSessionCookie> cookies) => platform switch
    {
        "zhihuishu" or "ucampus" => MobileSessionRules.Account(platform, cookies) is not null,
        "pta" => cookies.Any(cookie => cookie.Name == "PTASession" && cookie.Value.Length > 0),
        "mooc" => cookies.Any(cookie => cookie.Name == "NTESSTUDYSI" && cookie.Value.Length > 0),
        "xuetang" or "yuketang" or "changjiang" => cookies.Any(cookie => cookie.Name is "sessionid" or "xtsessionid" && cookie.Value.Length > 0),
        _ => cookies.Any(cookie => cookie.Name is "_uid" or "UID" && cookie.Value.Length > 0)
    };
    private Task<string> Evaluate(string script)
    {
        return Evaluate(web!, script);
    }
    internal static Task<string> Evaluate(WebView target, string script)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); target.EvaluateJavascript(script, new ScriptResult(value => result.TrySetResult(value))); return result.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private void AddTask()
    {
        taskEditorOpen = true; SetMobileNotificationEditing(true); Layout("添加任务", "手动记录课程事项", Dashboard);
        var platforms = new Spinner(this); platforms.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, MobilePlatforms.All.Select(PlatformCatalog.Name).ToArray()); page.AddView(platforms);
        var title = new EditText(this) { Hint = "任务名称" }; page.AddView(title); var course = new EditText(this) { Hint = "课程名称（可选）" }; page.AddView(course);
        var notes = new EditText(this) { Hint = "备注（可选）" }; page.AddView(notes); DateTimeOffset? deadline = null;
        var date = Button(page, "设置截止日期（可选）", () => { }, false);
        date.Click += (_, _) =>
        {
            var now = DateTime.Now;
            new DatePickerDialog(this, (_, args) =>
                new TimePickerDialog(this, (_, time) => { deadline = new DateTimeOffset(args.Year, args.Month + 1, args.DayOfMonth, time.HourOfDay, time.Minute, 0, TimeZoneInfo.Local.GetUtcOffset(now)); date.Text = deadline.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }, now.Hour, now.Minute, true).Show(), now.Year, now.Month - 1, now.Day).Show();
        };
        Button(page, "保存任务", () =>
        {
            try
            {
                var draft = new TaskDraft(title.Text ?? "", MobilePlatforms.All[Math.Max(0, platforms.SelectedItemPosition)], course.Text ?? "",
                    deadline is null ? DeadlineKind.Unknown : DeadlineKind.Known, null, deadline, null, null, "", notes.Text ?? "", null);
                TaskRules.Validate(draft);
                var currentStore = store;
                mobileTaskWrite = SaveMobileTaskAsync(() => currentStore.Create(draft, DateTimeOffset.UtcNow), _ => Dashboard(), "正在保存任务…");
            }
            catch (TaskValidationException error) { status.Text = error.Message; }
        });
    }
    private static string Deadline(TaskItem task) => TaskRules.DeadlineName(task);
    private static string StateText(TaskState state) => state switch { TaskState.Completed => "已完成", TaskState.Ignored => "已忽略", TaskState.Returned => "被打回", TaskState.NeedsReview => "待核对", _ => "待办" };
    public override void OnBackPressed()
    {
        HandleBackNavigation("legacy");
    }
    protected override void OnResume()
    {
        base.OnResume(); resumed = true; ResetExitConfirmation();
        ResumeMobileNotificationSettings();
        if (awaitingOfficialApp && loginPlatform is { } platform)
        { awaitingOfficialApp = false; _ = ResumeOfficialAppLogin(platform); }
        else if (HasPendingMobileNotificationTask) TryOpenPendingMobileNotificationTask();
        else if (externalTaskId is { } id) { externalTaskId = null; _ = ResumeTask(id); }
        else if (dashboardList is not null && mobileUiData is not null && !mobileUiData.TryGetCachedDashboard(out _)) ReloadDashboardSnapshot();
    }
    protected override void OnPause() { resumed = false; ResetExitConfirmation(); if (web is not null) Android.Webkit.CookieManager.Instance!.Flush(); base.OnPause(); }
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ResetExitConfirmation();
        if (intent is not null) Intent = intent;
        if (!mobileDataReady && !syntheticUi) return;
        OpenNotificationTask(intent);
    }
    protected override void OnDestroy() { alive = false; ReleaseMobileUiData(); ClearDashboardView(); if (mobileObservedStore is not null) mobileObservedStore.Changed -= OnMobileStoreUpdated; mobileDataRefreshDelay?.Cancel(); mobileDataRefreshDelay?.Dispose(); ReleaseSystemBack(); navigationLifetime?.Cancel(); navigationLifetime?.Dispose(); DeleteSavedLoginQr(); loginLifetime?.Cancel(); loginLifetime?.Dispose(); periodic?.Cancel(); periodic?.Dispose(); work?.Cancel(); web?.Destroy(); base.OnDestroy(); }

    private void OpenNotificationTask(Intent? intent)
    {
        if (!syntheticUi) DisplayNotificationTask(intent);
    }

    private bool DisplayNotificationTask(Intent? intent)
    {
        return ReceiveMobileNotificationTask(intent);
    }

    private async Task RunSelfTest()
    {
        var testRoot = System.IO.Path.Combine(MobileData.Root(this), "synthetic-installation-test");
        System.IO.Directory.CreateDirectory(testRoot);
        var output = System.IO.Path.Combine(testRoot, "self-test.json");
        try
        {
            var testProtector = new AndroidDataProtector("LearningTaskAggregator.SyntheticInstallation.v1");
            var result = await MobileSyntheticChecks.RunAsync(testRoot, testProtector, Intent?.GetBooleanExtra("synthetic_restart_check", false) == true);
            if (!result.RestartChecked)
            {
                var improvements = await MobileImprovementChecks.RunAsync(System.IO.Path.Combine(testRoot, "improvements", Guid.NewGuid().ToString("N")), testProtector);
                System.IO.File.WriteAllText(System.IO.Path.Combine(testRoot, "improvement-checks.json"), JsonSerializer.Serialize(improvements));
                var recovery = await MobileLoginRecoveryChecks.RunAsync(System.IO.Path.Combine(testRoot, "login-recovery", Guid.NewGuid().ToString("N")), testProtector);
                System.IO.File.WriteAllText(System.IO.Path.Combine(testRoot, "login-recovery-checks.json"), JsonSerializer.Serialize(recovery));
                var connection = await MobileConnectionChecks.RunAsync(System.IO.Path.Combine(testRoot, "connection", Guid.NewGuid().ToString("N")), testProtector);
                System.IO.File.WriteAllText(System.IO.Path.Combine(testRoot, "connection-checks.json"), JsonSerializer.Serialize(connection));
            }
            System.IO.File.WriteAllText(output, JsonSerializer.Serialize(result));
        }
        catch (Exception error) { System.IO.File.WriteAllText(output, JsonSerializer.Serialize(new { outcome = "failed", errorType = error.GetType().Name, officialNetworkAccessed = false, productionDataAccessed = false })); }
        Finish();
    }
}

internal sealed class SystemInsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
{
    public WindowInsets OnApplyWindowInsets(View? view, WindowInsets? insets)
    {
        if (view is null || insets is null) return insets!;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var safe = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout() | WindowInsets.Type.Ime());
            view.SetPadding(safe.Left, safe.Top, safe.Right, safe.Bottom);
        }
        else view.SetPadding(insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
        return insets;
    }
}

internal sealed class ScriptResult(Action<string> callback) : Java.Lang.Object, IValueCallback
{
    public void OnReceiveValue(Java.Lang.Object? value) => callback(value?.ToString() ?? "null");
}

internal class OfficialLoginClient(string platform, Action<string> message, Action started, MobileLoginDiagnostics? diagnostics = null,
    Action<string>? failure = null) : WebViewClient
{
    private readonly HttpsUpgradeChain httpsUpgrades = new();
    private int pageRevision;
    private bool pageFailed;
    public string? FailureOutcome { get; private set; }
    public void ResetNavigation() { httpsUpgrades.Reset(); pageFailed = false; FailureOutcome = null; }
    public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon)
    {
        pageRevision++; pageFailed = false; FailureOutcome = null; started(); diagnostics?.Record("main", "started", url);
        base.OnPageStarted(view, url, favicon);
    }
    public override void OnPageFinished(WebView? view, string? url)
    {
        if (!pageFailed) diagnostics?.Record("main", "ready", url);
        if (view is not null && platform is "xuetang" or "zhihuishu" && TaskRules.IsSafeLink(url) && MobilePlatforms.Allowed(platform, url!))
            _ = CheckPageContent(view, pageRevision);
        base.OnPageFinished(view, url);
    }
    private async Task CheckPageContent(WebView view, int revision)
    {
        try
        {
            await Task.Delay(900);
            if (view.Handle == IntPtr.Zero || revision != pageRevision || pageFailed) return;
            var visible = await MainActivity.Evaluate(view,
                "(() => { const b=document.body; return !!b && (!!b.innerText.trim() || !!b.querySelector('input,button,img,canvas,svg,iframe')); })()");
            if (view.Handle == IntPtr.Zero || revision != pageRevision || pageFailed || visible != "false") return;
            diagnostics?.Record("renderer", "blank", view.Url);
            message("官方登录页没有显示内容，请点击“重新打开官方登录”重试，并确认系统 Android WebView 已更新。");
            failure?.Invoke("blank_page");
            FailureOutcome = "blank_page";
        }
        catch { }
    }
    public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
    {
        var address = request?.Url?.ToString() ?? "";
        var allowed = request?.IsForMainFrame == true ? MobilePlatforms.Allowed(platform, address) : MobilePlatforms.FrameAllowed(platform, address);
        if (allowed) return false;
        if (request?.IsForMainFrame == true && platform == "chaoxing" && view is not null
            && ProbePolicy.TryGetHttpsNavigation(address, out var upgraded))
        {
            if (request.HasGesture) httpsUpgrades.Reset();
            if (httpsUpgrades.TrySchedule(upgraded))
            {
                message("正在打开扫码确认后的官方页面…");
                view.Post(() => { if (view.Handle != IntPtr.Zero) view.LoadUrl(upgraded); });
            }
            else { message("官方页面反复跳回旧链接，请重新打开官方登录后重试。"); FailureOutcome = "unsupported_redirect"; failure?.Invoke(FailureOutcome); }
            return true;
        }
        diagnostics?.Record(request?.IsForMainFrame == true ? "main" : "frame", "blocked", address);
        if (request?.IsForMainFrame == true) { message(LoginNavigationMessage.ForBlocked(address)); FailureOutcome = "unsupported_redirect"; failure?.Invoke(FailureOutcome); }
        return !allowed;
    }
    public override void OnReceivedSslError(WebView? view, SslErrorHandler? handler, Android.Net.Http.SslError? error)
    { handler?.Cancel(); pageFailed = true; FailureOutcome = "certificate_failed"; diagnostics?.Record("certificate", "failed", error?.Url); message("官网证书验证失败，请检查网络后重试。"); failure?.Invoke("certificate_failed"); }
    public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
    {
        if (request?.IsForMainFrame == true)
        { pageFailed = true; FailureOutcome = error?.ErrorCode == ClientError.Timeout ? "timeout" : "network_error"; diagnostics?.Record("main", "network_failed", request.Url?.ToString()); message("官方登录页加载失败，请检查网络并重新打开官方登录。"); failure?.Invoke(FailureOutcome); }
        else ResourceFailure(request?.Url?.ToString() ?? "", null);
    }
    public override void OnReceivedHttpError(WebView? view, IWebResourceRequest? request, WebResourceResponse? errorResponse)
    {
        if (request?.IsForMainFrame == true)
        {
            pageFailed = true; diagnostics?.Record("main", "http_failed", request.Url?.ToString(), errorResponse?.StatusCode);
            message(errorResponse?.StatusCode == 429 ? "官方登录服务暂时限制请求，请稍后重试。" : "官方登录页请求未成功，请点击“重新打开官方登录”重试。");
            FailureOutcome = errorResponse?.StatusCode switch { 429 => "rate_limited", 403 => "forbidden", 401 => "verification_required", _ => "http_error" };
            failure?.Invoke(FailureOutcome);
        }
        else ResourceFailure(request?.Url?.ToString() ?? "", errorResponse?.StatusCode);
    }
    private void ResourceFailure(string address, int? statusCode)
    {
        var kind = MobileLoginDiagnostics.ResourceKind(platform, address);
        if (kind == "none") return;
        diagnostics?.Record("resource", kind == "script" ? "script_failed" : "style_failed", address, statusCode);
        message(kind == "script" ? "官方登录脚本未能加载，请点击“重新打开官方登录”重试。" : "官方登录样式未能加载，请点击“重新打开官方登录”重试。");
        failure?.Invoke(kind == "script" ? "script_failed" : "style_failed");
        FailureOutcome = kind == "script" ? "script_failed" : "style_failed";
    }
    public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
    {
        pageRevision++; pageFailed = true; diagnostics?.Record("renderer", "failed");
        message("系统网页组件已停止，请退出连接页后重新打开官方登录。");
        FailureOutcome = "renderer_failed"; failure?.Invoke(FailureOutcome);
        // Returning false lets Android dispose of every WebView sharing the terminated renderer safely.
        return false;
    }
}

internal sealed class OfficialLoginChromeClient(Context context, string platform, Action<string> navigate, Action<string> message,
    MobileLoginDiagnostics? diagnostics = null, Action<string>? failure = null) : WebChromeClient
{
    public override bool OnConsoleMessage(ConsoleMessage? consoleMessage)
    {
        if (consoleMessage?.InvokeMessageLevel()?.Equals(ConsoleMessage.MessageLevel.Error) == true)
            diagnostics?.Record("renderer", "script_failed");
        return true;
    }
    public override bool OnCreateWindow(WebView? view, bool isDialog, bool isUserGesture, Message? resultMsg)
    {
        if (resultMsg?.Obj is not WebView.WebViewTransport transport) return false;
        var popup = new WebView(context);
        popup.Settings.JavaScriptEnabled = true; popup.Settings.DomStorageEnabled = true;
        popup.Settings.AllowFileAccess = false; popup.Settings.AllowContentAccess = false; popup.Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        if (view is not null) { popup.Settings.UserAgentString = view.Settings.UserAgentString; popup.Settings.CacheMode = view.Settings.CacheMode; }
        Android.Webkit.CookieManager.Instance!.SetAcceptThirdPartyCookies(popup, true);
        diagnostics?.Record("popup", "started");
        popup.SetWebViewClient(new PopupRedirectClient(platform, navigate, message, diagnostics, failure));
        transport.WebView = popup; resultMsg.SendToTarget(); return true;
    }

    private sealed class PopupRedirectClient(string platform, Action<string> navigate, Action<string> message, MobileLoginDiagnostics? diagnostics, Action<string>? failure) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.IsForMainFrame != true) return !MobilePlatforms.FrameAllowed(platform, request?.Url?.ToString() ?? "");
            var address = request.Url?.ToString() ?? "";
            if (address == "about:blank") return false;
            if (MobilePlatforms.Allowed(platform, address)) { diagnostics?.Record("popup", "ready", address); navigate(address); }
            else if (platform == "chaoxing" && ProbePolicy.TryGetHttpsNavigation(address, out var upgraded)) navigate(upgraded);
            else { diagnostics?.Record("popup", "blocked", address); message(LoginNavigationMessage.ForBlocked(address)); failure?.Invoke("unsupported_redirect"); }
            view?.StopLoading(); view?.Post(() => view.Destroy()); return true;
        }
        public override void OnReceivedSslError(WebView? view, SslErrorHandler? handler, Android.Net.Http.SslError? error)
        { handler?.Cancel(); diagnostics?.Record("certificate", "failed", error?.Url); failure?.Invoke("certificate_failed"); }
    }
}
