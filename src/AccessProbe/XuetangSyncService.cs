using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class XuetangSyncService : IDisposable
{
    private readonly TaskStore store;
    private readonly string projectRoot;
    private readonly WebView2 browser;
    private readonly Func<HttpMessageHandler>? handlerFactory;
    private readonly XuetangSessionStore sessionStore;
    private readonly SemaphoreSlim cookieGate = new(1, 1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer loginTimer;
    private Task? initialization;
    private Task<bool>? shutdown;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource<bool>? finished;
    private bool disposed, clearing, stopping, loginOpen, loginPending, sessionRejected;
    private long generation;
    private DateTimeOffset retryAfter;
    private sealed class SessionChangedException(string? context = null) : Exception(context);
    private sealed class ReadScope(long generation)
    {
        public long Generation { get; } = generation;
        public string? Fingerprint { get; set; }
    }

    public bool IsRunning => cancellation is not null;
    public string SessionRecoveryOutcome { get; private set; } = "not_started";
    public string SessionPersistenceOutcome { get; private set; } = "not_saved";
    public XuetangLoginDiagnostics LoginDiagnostics { get; }
    public event Action<string, bool, bool>? StatusChanged;
    public event Action<XuetangSyncSummary>? Completed;
    public event Action? CourseSelectionRequired;
    public event Action<XuetangSyncSummary>? CatalogCompleted;

    public XuetangSyncService(TaskStore store, string root, WebView2 sessionBrowser,
        Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? loginCheckInterval = null)
    {
        this.store = store; projectRoot = root; browser = sessionBrowser; this.handlerFactory = handlerFactory;
        sessionStore = new(Path.Combine(root, ".local", "sessions", "xuetang.bin"), store);
        LoginDiagnostics = new(Path.Combine(root, ".local", "diagnostics", "xuetang-login.json"));
        sessionRejected = store.LoadPlatformSessionInfo("xuetang").State == "expired";
        loginTimer = new() { Interval = loginCheckInterval ?? TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => StartAutomatic(); loginTimer.Tick += (_, _) => CheckLogin();
    }
    public static string ProfilePath(string root) => Path.Combine(root, ".local", "webview-profile-xuetang");

    public void StartAutomatic()
    {
        if (disposed || stopping || clearing) return;
        var settings = store.LoadXuetangSettings();
        if (!settings.Connected) { StatusChanged?.Invoke("学堂在线尚未连接；完成官方登录后返回课程列表，保存选择后采集任务。", false, false); return; }
        if (!store.LoadSettings().AutomaticSyncEnabled || !settings.AutomaticSyncEnabled)
        { StatusChanged?.Invoke("学堂在线 自动同步已关闭，可点击“同步所有平台”手动更新。", false, true); return; }
        timer.Start(); _ = StartAsync(false);
    }

    public void SettingsChanged(bool enabled)
    {
        if (disposed || stopping) return;
        if (enabled) { retryAfter = default; StartAutomatic(); }
        else { timer.Stop(); Cancel(); StatusChanged?.Invoke("学堂在线 自动同步已关闭，可手动更新任务。", false, true); }
    }

    public void ConnectionOpened()
    {
        if (disposed || stopping || clearing) return;
        loginOpen = true; generation++; Cancel();
        var settings = store.LoadXuetangSettings();
        if (!settings.Connected) store.SaveXuetangSettings(settings with { Connected = true });
        timer.Start(); ScheduleLoginCheck();
    }
    public void ConnectionClosed() => loginOpen = false;
    public void LoginNavigationCompleted(string address)
    {
        if (XuetangPlatform.IsOfficialNavigation(address) && address != "about:blank") ScheduleLoginCheck();
    }
    private void ScheduleLoginCheck()
    {
        if (disposed || stopping || clearing || !loginOpen && (!store.LoadSettings().AutomaticSyncEnabled || !store.LoadXuetangSettings().AutomaticSyncEnabled)) return;
        loginPending = true; loginTimer.Start();
    }
    private void CheckLogin()
    {
        if (disposed || stopping || clearing || !loginOpen && (!store.LoadSettings().AutomaticSyncEnabled || !store.LoadXuetangSettings().AutomaticSyncEnabled))
        { loginPending = false; loginTimer.Stop(); return; }
        if (IsRunning || !loginPending) return;
        loginPending = false; loginTimer.Stop(); _ = StartAsync(loginOpen, true);
    }

    public async Task StartAsync(bool manual, bool afterLogin = false, bool catalogOnly = false)
    {
        if (disposed || stopping || clearing) return;
        if (IsRunning) { if (afterLogin) ScheduleLoginCheck(); return; }
        var settings = store.LoadXuetangSettings();
        if (!settings.Connected) { StatusChanged?.Invoke("XT001：请先连接学堂在线 并完成官方登录。", false, true); return; }
        if (!manual && (!store.LoadSettings().AutomaticSyncEnabled || !settings.AutomaticSyncEnabled)) return;
        if (!manual && !afterLogin && DateTimeOffset.UtcNow < retryAfter) return;
        var needsSelection = !store.LoadCourseRecording("xuetang").SelectionConfirmed;
        catalogOnly |= needsSelection || afterLogin && loginOpen;
        using var runCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        cancellation = runCancellation; var runFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); finished = runFinished;
        var scope = new ReadScope(generation);
        var stage = "session_initialization";
        try
        {
            StatusChanged?.Invoke(catalogOnly ? "正在使用当前登录读取学堂在线课程…" : "正在同步学堂在线已选课程…", true, false);
            await EnsureSessionAsync(runCancellation.Token);
            stage = "session_validation";
            await ValidateSessionAsync(scope, runCancellation.Token);
            stage = "course_task_metadata";
            using var reader = CreateReader(scope);
            var progress = new Progress<string>(message =>
            { if (!disposed && !runCancellation.IsCancellationRequested) StatusChanged?.Invoke(message, true, false); });
            var summary = await new XuetangSynchronizer(store, reader, token => ValidateSessionAsync(scope, token))
                .RunAsync(progress, runCancellation.Token, catalogOnly);
            stage = "session_persistence";
            if (!await FinishSessionAsync(scope, summary.LoginRequired, runCancellation.Token))
                summary = summary with { Problems = summary.Problems.Concat([new XuetangSyncProblem("XT004", "", "登录状态未能保存，重启后可能需要重新登录。")]).ToArray() };
            CheckScope(scope, runCancellation.Token);
            if (catalogOnly)
            {
                var codes = summary.Problems.Select(problem => problem.Code).Distinct(StringComparer.Ordinal).Take(16).ToArray();
                var catalogOutcome = CourseRecordingRules.CatalogRefreshOutcome(summary.CourseListCovered, summary.LoginRequired, codes);
                store.RecordCourseCatalogResult("xuetang", catalogOutcome, codes, DateTimeOffset.UtcNow, runCancellation.Token);
                retryAfter = DateTimeOffset.UtcNow.AddMinutes(summary.LoginRequired ? 30 : 5);
                StatusChanged?.Invoke(summary.LoginRequired ? "XT001：请在连接学堂在线窗口完成官方登录后刷新课程。"
                    : catalogOutcome == "completed" ? $"学堂在线 课程列表已刷新，共 {summary.Courses} 门课程。"
                    : "学堂在线 课程列表未完整刷新；原有课程选择保留，请查看官方登录与网络。", false,
                    catalogOutcome != "completed");
                if (!summary.LoginRequired && summary.CourseListCovered && (needsSelection || afterLogin && loginOpen)) CourseSelectionRequired?.Invoke();
                CatalogCompleted?.Invoke(summary);
                if (afterLogin && summary.LoginRequired && loginOpen) ScheduleLoginCheck();
                return;
            }
            var outcome = summary.LoginRequired ? "login_required" : summary.Problems.Count > 0 || !summary.CourseListCovered || summary.CoursesRead != summary.SelectedCourses ? "partial" : "completed";
            stage = "result_persistence";
            store.RecordXuetangSyncResult(summary, outcome, DateTimeOffset.UtcNow, runCancellation.Token);
            retryAfter = DateTimeOffset.UtcNow.AddMinutes(summary.LoginRequired ? 30 : 5);
            var message = summary.LoginRequired ? "XT001：学堂在线需要登录；请在平台管理窗口重新登录，再保存课程选择并采集，已有任务保留。"
                : $"学堂在线 已读取 {summary.CoursesRead}/{summary.SelectedCourses} 门已选课程（发现 {summary.Courses} 门），采集 {summary.Tasks} 条任务；新增 {summary.Added}、更新 {summary.Updated}。"
                    + (summary.Problems.Count > 0 ? $"有 {summary.Problems.Count} 项需处理，见同步详情。" : "")
                    + (summary.StatusNeedsReview > 0 ? $"有 {summary.StatusNeedsReview} 条状态待核对。" : "")
                    + "仅覆盖新版主站普通课程，完整范围见同步详情。";
            StatusChanged?.Invoke(message, false, outcome != "completed"); Completed?.Invoke(summary);
            if (afterLogin && summary.LoginRequired && loginOpen) ScheduleLoginCheck();
        }
        catch (SessionChangedException)
        {
            if (!disposed && !stopping) StatusChanged?.Invoke("XT003：登录会话在同步期间更新，已停止旧请求；请重新同步，已保存任务保留。", false, true);
            if (loginOpen) ScheduleLoginCheck();
        }
        catch (CourseRecordingChangedException)
        { if (!disposed && !stopping) StatusChanged?.Invoke("学堂在线 记录课程已更新，本轮旧请求已停止；下次同步按新选择采集。", false, false); }
        catch (OperationCanceledException)
        { if (!disposed && !stopping) StatusChanged?.Invoke("学堂在线 本轮同步已停止；已有任务保留。", false, true); }
        catch (TaskValidationException error)
        {
            if (catalogOnly && !disposed && !stopping && !clearing) RecordCatalogFailure(error.Code, runCancellation.Token);
            if (!disposed && !stopping) StatusChanged?.Invoke(error.Code + "：" + error.Message, false, true);
        }
        catch (Exception error)
        {
            if (!disposed && !stopping && !clearing)
            {
                var detail = error.GetType().Name;
                var context = stage switch
                {
                    "session_initialization" => "浏览器登录环境初始化",
                    "session_validation" or "account_session_validation" => "登录会话检查",
                    "course_task_metadata" => "课程与任务读取",
                    "session_persistence" => "登录状态保存",
                    _ => "同步结果保存"
                };
                var message = "XT002：" + context + "未完成（" + detail + "）；已有任务保留，可稍后重新同步。";
                var summary = new XuetangSyncSummary(0, 0, 0, 0, 0, false, false, [new("XT002", "", message)]);
                try
                {
                    CheckScope(scope, runCancellation.Token);
                    if (!catalogOnly)
                    {
                        store.RecordXuetangSyncResult(summary, "error", DateTimeOffset.UtcNow, runCancellation.Token);
                        Completed?.Invoke(summary);
                    }
                    else store.RecordCourseCatalogResult("xuetang", "error", ["XT002"], DateTimeOffset.UtcNow, runCancellation.Token);
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

    private void RecordCatalogFailure(string code, CancellationToken token)
    {
        try { store.RecordCourseCatalogResult("xuetang", "error", [code], DateTimeOffset.UtcNow, token); }
        catch (Exception error) when (error is OperationCanceledException or TaskValidationException
            or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException) { }
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
        { if (args.Uri != "about:blank") args.Cancel = true; };
        browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
        await cookieGate.WaitAsync();
        try
        {
            if (disposed || clearing || stopping) return;
            var current = await ReadProfileAsync(); var saved = sessionStore.Load(DateTimeOffset.UtcNow);
            var missing = XuetangSessionStore.MissingForRestore(saved, current);
            foreach (var cookie in missing) ApplyCookie(cookie);
            if (missing.Count > 0)
            {
                var expected = current.Concat(missing).DistinctBy(cookie => cookie.Key).ToArray();
                var desired = Fingerprint(expected);
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    if (disposed || clearing || stopping) return;
                    var restored = await ReadProfileAsync();
                    if (Fingerprint(restored) == desired) break;
                    if (restored.Any(cookie => XuetangSessionStore.IsAccountEvidence(cookie)
                        && !expected.Any(value => value.Key == cookie.Key && value.Value == cookie.Value)))
                        throw new SessionChangedException("restore_login_changed");
                    if (attempt == 39) throw new SessionChangedException("restore_pending");
                    await Task.Delay(25);
                }
            }
            SessionRecoveryOutcome = missing.Count > 0 ? "restored" : saved.Count > 0 ? "profile_preferred" : sessionStore.LastLoadOutcome;
        }
        finally { cookieGate.Release(); }
    }

    private XuetangHttpReader CreateReader(ReadScope scope) => new(async (address, token) =>
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
                if (updated.Any(cookie => XuetangSessionStore.IsAccountEvidence(cookie) && cookie.Value != previous.GetValueOrDefault(cookie.Key)?.Value
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
    private static void CheckFingerprint(ReadScope scope, IEnumerable<XuetangSessionCookie> cookies)
    {
        var current = Fingerprint(cookies);
        if (scope.Fingerprint is not null && scope.Fingerprint != current) throw new SessionChangedException("profile_changed");
        scope.Fingerprint = current;
    }
    private static string Fingerprint(IEnumerable<XuetangSessionCookie> cookies)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cookies.Where(XuetangSessionStore.IsAccountEvidence).OrderBy(cookie => cookie.Key, StringComparer.Ordinal).Select(cookie => new[] { cookie.Key, cookie.Value }));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private async Task<XuetangSessionCookie[]> ReadProfileAsync()
    {
        var observed = await browser.CoreWebView2.CookieManager.GetCookiesAsync(""); var result = new List<XuetangSessionCookie>();
        foreach (var cookie in observed)
        {
            var value = new XuetangSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly,
                cookie.IsSession ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()), cookie.SameSite.ToString());
            if (XuetangSessionStore.IsValid(value) && !value.IsExpired(DateTimeOffset.UtcNow)) result.Add(value);
        }
        observed.Clear(); return result.DistinctBy(cookie => cookie.Key).ToArray();
    }
    private void ApplyCookie(XuetangSessionCookie cookie)
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
            if (loginRequired) { sessionRejected = true; sessionStore.Invalidate(DateTimeOffset.UtcNow); SessionPersistenceOutcome = "expired"; return true; }
            sessionRejected = false; var saved = SaveProfile(current);
            if (saved) store.ConfirmPlatformSession("xuetang", DateTimeOffset.UtcNow);
            return saved;
        }
        finally { cookieGate.Release(); }
    }
    private bool SaveProfile(IReadOnlyList<XuetangSessionCookie> current)
    {
        if (sessionRejected) return true;
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
        StatusChanged?.Invoke("正在清除学堂在线 登录数据，已有任务保留。", false, true);
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
            retryAfter = default; StatusChanged?.Invoke("XT001：登录数据已清除，请在官方页面重新登录。", false, true);
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
