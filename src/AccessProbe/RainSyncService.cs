using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class RainSyncService : IDisposable
{
    private readonly TaskStore store;
    private readonly string projectRoot;
    private readonly WebView2 browser;
    private readonly Func<HttpMessageHandler>? handlerFactory;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer loginTimer;
    private Task? initialization;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource<bool>? finished;
    private bool disposed;
    private bool clearing;
    private bool loginOpen;
    private bool loginPending;
    private bool awaitingLogin;
    private DateTimeOffset retryAfter;
    public RainPlatform Platform { get; }
    public bool IsRunning => cancellation is not null;
    public event Action<string, bool, bool>? StatusChanged;
    public event Action<RainSyncSummary>? Completed;

    public RainSyncService(TaskStore store, string root, RainPlatform platform, WebView2 sessionBrowser,
        Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? loginCheckInterval = null)
    {
        this.store = store; projectRoot = root; Platform = platform; browser = sessionBrowser;
        this.handlerFactory = handlerFactory;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        timer.Tick += (_, _) => StartAutomatic();
        loginTimer = new DispatcherTimer { Interval = loginCheckInterval ?? TimeSpan.FromSeconds(5) };
        loginTimer.Tick += (_, _) => CheckLogin();
    }

    public static string ProfilePath(string root, RainPlatform platform) => Path.Combine(root, ".local", "webview-profile-" + platform.Id);
    public string Host => store.LoadPlatformSettings(Platform.Id).Host;

    public void StartAutomatic()
    {
        if (disposed || clearing) return;
        var settings = store.LoadPlatformSettings(Platform.Id);
        if (!settings.Connected)
        {
            StatusChanged?.Invoke(Platform.Name + "尚未连接；点击“连接" + Platform.Name + "”完成官方登录后自动采集。", false, false);
            return;
        }
        if (!store.LoadSettings().AutomaticSyncEnabled || !settings.AutomaticSyncEnabled)
        {
            StatusChanged?.Invoke(Platform.Name + "自动记录已关闭，任务不会自动更新，请手动维护任务。", false, true);
            return;
        }
        timer.Start();
        _ = StartAsync(false);
    }

    public void SettingsChanged(bool enabled)
    {
        if (disposed) return;
        if (enabled) { retryAfter = default; StartAutomatic(); }
        else
        {
            timer.Stop(); Cancel();
            StatusChanged?.Invoke(Platform.Name + "自动记录已关闭，任务不会自动更新，请手动维护任务。", false, true);
        }
    }

    public void ConnectionOpened()
    {
        if (disposed) return;
        loginOpen = true; awaitingLogin = true;
        var settings = store.LoadPlatformSettings(Platform.Id);
        if (!settings.Connected) store.SavePlatformSettings(Platform.Id, settings with { Connected = true });
        timer.Start(); ScheduleLoginCheck();
    }

    public void ConnectionClosed() => loginOpen = false;

    public async Task SelectHostAsync(string host)
    {
        host = Platform.CheckHost(host);
        if (host == Host) return;
        if (disposed || clearing) return;
        // Keep the prior run tied to its selected host; wait before changing connection settings.
        var pending = finished?.Task;
        Cancel();
        if (pending is not null) await pending;
        if (disposed || clearing) return;
        var settings = store.LoadPlatformSettings(Platform.Id);
        store.SavePlatformSettings(Platform.Id, settings with
        { Host = host, LastSummary = null, LastSyncOutcome = "not_started", LastSyncAtUtc = null });
        retryAfter = default; awaitingLogin = true;
        if (loginOpen) ScheduleLoginCheck();
    }

    public void LoginNavigationCompleted(string address)
    {
        if (disposed || !awaitingLogin || !RainAddressPolicy.IsNavigation(Platform, address)
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || RainAddressPolicy.IsAuthentication(uri)) return;
        ScheduleLoginCheck();
    }

    private void ScheduleLoginCheck()
    {
        if (disposed || clearing || !store.LoadSettings().AutomaticSyncEnabled
            || !store.LoadPlatformSettings(Platform.Id).AutomaticSyncEnabled) return;
        loginPending = true; loginTimer.Start();
    }

    private void CheckLogin()
    {
        if (disposed || clearing || !loginPending || !store.LoadSettings().AutomaticSyncEnabled
            || !store.LoadPlatformSettings(Platform.Id).AutomaticSyncEnabled)
        { loginPending = false; loginTimer.Stop(); return; }
        if (IsRunning) return;
        loginPending = false; loginTimer.Stop();
        _ = StartAsync(false, afterLogin: true);
    }

    public async Task StartAsync(bool manual, bool afterLogin = false, bool catalogOnly = false)
    {
        if (disposed || clearing) return;
        if (IsRunning) { if (afterLogin) ScheduleLoginCheck(); return; }
        var settings = store.LoadPlatformSettings(Platform.Id);
        if (!settings.Connected)
        {
            StatusChanged?.Invoke("RAIN001：请先连接" + Platform.Name + "并完成官方登录。", false, true);
            return;
        }
        if (!manual && (!store.LoadSettings().AutomaticSyncEnabled || !settings.AutomaticSyncEnabled)) return;
        if (!manual && !afterLogin && DateTimeOffset.UtcNow < retryAfter) return;
        using var runCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        cancellation = runCancellation; finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            StatusChanged?.Invoke("正在后台同步" + Platform.Name + "…", true, false);
            await EnsureSessionAsync(runCancellation.Token);
            using var reader = new RainHttpReader(Platform, settings.Host, ReadCookiesAsync, handlerFactory?.Invoke());
            var progress = new Progress<string>(message =>
            {
                if (!disposed && !runCancellation.IsCancellationRequested) StatusChanged?.Invoke(message, true, false);
            });
            var summary = await new RainSynchronizer(store, reader, Platform, settings.Host).RunAsync(progress, runCancellation.Token, catalogOnly);
            if (disposed) return;
            if (catalogOnly)
            {
                StatusChanged?.Invoke(summary.LoginRequired ? "RAIN001：请在对应连接窗口完成官方登录后刷新课程。"
                    : summary.CourseListCovered ? $"{Platform.Name}课程列表已刷新，共 {summary.Courses} 门课程。"
                    : Platform.Name + "课程列表未完整刷新；原有课程选择保留，请检查官方登录与网络。", false,
                    summary.LoginRequired || !summary.CourseListCovered);
                return;
            }
            awaitingLogin = summary.LoginRequired;
            var outcome = summary.LoginRequired ? "login_required"
                : summary.Problems.Count > 0 || !summary.CourseListCovered || summary.CoursesRead != (summary.SelectedCourses ?? summary.Courses) ? "partial" : "completed";
            await Task.Run(() => store.RecordPlatformSyncResult(summary, outcome, DateTimeOffset.UtcNow, runCancellation.Token), runCancellation.Token);
            retryAfter = DateTimeOffset.UtcNow.AddMinutes(summary.LoginRequired ? 30 : 5);
            var message = summary.LoginRequired
                ? "RAIN001：" + Platform.Name + "需要登录；完成官方登录后自动采集，本地任务保留。"
                : $"{Platform.Name}已读取 {summary.CoursesRead}/{summary.SelectedCourses ?? summary.Courses} 门已选课程（发现 {summary.Courses} 门），采集 {summary.Tasks} 条任务；新增 {summary.Added}、更新 {summary.Updated}。"
                    + (summary.Problems.Count > 0 ? $"有 {summary.Problems.Count} 项未完成，见同步详情。" : "")
                    + (summary.StatusNeedsReview > 0 ? $"有 {summary.StatusNeedsReview} 条状态待核对。" : "");
            StatusChanged?.Invoke(message, false, outcome != "completed");
            Completed?.Invoke(summary);
            if (afterLogin && summary.LoginRequired && loginOpen) ScheduleLoginCheck();
        }
        catch (CourseRecordingChangedException)
        { if (!disposed) StatusChanged?.Invoke(Platform.Name + "记录课程已更新，本轮旧请求已停止；下次同步按新选择采集。", false, false); }
        catch (OperationCanceledException)
        {
            if (!disposed) StatusChanged?.Invoke(!store.LoadSettings().AutomaticSyncEnabled || !store.LoadPlatformSettings(Platform.Id).AutomaticSyncEnabled
                ? Platform.Name + "自动记录已关闭，任务不会自动更新，请手动维护任务。"
                : Platform.Name + "本轮同步已停止；已保存的任务保留。", false, true);
        }
        catch (TaskValidationException error)
        {
            if (!disposed) StatusChanged?.Invoke(error.Code + "：" + error.Message, false, true);
        }
        catch
        {
            if (!disposed) StatusChanged?.Invoke("RAIN002：" + Platform.Name + "同步未完成，请检查网络或 WebView2；本地任务保留。", false, true);
        }
        finally
        {
            cancellation = null; finished?.TrySetResult(true); finished = null;
            if (loginPending && !disposed && !clearing) loginTimer.Start();
        }
    }

    public async Task RefreshCourseCatalogAsync()
    {
        var pending = finished; Cancel();
        if (pending is not null) await pending.Task;
        await StartAsync(true, catalogOnly: true);
    }

    public async Task RecordingCoursesChangedAsync()
    {
        var pending = finished; Cancel();
        if (pending is not null) await pending.Task;
        await StartAsync(true);
    }

    public async Task<CoreWebView2Environment> EnsureSessionAsync(CancellationToken token = default)
    {
        if (disposed) throw new OperationCanceledException(token);
        initialization ??= InitializeAsync();
        try { await initialization.WaitAsync(token); }
        catch { if (initialization.IsFaulted) initialization = null; throw; }
        token.ThrowIfCancellationRequested();
        if (disposed) throw new OperationCanceledException(token);
        return browser.CoreWebView2.Environment;
    }

    private async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, ProfilePath(projectRoot, Platform));
        if (disposed) return;
        await browser.EnsureCoreWebView2Async(environment);
        if (disposed) return;
        ConfigureCore(browser.CoreWebView2);
    }

    internal static void ConfigureCore(CoreWebView2 core)
    {
        core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private async Task<IReadOnlyList<Cookie>> ReadCookiesAsync(Uri address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed || clearing) throw new OperationCanceledException(token);
        var observed = await browser.CoreWebView2.CookieManager.GetCookiesAsync(address.AbsoluteUri);
        token.ThrowIfCancellationRequested();
        var result = new List<Cookie>();
        foreach (var cookie in observed)
        {
            var value = new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
            { Secure = cookie.IsSecure, HttpOnly = cookie.IsHttpOnly };
            if (!cookie.IsSession) value.Expires = cookie.Expires.ToUniversalTime();
            result.Add(value);
        }
        observed.Clear(); return result;
    }

    public void Cancel()
    {
        loginPending = false; loginTimer.Stop(); cancellation?.Cancel();
    }

    public async Task ClearSessionAsync()
    {
        if (disposed || clearing) return;
        clearing = true; timer.Stop();
        var pending = finished?.Task; Cancel();
        StatusChanged?.Invoke("正在清除" + Platform.Name + "登录数据；本地任务保留。", false, true);
        try
        {
            if (pending is not null) await pending;
            if (disposed) return;
            await EnsureSessionAsync();
            browser.CoreWebView2.CookieManager.DeleteAllCookies();
            await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            retryAfter = default; awaitingLogin = true;
            StatusChanged?.Invoke("RAIN001：" + Platform.Name + "登录数据已清除；重新登录后可继续自动采集。", false, true);
        }
        finally { clearing = false; }
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); Cancel(); browser.Dispose();
    }
}
