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
using OperationCanceledException = System.OperationCanceledException;

namespace AccessProbe.Mobile;

[Activity(Label = "聚合平台", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask,
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

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state); alive = true;
        if (Intent?.GetBooleanExtra("synthetic_self_test", false) == true)
        {
            _ = RunSelfTest(); return;
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
        if (Intent?.GetBooleanExtra("synthetic_ui_test", false) == true)
        {
            if (Build.Hardware is not ("ranchu" or "goldfish")) { Finish(); return; }
            syntheticUi = true;
            OpenSyntheticUi(); CleanupOrphanedLoginQr(); Dashboard(); return;
        }
        var protector = new AndroidDataProtector(); store = MobileData.Open(this, protector); vault = new(store, protector); sync = new(store, vault);
        CleanupOrphanedLoginQr(); Dashboard(); ScheduleReminders();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 301);
        periodic = new(); _ = ForegroundSyncLoop(periodic.Token);
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
                if (resumed && !busy && !taskEditorOpen && web is null && store.LoadSettings().AutomaticSyncEnabled)
                {
                    var syncViewRevision = viewRevision;
                    foreach (var platform in MobilePlatforms.All)
                    {
                        if (token.IsCancellationRequested || !resumed || web is not null || busy || taskEditorOpen || syncViewRevision != viewRevision) break;
                        if (vault.Load(platform) is not null && store.LoadCourseRecording(platform).SelectionConfirmed)
                            await RunSync(platform, false, false);
                    }
                    RefreshSyncView(syncViewRevision);
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
        status.Text = "正在读取 " + PlatformCatalog.Name(platform) + (catalogOnly ? "课程…" : "任务…");
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => sync.RunAsync(platform, catalogOnly, new Progress<string>(message => RunOnUiThread(() => { if (alive) status.Text = message; })), token), token);
            if (!alive) return;
            if (redraw) RefreshSyncView(syncViewRevision);
            status.Text = result.LoginRequired ? "登录已过期，请点击“连接平台”重新登录。" : string.Join("、", result.Codes) + " " + result.Message;
            ScheduleReminders();
        }
        catch (OperationCanceledException) { if (alive) status.Text = "本次同步已停止，已有任务保留。"; }
        catch { if (alive) status.Text = "读取未完成，请检查网络后重试。已有任务保留。"; }
        finally { busy = false; work.Dispose(); work = null; }
    }
    private void RefreshSyncView(int expectedRevision)
    {
        if (!alive || web is not null || taskEditorOpen || viewRevision != expectedRevision) return;
        var message = status.Text; var scrollY = (page.Parent as ScrollView)?.ScrollY ?? 0;
        if (selectedPlatform is { } platform) Platform(platform);
        else if (rootPage == "platforms") PlatformsPage();
        else if (rootPage == "settings") SettingsPage();
        else Dashboard();
        status.Text = message;
        if (page.Parent is ScrollView scroll) scroll.Post(() => scroll.ScrollTo(0, scrollY));
    }
    private async Task Login(string platform)
    {
        if (syntheticUi) { status.Text = "隔离验收模式：官网登录由独立测试数据代替。"; return; }
        if (busy) { status.Text = "请先停止当前同步。"; return; }
        loginLifetime?.Cancel(); loginLifetime?.Dispose(); loginLifetime = new();
        CreateLoginPage(platform);
        var loginWeb = web!;
        try
        {
            status.Text = "正在恢复已保存的登录…";
            if (vault.Load(platform) is { } saved) await MobilePlatforms.RestoreBrowserAsync(saved);
            else if (store.LoadPlatformSessionInfo(platform).State == "expired") await MobilePlatforms.ClearBrowserAsync(platform, null);
            if (!alive || web != loginWeb) return;
            web!.LoadUrl(MobilePlatforms.Login(platform).AbsoluteUri); status.Text = "请完成官方登录；确认后会自动保存并读取课程。";
            _ = MonitorLogin(platform, loginLifetime.Token);
        }
        catch { if (alive && web == loginWeb) { status.Text = "登录环境恢复未完成，请重新打开连接窗口。"; } }
    }
    private void CreateLoginPage(string platform)
    {
        loginPlatform = platform; Layout("连接 " + PlatformCatalog.Name(platform), "官方确认后保存登录并读取课程。", () => CloseLogin(platform));
        if (MobilePasswordlessLogin.SupportsQr(platform))
        {
            Pair(page, "官网扫码登录", async () => await PrepareOfficialQr(platform), "保存登录二维码", async () => await SaveOfficialQr(platform, false), true);
            Button(page, "保存并打开" + MobilePasswordlessLogin.AppName(platform), async () => await SaveOfficialQr(platform, true), false);
            Text(page, "在官方 App 的扫一扫中选相册，识别二维码并确认；返回后自动检查登录。", 12);
        }
        else Text(page, MobilePasswordlessLogin.Guidance(platform), 13);
        Button(page, "登录完成并读取课程", async () => await CaptureLogin(platform));
        if (platform == "mooc") Button(page, "打开官网首页", () => web?.LoadUrl(MoocPlatform.Home.AbsoluteUri), false);
        if (platform == "xuetang") Text(page, "点击学堂在线官网右上角“登录”打开表单。", 13);
        web = new WebView(this); web.Settings.JavaScriptEnabled = true; web.Settings.DomStorageEnabled = true;
        web.Settings.AllowFileAccess = false; web.Settings.AllowContentAccess = false; web.Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        web.Settings.UseWideViewPort = true; web.Settings.LoadWithOverviewMode = true;
        web.Settings.SetSupportZoom(true); web.Settings.BuiltInZoomControls = true; web.Settings.DisplayZoomControls = false;
        web.Settings.SetSupportMultipleWindows(true); web.Settings.JavaScriptCanOpenWindowsAutomatically = true;
        web.Settings.UserAgentString = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        Android.Webkit.CookieManager.Instance!.SetAcceptCookie(true); Android.Webkit.CookieManager.Instance.SetAcceptThirdPartyCookies(web, true);
        loginClient = new OfficialLoginClient(platform, message => RunOnUiThread(() => status.Text = message), LoginPageStarted);
        web.SetWebViewClient(loginClient);
        web.SetWebChromeClient(new OfficialLoginChromeClient(this, platform, address => web?.LoadUrl(address), message => status.Text = message));
        page.AddView(web, new LinearLayout.LayoutParams(-1, Dp(650)));
        Button(page, "重新打开官方登录", () => { DeleteSavedLoginQr(); loginClient?.ResetNavigation(); web?.LoadUrl(MobilePlatforms.Login(platform).AbsoluteUri); }, false);
        if (MobilePasswordlessLogin.SupportsQr(platform)) Text(page, "二维码过期时请在官网刷新后重新保存。不支持相册识别时可用另一台设备扫码；也可选择官网短信登录。退出连接页时会清理本应用保存的二维码。", 12);
    }
    private void CloseLogin(string platform)
    {
        qrNavigationRevision++; DeleteSavedLoginQr(); awaitingOfficialApp = false; pendingQrSave = null;
        loginLifetime?.Cancel(); web?.StopLoading(); web?.Destroy(); web = null; loginClient = null; loginPlatform = null; Platform(platform);
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
        if (web is null || checkingLogin || busy) return;
        checkingLogin = true;
        try { await CaptureLoginCore(platform, manual); }
        catch (OperationCanceledException) { if (alive && manual) status.Text = "官网登录页面已更新，请确认当前账号后重试。"; }
        catch { if (alive && manual) status.Text = "官网登录确认未完成，请等待页面加载后重试。"; }
        finally { checkingLogin = false; }
    }
    private async Task CaptureLoginCore(string platform, bool manual)
    {
        if (web is null) return;
        var loginWeb = web;
        string? account = null; string? pageToken = null;
        var cookies = MobilePlatforms.ReadBrowserCookies(platform);
        if (!manual && !HasLoginEvidence(platform, cookies)) return;
        var cookieBinding = MobileSessionRules.CookieFingerprint(platform, cookies);
        if (platform == "mooc")
        {
            if (!Uri.TryCreate(web.Url, UriKind.Absolute, out var address) || address.Host != MoocPlatform.Host || address.AbsolutePath != "/home.htm")
            { if (manual) status.Text = "请先打开官网首页，再确认登录。"; return; }
            var snapshot = await Evaluate("(() => { const u=window.webUser; return u ? String(u.id||'') : ''; })()");
            try { account = JsonSerializer.Deserialize<string>(snapshot); } catch (JsonException) { }
            if (!MoocPlatform.IsId(account)) { if (manual) status.Text = "官网尚未确认账号，请完成登录后重试。"; return; }
        }
        if (platform is "ucampus" or "zhihuishu")
        {
            account = MobileSessionRules.Account(platform, cookies);
            if (account is null) { if (manual) status.Text = "正在等待官网确认登录；请登录后返回学生课程主页。"; return; }
        }
        if (platform == "zhihuishu")
        {
            if (!MetadataPlatform.Zhihuishu.IsMetadataDocument(web.Url ?? ""))
            { if (manual) status.Text = "请等待智慧树返回学生课程主页，再确认登录。"; return; }
            var snapshot = await Evaluate("(() => { const read = w => { try { const l=w.location; if (l.origin==='https://onlineweb.zhihuishu.com' && l.pathname==='/onlinestuh5' || l.origin==='https://onlinestuh5.zhihuishu.com' && l.pathname==='/') return w.localStorage.getItem('token'); } catch {} return null; }; return read(window) || Array.from(window.frames).map(read).find(Boolean) || null; })()");
            try { pageToken = JsonSerializer.Deserialize<string>(snapshot); } catch (JsonException) { }
            if (pageToken is { Length: > 16384 } || pageToken is not null && pageToken.Any(char.IsControl)) pageToken = null;
        }
        var after = MobilePlatforms.ReadBrowserCookies(platform);
        if (!alive || web != loginWeb || MobileSessionRules.CookieFingerprint(platform, after) != cookieBinding) return;
        cookies = after;
        if (cookies.Length == 0) { status.Text = "尚未取得官网登录 Cookie，请先完成登录。"; return; }
        vault.Save(new(Guid.NewGuid().ToString("N"), platform, DateTimeOffset.UtcNow, cookies, account, pageToken,
            pageToken is null ? null : cookieBinding, cookies.Select(cookie => cookie.Key).ToArray()));
        Android.Webkit.CookieManager.Instance!.Flush();
        busy = true; work = new(TimeSpan.FromMinutes(10));
        status.Text = "正在确认官网会话并读取课程…";
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => sync.RunAsync(platform, true, cancellation: work.Token), work.Token);
            if (!alive || web != loginWeb) return;
            if (result.LoginRequired || !result.AccountVerified)
            {
                status.Text = string.Join("、", result.Codes) + " " + (result.LoginRequired ? "官网仍要求登录，请完成登录后等待返回课程主页。" : result.Message);
                return;
            }
            CloseLogin(platform); status.Text = result.CatalogCovered ? "登录已保存，请勾选课程后点击“保存选择并采集”。" : result.Message;
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
    private static Task<string> Evaluate(WebView target, string script)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); target.EvaluateJavascript(script, new ScriptResult(value => result.TrySetResult(value))); return result.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private void TaskDetail(string id)
    {
        var task = store.Get(id); if (task is null) { Dashboard(); return; }
        taskEditorOpen = true;
        Layout(task.Title, PlatformCatalog.Name(task.PlatformId) + " · " + task.CourseName, Dashboard);
        var info = Card(page); Text(info, StateText(task.State) + " · " + Deadline(task), 17, true); Text(info, task.PlatformStatusText, 14);
        Text(info, task.AutomaticStatusExplanation, 14); Text(info, task.DisplayedTimeText, 13); Text(info, task.Notes, 14);
        Button(info, "打开官方任务", () =>
        {
            if (syntheticUi) { status.Text = "隔离验收模式：已拦截外部任务入口。"; return; }
            if (!TaskRules.IsSafeLink(task.Link)) { status.Text = "官方入口尚未确认，请到官网课程列表核对。"; return; }
            try { StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(task.Link!))); }
            catch (ActivityNotFoundException) { status.Text = "未找到可用浏览器。"; }
        });
        foreach (var state in new[] {TaskState.Pending, TaskState.Completed, TaskState.Ignored, TaskState.NeedsReview})
            Button(info, "标记为" + StateText(state), () =>
            {
                try { store.SetState(task.Id, task.Revision, state, DateTimeOffset.UtcNow); ScheduleReminders(); Dashboard(); }
                catch (TaskValidationException error) { status.Text = error.Message; }
            }, false);
    }
    private void AddTask()
    {
        taskEditorOpen = true; Layout("添加任务", "手动记录课程事项", Dashboard);
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
            try { store.Create(new(title.Text ?? "", MobilePlatforms.All[Math.Max(0, platforms.SelectedItemPosition)], course.Text ?? "", deadline is null ? DeadlineKind.Unknown : DeadlineKind.Known, null, deadline, null, null, "", notes.Text ?? "", null), DateTimeOffset.UtcNow); ScheduleReminders(); Dashboard(); }
            catch (TaskValidationException error) { status.Text = error.Message; }
        });
    }
    private static string Deadline(TaskItem task) => task.DeadlineKind == DeadlineKind.None ? "无截止时间" : task.DeadlineUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? "截止待核对";
    private static string StateText(TaskState state) => state switch { TaskState.Completed => "已完成", TaskState.Ignored => "已忽略", TaskState.Returned => "被打回", TaskState.NeedsReview => "待核对", _ => "待办" };
    public override void OnBackPressed()
    {
        if (loginPlatform is { } platform) { work?.Cancel(); CloseLogin(platform); }
        else if (pageBack is not null) pageBack();
        else if (rootPage != "tasks") Dashboard();
        else MoveTaskToBack(true);
    }
    protected override void OnResume()
    {
        base.OnResume(); resumed = true;
        if (awaitingOfficialApp && loginPlatform is { } platform)
        { awaitingOfficialApp = false; _ = ResumeOfficialAppLogin(platform); }
    }
    protected override void OnPause() { resumed = false; base.OnPause(); }
    protected override void OnDestroy() { alive = false; DeleteSavedLoginQr(); loginLifetime?.Cancel(); loginLifetime?.Dispose(); periodic?.Cancel(); periodic?.Dispose(); work?.Cancel(); web?.Destroy(); base.OnDestroy(); }

    private async Task RunSelfTest()
    {
        var testRoot = System.IO.Path.Combine(MobileData.Root(this), "synthetic-installation-test");
        System.IO.Directory.CreateDirectory(testRoot);
        var output = System.IO.Path.Combine(testRoot, "self-test.json");
        try
        {
            var testProtector = new AndroidDataProtector("LearningTaskAggregator.SyntheticInstallation.v1");
            var result = await MobileSyntheticChecks.RunAsync(testRoot, testProtector, Intent?.GetBooleanExtra("synthetic_restart_check", false) == true);
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

internal class OfficialLoginClient(string platform, Action<string> message, Action started) : WebViewClient
{
    private readonly HttpsUpgradeChain httpsUpgrades = new();
    public void ResetNavigation() => httpsUpgrades.Reset();
    public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon) { started(); base.OnPageStarted(view, url, favicon); }
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
            else message("官方页面反复跳回旧链接，请重新打开官方登录后重试。");
            return true;
        }
        if (!allowed && request?.IsForMainFrame == true) message("该登录跳转尚未适配，请返回官方登录后重试。");
        return !allowed;
    }
    public override void OnReceivedSslError(WebView? view, SslErrorHandler? handler, Android.Net.Http.SslError? error)
    { handler?.Cancel(); message("官网证书验证失败，请检查网络后重试。"); }
    public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
    { if (request?.IsForMainFrame == true) message("官方登录页加载失败，请检查网络并重新打开官方登录。"); }
}

internal sealed class OfficialLoginChromeClient(Context context, string platform, Action<string> navigate, Action<string> message) : WebChromeClient
{
    public override bool OnCreateWindow(WebView? view, bool isDialog, bool isUserGesture, Message? resultMsg)
    {
        if (resultMsg?.Obj is not WebView.WebViewTransport transport) return false;
        var popup = new WebView(context);
        popup.Settings.JavaScriptEnabled = true; popup.Settings.DomStorageEnabled = true;
        popup.Settings.AllowFileAccess = false; popup.Settings.AllowContentAccess = false; popup.Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        popup.SetWebViewClient(new PopupRedirectClient(platform, navigate, message));
        transport.WebView = popup; resultMsg.SendToTarget(); return true;
    }

    private sealed class PopupRedirectClient(string platform, Action<string> navigate, Action<string> message) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.IsForMainFrame != true) return !MobilePlatforms.FrameAllowed(platform, request?.Url?.ToString() ?? "");
            var address = request.Url?.ToString() ?? "";
            if (address == "about:blank") return false;
            if (MobilePlatforms.Allowed(platform, address)) navigate(address);
            else if (platform == "chaoxing" && ProbePolicy.TryGetHttpsNavigation(address, out var upgraded)) navigate(upgraded);
            else message("该登录弹窗跳转尚未适配，请返回官方登录后重试。");
            view?.StopLoading(); view?.Post(() => view.Destroy()); return true;
        }
        public override void OnReceivedSslError(WebView? view, SslErrorHandler? handler, Android.Net.Http.SslError? error) { handler?.Cancel(); }
    }
}
