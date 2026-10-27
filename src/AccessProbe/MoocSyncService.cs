using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class MoocSyncService : IDisposable
{
    private readonly TaskStore store;
    private readonly string projectRoot;
    private readonly WebView2 browser;
    private readonly Func<HttpMessageHandler>? handlerFactory;
    private readonly MoocSessionStore sessionStore;
    private readonly SemaphoreSlim cookieGate = new(1, 1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer loginTimer;
    private readonly TimeSpan accountTimeout;
    private Task? initialization;
    private Task<bool>? shutdown;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource<bool>? finished;
    private bool disposed, clearing, stopping, loginOpen, loginPending, sessionRejected;
    private long generation;
    private DateTimeOffset retryAfter;
    private sealed class SessionChangedException(string? context = null) : Exception(context);
    private sealed class AccountPageException(string outcome) : Exception(outcome)
    { public string Outcome { get; } = outcome; }
    private sealed class ReadScope(long generation)
    {
        public long Generation { get; } = generation;
        public string? Fingerprint { get; set; }
    }

    public bool IsRunning => cancellation is not null;
    public string SessionRecoveryOutcome { get; private set; } = "not_started";
    public string SessionPersistenceOutcome { get; private set; } = "not_saved";
    public event Action<string, bool, bool>? StatusChanged;
    public event Action<MoocSyncSummary>? Completed;

    public MoocSyncService(TaskStore store, string root, WebView2 sessionBrowser,
        Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? loginCheckInterval = null, TimeSpan? accountTimeout = null)
    {
        this.store = store; projectRoot = root; browser = sessionBrowser; this.handlerFactory = handlerFactory;
        sessionStore = new(Path.Combine(root, ".local", "sessions", "mooc.bin"));
        loginTimer = new() { Interval = loginCheckInterval ?? TimeSpan.FromSeconds(5) };
        this.accountTimeout = accountTimeout ?? TimeSpan.FromSeconds(30);
        timer.Tick += (_, _) => StartAutomatic(); loginTimer.Tick += (_, _) => CheckLogin();
    }
    public static string ProfilePath(string root) => Path.Combine(root, ".local", "webview-profile-mooc");

    public void StartAutomatic()
    {
        if (disposed || stopping || clearing) return;
        var settings = store.LoadMoocSettings();
        if (!settings.Connected) { StatusChanged?.Invoke("中国大学 MOOC 尚未连接；完成官方登录后自动采集到统一清单。", false, false); return; }
        if (!store.LoadSettings().AutomaticSyncEnabled || !settings.AutomaticSyncEnabled)
        { StatusChanged?.Invoke("中国大学 MOOC 自动同步已关闭，可点击“同步所有平台”手动更新。", false, true); return; }
        timer.Start(); _ = StartAsync(false);
    }

    public void SettingsChanged(bool enabled)
    {
        if (disposed || stopping) return;
        if (enabled) { retryAfter = default; StartAutomatic(); }
        else { timer.Stop(); Cancel(); StatusChanged?.Invoke("中国大学 MOOC 自动同步已关闭，可手动更新任务。", false, true); }
    }

    public void ConnectionOpened()
    {
        if (disposed || stopping || clearing) return;
        loginOpen = true; generation++; Cancel();
        var settings = store.LoadMoocSettings();
        if (!settings.Connected) store.SaveMoocSettings(settings with { Connected = true });
        timer.Start(); ScheduleLoginCheck();
    }
    public void ConnectionClosed() => loginOpen = false;
    public void LoginNavigationCompleted(string address)
    {
        if (MoocPlatform.IsOfficialNavigation(address) && address != "about:blank") ScheduleLoginCheck();
    }
    private void ScheduleLoginCheck()
    {
        if (disposed || stopping || clearing || !store.LoadSettings().AutomaticSyncEnabled || !store.LoadMoocSettings().AutomaticSyncEnabled) return;
        loginPending = true; loginTimer.Start();
    }
    private void CheckLogin()
    {
        if (disposed || stopping || clearing || !store.LoadSettings().AutomaticSyncEnabled || !store.LoadMoocSettings().AutomaticSyncEnabled)
        { loginPending = false; loginTimer.Stop(); return; }
        if (IsRunning || !loginPending) return;
        loginPending = false; loginTimer.Stop(); _ = StartAsync(false, true);
    }

    public async Task StartAsync(bool manual, bool afterLogin = false, bool catalogOnly = false)
    {
        if (disposed || stopping || clearing) return;
        if (IsRunning) { if (afterLogin) ScheduleLoginCheck(); return; }
        var settings = store.LoadMoocSettings();
        if (!settings.Connected) { StatusChanged?.Invoke("MOOC001：请先连接中国大学 MOOC 并完成官方登录。", false, true); return; }
        if (!manual && (!store.LoadSettings().AutomaticSyncEnabled || !settings.AutomaticSyncEnabled)) return;
        if (!manual && !afterLogin && DateTimeOffset.UtcNow < retryAfter) return;
        using var runCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        cancellation = runCancellation; var runFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); finished = runFinished;
        var scope = new ReadScope(generation);
        var stage = "session_initialization";
        try
        {
            StatusChanged?.Invoke("正在后台同步中国大学 MOOC…", true, false);
            await EnsureSessionAsync(runCancellation.Token);
            stage = "session_validation";
            await ValidateSessionAsync(scope, runCancellation.Token);
            stage = "account_navigation";
            var identity = await ReadAccountAsync(runCancellation.Token);
            stage = "account_session_validation";
            await ValidateSessionAsync(scope, runCancellation.Token);
            MoocSyncSummary summary;
            if (identity.Outcome != "ok")
                summary = new(0, 0, 0, 0, 0, identity.Outcome == "login_required", false,
                    [new(identity.Outcome == "login_required" ? "MOOC001" : "MOOC003", "", "账号检查：" + (identity.Outcome == "login_required"
                        ? "请在连接窗口完成官方登录；本地已有任务保留。" : "官方账号身份结构暂未识别，未写入任务。"))]);
            else
            {
                await ValidateSessionAsync(scope, runCancellation.Token);
                stage = "course_task_metadata";
                using var reader = CreateReader(scope);
                var progress = new Progress<string>(message =>
                { if (!disposed && !runCancellation.IsCancellationRequested) StatusChanged?.Invoke(message, true, false); });
                summary = await new MoocSynchronizer(store, reader, identity.AccountId!, token => ValidateSessionAsync(scope, token)).RunAsync(progress, runCancellation.Token, catalogOnly);
            }
            stage = "session_persistence";
            if (!await FinishSessionAsync(scope, summary.LoginRequired, runCancellation.Token))
                summary = summary with { Problems = summary.Problems.Concat([new MoocSyncProblem("MOOC004", "", "登录状态未能保存，重启后可能需要重新登录。")]).ToArray() };
            CheckScope(scope, runCancellation.Token);
            if (catalogOnly)
            {
                StatusChanged?.Invoke(summary.LoginRequired ? "MOOC001：请在连接 MOOC 窗口完成官方登录后刷新课程。"
                    : summary.CourseListCovered ? $"中国大学 MOOC 课程列表已刷新，共 {summary.Courses} 门课程。"
                    : "中国大学 MOOC 课程列表未完整刷新；原有课程选择保留，请查看官方登录与网络。", false,
                    summary.LoginRequired || !summary.CourseListCovered || summary.Problems.Count > 0);
                return;
            }
            var outcome = summary.LoginRequired ? "login_required" : summary.Problems.Count > 0 || !summary.CourseListCovered || summary.CoursesRead != (summary.SelectedCourses ?? summary.Courses) ? "partial" : "completed";
            stage = "result_persistence";
            store.RecordMoocSyncResult(summary, outcome, DateTimeOffset.UtcNow, runCancellation.Token);
            retryAfter = DateTimeOffset.UtcNow.AddMinutes(summary.LoginRequired ? 30 : 5);
            var message = summary.LoginRequired ? "MOOC001：中国大学 MOOC 需要登录；完成官方登录后自动采集，已有任务保留。"
                : $"中国大学 MOOC 已读取 {summary.CoursesRead}/{summary.SelectedCourses ?? summary.Courses} 门已选课程（发现 {summary.Courses} 门），采集 {summary.Tasks} 条任务；新增 {summary.Added}、更新 {summary.Updated}。"
                    + (summary.Problems.Count > 0 ? $"有 {summary.Problems.Count} 项需处理，见同步详情。" : "")
                    + (summary.StatusNeedsReview > 0 ? $"有 {summary.StatusNeedsReview} 条状态待核对。" : "");
            StatusChanged?.Invoke(message, false, outcome != "completed"); Completed?.Invoke(summary);
            if (afterLogin && summary.LoginRequired && loginOpen) ScheduleLoginCheck();
        }
        catch (SessionChangedException)
        {
            if (!disposed && !stopping) StatusChanged?.Invoke("MOOC003：登录会话在同步期间更新，已停止旧请求；请重新同步，已保存任务保留。", false, true);
            if (loginOpen) ScheduleLoginCheck();
        }
        catch (CourseRecordingChangedException)
        { if (!disposed && !stopping) StatusChanged?.Invoke("中国大学 MOOC 记录课程已更新，本轮旧请求已停止；下次同步按新选择采集。", false, false); }
        catch (OperationCanceledException)
        { if (!disposed && !stopping) StatusChanged?.Invoke("中国大学 MOOC 本轮同步已停止；已有任务保留。", false, true); }
        catch (TaskValidationException error)
        { if (!disposed && !stopping) StatusChanged?.Invoke(error.Code + "：" + error.Message, false, true); }
        catch (Exception error)
        {
            if (!disposed && !stopping && !clearing)
            {
                var detail = error is AccountPageException accountError ? accountError.Outcome : error.GetType().Name;
                var context = stage switch
                {
                    "session_initialization" => "浏览器登录环境初始化",
                    "session_validation" or "account_session_validation" => "登录会话检查",
                    "account_navigation" => "官方个人中心读取",
                    "course_task_metadata" => "课程与任务读取",
                    "session_persistence" => "登录状态保存",
                    _ => "同步结果保存"
                };
                var message = "MOOC002：" + context + "未完成（" + detail + "）；已有任务保留，可稍后重新同步。";
                var summary = new MoocSyncSummary(0, 0, 0, 0, 0, false, false, [new("MOOC002", "", message)]);
                try
                {
                    CheckScope(scope, runCancellation.Token);
                    if (!catalogOnly)
                    {
                        store.RecordMoocSyncResult(summary, "error", DateTimeOffset.UtcNow, runCancellation.Token);
                        Completed?.Invoke(summary);
                    }
                }
                catch (Exception persistenceError) when (persistenceError is SessionChangedException or OperationCanceledException
                    or TaskValidationException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException) { }
                retryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
                StatusChanged?.Invoke(message, false, true);
                if (afterLogin && loginOpen) ScheduleLoginCheck();
            }
        }
        finally
        {
            cancellation = null; runFinished.TrySetResult(true); finished = null;
            if (loginPending && !disposed && !stopping && !clearing) loginTimer.Start();
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
        catch { if (initialization.IsFaulted || initialization.IsCanceled) initialization = null; throw; }
        token.ThrowIfCancellationRequested(); if (disposed) throw new OperationCanceledException(token);
        return browser.CoreWebView2.Environment;
    }
    private async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, ProfilePath(projectRoot));
        if (disposed) return;
        await browser.EnsureCoreWebView2Async(environment); if (disposed) return;
        RainSyncService.ConfigureCore(browser.CoreWebView2);
        browser.CoreWebView2.NavigationStarting += (_, args) =>
        { if (args.Uri != "about:blank" && (!MoocPlatform.IsOfficialNavigation(args.Uri) || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/home.htm")) args.Cancel = true; };
        browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
        await cookieGate.WaitAsync();
        try
        {
            if (disposed || clearing || stopping) return;
            var current = await ReadProfileAsync(); var saved = sessionStore.Load(DateTimeOffset.UtcNow);
            var missing = MoocSessionStore.MissingForRestore(saved, current);
            foreach (var cookie in missing) ApplyCookie(cookie);
            SessionRecoveryOutcome = missing.Count > 0 ? "restored" : saved.Count > 0 ? "profile_preferred" : sessionStore.LastLoadOutcome;
        }
        finally { cookieGate.Release(); }
    }

    private async Task<(string Outcome, string? AccountId)> ReadAccountAsync(CancellationToken token)
    {
        var core = browser.CoreWebView2;
        // The official home script starts /home.htm?userId=... while the first navigation is still loading.
        // Track the newest document and read confirmed identity before optional scripts finish blocking its DOM.
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong latestNavigation = 0, contentNavigation = 0, domNavigation = 0;
        string? latestAddress = null;
        var documentMarker = Guid.NewGuid().ToString("N");
        bool loginRequired = false;
        AccountPageException? failure = null;
        void Notify()
        {
            var pending = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); pending.TrySetResult(true);
        }
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && target.Scheme == "https"
                && target.IdnHost == MoocPlatform.Host && target.AbsolutePath == "/home.htm")
            { latestNavigation = args.NavigationId; latestAddress = args.Uri; contentNavigation = domNavigation = 0; }
            else if (args.Uri != "about:blank")
            {
                if (MoocPlatform.IsLoginNavigation(args.Uri) && Uri.TryCreate(args.Uri, UriKind.Absolute, out var login)
                    && (login.IdnHost != MoocPlatform.Host || login.AbsolutePath is "/" or "/login.htm"
                        || login.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase))) loginRequired = true;
                else failure = new("unsupported_redirect");
            }
            Notify();
        }
        void DomReady(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
        { if (args.NavigationId == latestNavigation) { domNavigation = args.NavigationId; Notify(); } }
        void ContentReady(object? sender, CoreWebView2ContentLoadingEventArgs args)
        { if (args.NavigationId == latestNavigation) { contentNavigation = args.NavigationId; Notify(); } }
        void Navigation(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId != latestNavigation) return;
            if (args.IsSuccess) domNavigation = args.NavigationId;
            else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.ConnectionAborted && !loginRequired && failure is null)
                failure = new("navigation_" + args.WebErrorStatus);
            Notify();
        }
        core.NavigationStarting += Starting; core.ContentLoading += ContentReady;
        core.DOMContentLoaded += DomReady; core.NavigationCompleted += Navigation;
        string? markerScript = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(accountTimeout);
            markerScript = await core.AddScriptToExecuteOnDocumentCreatedAsync("Object.defineProperty(window,'__taskAggregatorMoocDocument',"
                + "{value:" + JsonSerializer.Serialize(documentMarker) + ",writable:false,configurable:false});");
            timeout.Token.ThrowIfCancellationRequested();
            core.Navigate(MoocPlatform.Home.AbsoluteUri);
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (failure is not null) throw failure;
                    if (loginRequired) return ("login_required", null);
                    if (latestNavigation == 0 || contentNavigation != latestNavigation && domNavigation != latestNavigation)
                    { await changed.Task.WaitAsync(timeout.Token); continue; }
                    var snapshot = latestNavigation;
                    await Task.Delay(100, timeout.Token);
                    if (snapshot != latestNavigation || failure is not null || loginRequired) continue;
                    if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var address) || address.IdnHost != MoocPlatform.Host || address.AbsolutePath != "/home.htm")
                        return ("account_unrecognized", null);
                    var result = await core.ExecuteScriptAsync("(() => { const user = window.webUser; return { marker: window.__taskAggregatorMoocDocument, address: location.href, host: location.hostname, path: location.pathname, readyState: document.readyState, loggedIn: !!user, id: user ? String(user.id || '') : '' }; })()").WaitAsync(timeout.Token);
                    if (snapshot != latestNavigation || failure is not null || loginRequired) continue;
                    using var document = JsonDocument.Parse(result); var root = document.RootElement;
                    if (MoocParser.Text(MoocParser.Get(root, "marker")) != documentMarker
                        || MoocParser.Text(MoocParser.Get(root, "address")) != latestAddress) continue;
                    if (MoocParser.Text(MoocParser.Get(root, "host")) != MoocPlatform.Host || MoocParser.Text(MoocParser.Get(root, "path")) != "/home.htm") continue;
                    var domReady = domNavigation == latestNavigation || MoocParser.Text(MoocParser.Get(root, "readyState")) is "interactive" or "complete";
                    if (MoocParser.Get(root, "loggedIn").ValueKind == JsonValueKind.False)
                    {
                        if (domReady) return ("login_required", null);
                        await Task.Delay(150, timeout.Token); continue;
                    }
                    var id = MoocParser.Text(MoocParser.Get(root, "id"));
                    if (MoocPlatform.IsId(id)) return ("ok", id);
                    if (domReady) return ("account_unrecognized", null);
                    await Task.Delay(150, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new AccountPageException("navigation_timeout"); }
        }
        finally
        {
            core.NavigationStarting -= Starting; core.ContentLoading -= ContentReady;
            core.DOMContentLoaded -= DomReady; core.NavigationCompleted -= Navigation;
            if (markerScript is not null) core.RemoveScriptToExecuteOnDocumentCreated(markerScript);
            core.Stop();
        }
    }

    private MoocHttpReader CreateReader(ReadScope scope) => new(async (address, token) =>
    {
        await cookieGate.WaitAsync(token);
        try
        {
            CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current);
            return current.Where(cookie => cookie.AppliesTo(address)).ToArray();
        }
        finally { cookieGate.Release(); }
    }, handlerFactory?.Invoke(), async (_, observed, renewed, token) =>
    {
        await cookieGate.WaitAsync(token);
        try
        {
            CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current);
            if (renewed.Count == 0) return;
            var expected = current.ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
            foreach (var cookie in renewed)
            { ApplyCookie(cookie); if (cookie.IsExpired(DateTimeOffset.UtcNow)) expected.Remove(cookie.Key); else expected[cookie.Key] = cookie; }
            // CookieManager updates are sent to another process; reads can briefly return the prior value.
            // Wait only for this response's known before/after values, never overwrite a newer login.
            var desired = Fingerprint(expected.Values); var previous = current.ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var updated = await ReadProfileAsync(); CheckScope(scope, token);
                if (Fingerprint(updated) == desired) { scope.Fingerprint = desired; SaveProfile(updated); return; }
                if (updated.Any(cookie => MoocSessionStore.IsAccountEvidence(cookie) && cookie.Value != previous.GetValueOrDefault(cookie.Key)?.Value
                    && cookie.Value != expected.GetValueOrDefault(cookie.Key)?.Value)) throw new SessionChangedException("profile_changed");
                await Task.Delay(25, token);
            }
            throw new SessionChangedException("cookie_writeback_pending");
        }
        finally { cookieGate.Release(); }
    });

    private async Task ValidateSessionAsync(ReadScope scope, CancellationToken token)
    {
        await cookieGate.WaitAsync(token);
        try { CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current); }
        finally { cookieGate.Release(); }
    }
    private void CheckScope(ReadScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed || stopping || clearing || scope.Generation != generation) throw new SessionChangedException();
    }
    private static void CheckFingerprint(ReadScope scope, IEnumerable<MoocSessionCookie> cookies)
    {
        var current = Fingerprint(cookies);
        if (scope.Fingerprint is not null && scope.Fingerprint != current) throw new SessionChangedException("profile_changed");
        scope.Fingerprint = current;
    }
    private static string Fingerprint(IEnumerable<MoocSessionCookie> cookies)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cookies.Where(MoocSessionStore.IsAccountEvidence).OrderBy(cookie => cookie.Key, StringComparer.Ordinal).Select(cookie => new[] { cookie.Key, cookie.Value }));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private async Task<MoocSessionCookie[]> ReadProfileAsync()
    {
        var observed = await browser.CoreWebView2.CookieManager.GetCookiesAsync(""); var result = new List<MoocSessionCookie>();
        foreach (var cookie in observed)
        {
            var value = new MoocSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly,
                cookie.IsSession ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()), cookie.SameSite.ToString());
            if (MoocSessionStore.IsValid(value) && !value.IsExpired(DateTimeOffset.UtcNow)) result.Add(value);
        }
        observed.Clear(); return result.DistinctBy(cookie => cookie.Key).ToArray();
    }
    private void ApplyCookie(MoocSessionCookie cookie)
    {
        var manager = browser.CoreWebView2.CookieManager; var value = manager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        if (cookie.IsExpired(DateTimeOffset.UtcNow)) { manager.DeleteCookie(value); return; }
        value.IsSecure = cookie.Secure; value.IsHttpOnly = cookie.HttpOnly; value.Expires = cookie.ExpiresUtc?.UtcDateTime ?? DateTime.MinValue;
        if (Enum.TryParse<CoreWebView2CookieSameSiteKind>(cookie.SameSite, out var sameSite)) value.SameSite = sameSite;
        manager.AddOrUpdateCookie(value);
    }
    private async Task<bool> FinishSessionAsync(ReadScope scope, bool loginRequired, CancellationToken token)
    {
        await cookieGate.WaitAsync(token);
        try
        {
            CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current);
            if (loginRequired) { sessionRejected = true; return ClearArchive(); }
            sessionRejected = false; return SaveProfile(current);
        }
        finally { cookieGate.Release(); }
    }
    private bool SaveProfile(IReadOnlyList<MoocSessionCookie> current)
    {
        if (sessionRejected) return ClearArchive();
        try { sessionStore.Save(current, DateTimeOffset.UtcNow); SessionPersistenceOutcome = current.Count > 0 ? "saved" : "empty"; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { SessionPersistenceOutcome = "unavailable"; return false; }
    }
    private bool ClearArchive()
    {
        try { sessionStore.Clear(); SessionPersistenceOutcome = "cleared"; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { SessionPersistenceOutcome = "clear_failed"; return false; }
    }

    public void Cancel() { loginPending = false; loginTimer.Stop(); cancellation?.Cancel(); }
    public async Task ClearSessionAsync()
    {
        if (disposed || stopping || clearing) return;
        clearing = true; generation++; timer.Stop(); var pending = finished?.Task; Cancel();
        StatusChanged?.Invoke("正在清除中国大学 MOOC 登录数据，已有任务保留。", false, true);
        try
        {
            if (pending is not null) await pending;
            await EnsureSessionAsync(); await cookieGate.WaitAsync();
            try
            {
                sessionRejected = true; sessionStore.Clear(); SessionPersistenceOutcome = "cleared";
                browser.CoreWebView2.Stop(); browser.CoreWebView2.CookieManager.DeleteAllCookies();
                await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            }
            finally { cookieGate.Release(); }
            retryAfter = default; StatusChanged?.Invoke("MOOC001：登录数据已清除，请在官方页面重新登录。", false, true);
        }
        finally { clearing = false; }
    }
    public Task<bool> SaveForExitAsync() => shutdown ??= SaveForExitCoreAsync();
    private async Task<bool> SaveForExitCoreAsync()
    {
        if (disposed) return true;
        stopping = true; generation++; timer.Stop(); var pending = finished?.Task; Cancel();
        try
        {
            if (pending is not null) await pending;
            if (initialization is null) return true; await initialization;
            if (browser.CoreWebView2 is null) return true;
            await cookieGate.WaitAsync();
            try { return clearing ? ClearArchive() : SaveProfile(await ReadProfileAsync()); }
            finally { cookieGate.Release(); }
        }
        catch { SessionPersistenceOutcome = "unavailable"; return false; }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; generation++; timer.Stop(); Cancel(); browser.Dispose();
    }
}
