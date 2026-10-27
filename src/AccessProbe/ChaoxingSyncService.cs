using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class ChaoxingSyncService : IDisposable
{
    private readonly TaskStore store;
    private readonly string projectRoot;
    private readonly WebView2 browser;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer loginTimer;
    private readonly Func<HttpMessageHandler>? handlerFactory;
    private readonly ChaoxingSessionStore sessionStore;
    private readonly SemaphoreSlim cookieGate = new(1, 1);
    private Task? initialization;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource<bool>? finished;
    private Task<bool>? shutdown;
    private bool disposed;
    private bool stopping;
    private bool loginPending;
    private bool awaitingLogin;
    private bool loginWindowOpen;
    private bool sessionClearing;
    private DateTimeOffset retryAfter;
    private long sessionGeneration;
    private bool sessionRejected;
    private sealed class ReadScope(long generation)
    {
        public long Generation { get; } = generation;
        public string? Fingerprint { get; set; }
    }
    private sealed class SessionChangedException : Exception;
    public bool IsRunning => cancellation is not null;
    public string SessionRecoveryOutcome { get; private set; } = "not_started";
    public string SessionPersistenceOutcome { get; private set; } = "not_saved";
    public event Action<string, bool, bool>? StatusChanged;
    public event Action<ChaoxingSyncSummary>? Completed;

    public ChaoxingSyncService(TaskStore store, string root, WebView2 sessionBrowser,
        Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? loginCheckInterval = null)
    {
        this.store = store; projectRoot = root; browser = sessionBrowser; this.handlerFactory = handlerFactory;
        sessionStore = new ChaoxingSessionStore(Path.Combine(root, ".local", "sessions", "chaoxing.bin"));
        timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        timer.Tick += (_, _) => StartAutomatic();
        loginTimer = new DispatcherTimer { Interval = loginCheckInterval ?? TimeSpan.FromSeconds(5) };
        loginTimer.Tick += (_, _) => CheckLoginSession();
    }

    public void StartAutomatic()
    {
        if (disposed || stopping || sessionClearing) return;
        var settings = store.LoadSettings();
        if (!settings.AutomaticSyncEnabled)
        { StatusChanged?.Invoke("您已关闭自动记录，任务不会自动更新，请手动维护任务。", false, true); return; }
        timer.Start();
        _ = StartAsync(false);
    }

    public void SettingsChanged(bool enabled)
    {
        if (disposed || stopping) return;
        if (enabled) { retryAfter = default; StartAutomatic(); }
        else
        {
            timer.Stop(); Cancel();
            StatusChanged?.Invoke("您已关闭自动记录，任务不会自动更新，请手动维护任务。", false, true);
        }
    }

    public void LoginNavigationCompleted(string address)
    {
        if (disposed || stopping || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !ProbePolicy.IsOfficialHost(uri.IdnHost)
            || !ProbePolicy.IsAllowedNavigation(address)) return;
        if (awaitingLogin)
        {
            sessionGeneration++;
            if (!ChaoxingSyncParser.IsAuthenticationAddress(uri)) ScheduleLoginCheck();
        }
        if (!IsRunning) _ = CheckpointSessionAsync();
    }

    public void LoginConnectionOpened()
    {
        if (disposed || stopping) return;
        sessionGeneration++;
        loginWindowOpen = true;
        awaitingLogin = true;
        ScheduleLoginCheck();
    }

    public void LoginConnectionClosed()
    {
        loginWindowOpen = false;
        _ = CheckpointSessionAsync();
    }

    private void ScheduleLoginCheck()
    {
        if (disposed || stopping || sessionClearing || !store.LoadSettings().AutomaticSyncEnabled) return;
        loginPending = true;
        loginTimer.Start();
    }

    private void CheckLoginSession()
    {
        if (disposed || stopping || sessionClearing || !loginPending || !store.LoadSettings().AutomaticSyncEnabled)
        { loginPending = false; loginTimer.Stop(); return; }
        // Login can complete while startup synchronization is still waiting for HTTP/WebView.
        // Keep the pending check until that run releases the shared session.
        if (IsRunning) return;
        loginPending = false;
        loginTimer.Stop();
        _ = StartAsync(false, afterLogin: true);
    }

    public void SessionClearing()
    {
        sessionGeneration++;
        sessionClearing = true;
        Cancel();
        TryClearArchive();
        StatusChanged?.Invoke("学习通登录数据正在清除；本地任务保留，重新登录后可继续同步。", false, true);
    }

    public void SessionClearCompleted(bool succeeded)
    {
        if (disposed || stopping) return;
        sessionGeneration++;
        sessionClearing = false;
        awaitingLogin = true;
        retryAfter = default;
        StatusChanged?.Invoke(succeeded
            ? "SYNC001：学习通登录数据已清除，完成官方登录后将自动采集。已保留本地任务。"
            : "登录数据清理未完成，请核对官方登录状态；本地任务已保留。", false, true);
        if (loginWindowOpen) ScheduleLoginCheck();
    }

    public void Cancel()
    {
        loginPending = false;
        loginTimer.Stop();
        cancellation?.Cancel();
    }

    public async Task StartAsync(bool manual, bool afterLogin = false, bool catalogOnly = false)
    {
        if (disposed || stopping || sessionClearing) return;
        if (IsRunning)
        {
            if (afterLogin) ScheduleLoginCheck();
            return;
        }
        var settings = store.LoadSettings();
        if (!manual && !settings.AutomaticSyncEnabled) return;
        if (!manual && !afterLogin && DateTimeOffset.UtcNow < retryAfter) return;
        using var runCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        cancellation = runCancellation;
        var runFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        finished = runFinished;
        IChaoxingPageReader? reader = null;
        try
        {
            StatusChanged?.Invoke("正在后台同步学习通…", true, false);
            await EnsureSessionAsync(runCancellation.Token);
            var scope = new ReadScope(sessionGeneration);
            reader = CreateReader(scope);
            var progress = new Progress<ChaoxingSyncProgress>(value =>
            {
                if (!disposed && !stopping && !runCancellation.IsCancellationRequested)
                    StatusChanged?.Invoke(value.Message, true, false);
            });
            var result = await new ChaoxingSynchronizer(store, reader).RunAsync(progress, runCancellation.Token, catalogOnly);
            runCancellation.Token.ThrowIfCancellationRequested();
            if (disposed || stopping || sessionClearing) return;
            var persisted = await FinishSessionAsync(scope, result.LoginRequired, runCancellation.Token);
            if (catalogOnly)
            {
                StatusChanged?.Invoke(result.LoginRequired ? "SYNC001：请在连接学习通窗口完成官方登录后刷新课程。"
                    : result.CourseListCovered ? $"学习通课程列表已刷新，共 {result.Courses} 门课程。"
                    : "学习通课程列表未完整刷新，请查看官方登录与网络状态；原有课程选择保留。", false,
                    result.LoginRequired || !result.CourseListCovered || !persisted);
                return;
            }
            awaitingLogin = result.LoginRequired;
            var outcome = result.LoginRequired ? "login_required"
                : result.Problems.Count > 0 || result.DetailsDeferred > 0 || !result.CourseListCovered ? "partial" : "completed";
            await Task.Run(() => store.RecordSyncResult(result, outcome, DateTimeOffset.UtcNow, runCancellation.Token), runCancellation.Token);
            retryAfter = DateTimeOffset.UtcNow.AddMinutes(result.LoginRequired ? 30 : 5);
            var message = result.LoginRequired
                ? "SYNC001：学习通需要登录。点击“连接学习通”完成一次登录，程序会自动读取各课程并汇总任务。"
                : $"学习通已同步 {result.CoursesRead}/{result.SelectedCourses ?? result.Courses} 门已选课程（发现 {result.Courses} 门）；新增 {result.Added} 条、更新 {result.Updated} 条。"
                    + (result.Problems.Count > 0 ? $"有 {result.Problems.Count} 项未完成，点击“同步详情”查看。" : "")
                    + (result.DetailsUnavailable + result.DetailsRequireAction > 0
                        ? $"其中 {result.DetailsUnavailable + result.DetailsRequireAction} 条任务未取得作答时间，列表信息已保留。" : "")
                    + (result.DetailsDeferred > 0 ? $"剩余 {result.DetailsDeferred} 条任务时间将在后续同步读取。" : "");
            if (!result.LoginRequired && !persisted) message += "SESSION001：登录状态未能保存，重启后可能需要重新登录。";
            StatusChanged?.Invoke(message, false, result.LoginRequired || result.Problems.Count > 0 || !persisted);
            Completed?.Invoke(result);
            // Some official login pages update cookies without navigating the top-level document.
            // Recheck while the user has the connection window open; no login fields are touched.
            if (afterLogin && result.LoginRequired && loginWindowOpen) ScheduleLoginCheck();
        }
        catch (SessionChangedException)
        {
            if (!disposed && !stopping && !sessionClearing)
            {
                awaitingLogin = true;
                StatusChanged?.Invoke("学习通登录状态发生变化，正在使用当前会话重新检查。", false, false);
                ScheduleLoginCheck();
            }
        }
        catch (CourseRecordingChangedException)
        { if (!disposed && !stopping) StatusChanged?.Invoke("学习通记录课程已更新，本轮旧请求已停止；下次同步按新选择采集。", false, false); }
        catch (OperationCanceledException)
        {
            if (!disposed && !stopping)
            {
                var enabled = store.LoadSettings().AutomaticSyncEnabled;
                StatusChanged?.Invoke(enabled ? "本轮同步已停止，已保存的任务保留。" : "您已关闭自动记录，任务不会自动更新，请手动维护任务。", false, true);
            }
        }
        catch (TaskValidationException error)
        { if (!disposed) StatusChanged?.Invoke(error.Code + "：" + error.Message, false, true); }
        catch
        { if (!disposed) StatusChanged?.Invoke("SYNC002：后台同步未完成，请检查网络与 WebView2 运行时。已保留本地任务。", false, true); }
        finally
        {
            (reader as IDisposable)?.Dispose();
            cancellation = null;
            runFinished.TrySetResult(true);
            if (ReferenceEquals(finished, runFinished)) finished = null;
            if (loginPending && !disposed && !stopping && !sessionClearing) loginTimer.Start();
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

    public async Task<TaskLinkResolution> ResolveTaskAddressAsync(Uri address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed || stopping || sessionClearing) return new("session_unavailable", "none");
        try
        {
            await EnsureSessionAsync(token);
            token.ThrowIfCancellationRequested();
            if (disposed || stopping || sessionClearing) return new("session_unavailable", "none");
            var scope = new ReadScope(sessionGeneration);
            using var reader = CreateReader(scope);
            var result = await reader.ResolveTaskAddressAsync(address, token);
            if (result.Outcome == "login_required") await FinishSessionAsync(scope, true, token);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return new("session_unavailable", "none"); }
    }

    private async Task EnsureSessionAsync(CancellationToken token)
    {
        initialization ??= InitializeAsync();
        try { await initialization.WaitAsync(token); }
        catch
        {
            // A cancelled waiter does not cancel WebView initialization or permit a second restore.
            if (initialization.IsFaulted || initialization.IsCanceled) initialization = null;
            throw;
        }
    }

    public async Task<CoreWebView2Environment> GetLoginEnvironmentAsync()
    {
        if (disposed || stopping) throw new InvalidOperationException("Session host is unavailable.");
        await EnsureSessionAsync(CancellationToken.None);
        if (disposed || stopping) throw new InvalidOperationException("Session host is unavailable.");
        return browser.CoreWebView2.Environment;
    }

    private async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null,
            System.IO.Path.Combine(projectRoot, ".local", "webview-profile"));
        if (disposed || stopping) return;
        await browser.EnsureCoreWebView2Async(environment);
        if (disposed || stopping) return;
        var core = browser.CoreWebView2;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        // This WebView is a hidden session host, not a per-course browsing workflow.
        await cookieGate.WaitAsync();
        try
        {
            if (disposed || stopping || sessionClearing) return;
            var observed = await ReadProfileAsync();
            if (disposed || stopping || sessionClearing) return;
            var saved = sessionStore.Load(DateTimeOffset.UtcNow);
            var missing = ChaoxingSessionStore.MissingForRestore(saved, observed);
            foreach (var cookie in missing) ApplyCookie(cookie);
            SessionRecoveryOutcome = missing.Count > 0 ? "restored"
                : saved.Count > 0 ? "profile_preferred" : sessionStore.LastLoadOutcome;
        }
        finally { cookieGate.Release(); }
    }

    private ChaoxingHttpReader CreateReader(ReadScope scope) => new(
        async (address, token) =>
        {
            await cookieGate.WaitAsync(token);
            try
            {
                CheckScope(scope, token);
                var current = await ReadProfileAsync();
                CheckScope(scope, token);
                CheckFingerprint(scope, current);
                return current.Where(cookie => cookie.AppliesTo(address)).Select(cookie => cookie.ToCookie()).ToArray();
            }
            finally { cookieGate.Release(); }
        }, handlerFactory?.Invoke(), saveResponseCookies: async (_, _, renewed, token) =>
        {
            await cookieGate.WaitAsync(token);
            try
            {
                CheckScope(scope, token);
                var current = await ReadProfileAsync();
                CheckScope(scope, token);
                CheckFingerprint(scope, current);
                if (renewed.Count == 0) return;
                var expected = current.ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
                foreach (var cookie in renewed)
                {
                    ApplyCookie(cookie);
                    if (cookie.IsExpired(DateTimeOffset.UtcNow)) expected.Remove(cookie.Key);
                    else expected[cookie.Key] = cookie;
                }
                var updated = await ReadProfileAsync();
                CheckScope(scope, token);
                scope.Fingerprint = Fingerprint(expected.Values);
                CheckFingerprint(scope, updated);
                SaveProfile(updated);
            }
            finally { cookieGate.Release(); }
        });

    private void CheckScope(ReadScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed || stopping || sessionClearing || scope.Generation != sessionGeneration) throw new SessionChangedException();
    }

    private static void CheckFingerprint(ReadScope scope, IEnumerable<ChaoxingSessionCookie> cookies)
    {
        var current = Fingerprint(cookies);
        if (scope.Fingerprint is not null && scope.Fingerprint != current) throw new SessionChangedException();
        scope.Fingerprint = current;
    }

    private static string Fingerprint(IEnumerable<ChaoxingSessionCookie> cookies)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cookies.OrderBy(cookie => cookie.Key, StringComparer.Ordinal)
            .Select(cookie => new[] { cookie.Key, cookie.Value }));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<ChaoxingSessionCookie[]> ReadProfileAsync()
    {
        var observed = await browser.CoreWebView2.CookieManager.GetCookiesAsync("");
        var result = new List<ChaoxingSessionCookie>();
        foreach (var cookie in observed)
        {
            var value = new ChaoxingSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
                cookie.IsSecure, cookie.IsHttpOnly, cookie.IsSession ? null
                    : new DateTimeOffset(cookie.Expires.ToUniversalTime()), cookie.SameSite.ToString());
            if (ChaoxingSessionStore.IsValid(value) && !value.IsExpired(DateTimeOffset.UtcNow)) result.Add(value);
        }
        observed.Clear();
        return result.DistinctBy(cookie => cookie.Key).ToArray();
    }

    private void ApplyCookie(ChaoxingSessionCookie cookie)
    {
        var manager = browser.CoreWebView2.CookieManager;
        var value = manager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        if (cookie.IsExpired(DateTimeOffset.UtcNow)) { manager.DeleteCookie(value); return; }
        value.IsSecure = cookie.Secure; value.IsHttpOnly = cookie.HttpOnly;
        value.Expires = cookie.ExpiresUtc?.UtcDateTime ?? DateTime.MinValue;
        if (Enum.TryParse<CoreWebView2CookieSameSiteKind>(cookie.SameSite, out var sameSite)) value.SameSite = sameSite;
        manager.AddOrUpdateCookie(value);
    }

    private async Task<bool> FinishSessionAsync(ReadScope scope, bool loginRequired, CancellationToken token)
    {
        await cookieGate.WaitAsync(token);
        try
        {
            CheckScope(scope, token);
            var current = await ReadProfileAsync();
            CheckScope(scope, token); CheckFingerprint(scope, current);
            if (loginRequired)
            {
                sessionRejected = true;
                return TryClearArchive();
            }
            sessionRejected = false;
            return SaveProfile(current);
        }
        finally { cookieGate.Release(); }
    }

    private bool SaveProfile(IReadOnlyList<ChaoxingSessionCookie> cookies)
    {
        // A confirmed expired session must not be re-archived by an exit or an unrelated response.
        if (sessionRejected) return TryClearArchive();
        try
        {
            sessionStore.Save(cookies, DateTimeOffset.UtcNow);
            SessionPersistenceOutcome = cookies.Count > 0 ? "saved" : "empty";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { SessionPersistenceOutcome = "unavailable"; return false; }
    }

    private bool TryClearArchive()
    {
        try { sessionStore.Clear(); SessionPersistenceOutcome = "cleared"; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { SessionPersistenceOutcome = "clear_failed"; return false; }
    }

    public async Task PrepareSessionClearAsync()
    {
        if (finished?.Task is { } pending) await pending;
        await cookieGate.WaitAsync();
        try
        {
            sessionStore.Clear();
            sessionRejected = true;
            SessionPersistenceOutcome = "cleared";
        }
        finally { cookieGate.Release(); }
    }

    public async Task<bool> CheckpointSessionAsync()
    {
        if (disposed || stopping || sessionClearing || initialization is null) return true;
        try
        {
            await initialization;
            var generation = sessionGeneration;
            await cookieGate.WaitAsync();
            try
            {
                if (disposed || stopping || sessionClearing) return true;
                var current = await ReadProfileAsync();
                if (disposed || stopping || sessionClearing || generation != sessionGeneration) return true;
                return SaveProfile(current);
            }
            finally { cookieGate.Release(); }
        }
        catch { SessionPersistenceOutcome = "unavailable"; return false; }
    }

    public Task<bool> SaveForExitAsync() => shutdown ??= SaveForExitCoreAsync();

    private async Task<bool> SaveForExitCoreAsync()
    {
        if (disposed) return true;
        stopping = true; sessionGeneration++; timer.Stop(); Cancel();
        try
        {
            if (finished?.Task is { } pending) await pending;
            if (initialization is null) return true;
            await initialization;
            if (browser.CoreWebView2 is null) return true;
            await cookieGate.WaitAsync();
            try
            {
                if (disposed || sessionClearing) return TryClearArchive();
                return SaveProfile(await ReadProfileAsync());
            }
            finally { cookieGate.Release(); }
        }
        catch { SessionPersistenceOutcome = "unavailable"; return false; }
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        timer.Stop(); loginTimer.Stop(); Cancel(); browser.Dispose();
    }
}
