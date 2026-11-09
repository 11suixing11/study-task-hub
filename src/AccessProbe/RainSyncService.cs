using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
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
    private readonly RainSessionStore sessionStore;
    private readonly SemaphoreSlim cookieGate = new(1, 1);
    private Task<bool>? shutdown;
    private bool sessionRejected, stopping;
    private long generation;
    private sealed class ReadScope(long generation)
    {
        public long Generation { get; } = generation;
        public string? Fingerprint { get; set; }
    }
    private sealed class SessionChangedException() : Exception("Platform login changed.");
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
    public event Action? CourseSelectionRequired;
    public event Action<RainSyncSummary>? CatalogCompleted;
    public string SessionRecoveryOutcome { get; private set; } = "not_started";
    public string SessionPersistenceOutcome { get; private set; } = "not_saved";

    public RainSyncService(TaskStore store, string root, RainPlatform platform, WebView2 sessionBrowser,
        Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? loginCheckInterval = null)
    {
        this.store = store; projectRoot = root; Platform = platform; browser = sessionBrowser;
        this.handlerFactory = handlerFactory;
        sessionStore = new(platform, Path.Combine(root, ".local", "sessions", platform.Id + ".bin"), store);
        sessionRejected = store.LoadPlatformSessionInfo(platform.Id).State == "expired";
        timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        timer.Tick += (_, _) => StartAutomatic();
        loginTimer = new DispatcherTimer { Interval = loginCheckInterval ?? TimeSpan.FromSeconds(5) };
        loginTimer.Tick += (_, _) => CheckLogin();
    }

    public static string ProfilePath(string root, RainPlatform platform) => Path.Combine(root, ".local", "webview-profile-" + platform.Id);
    public string Host => store.LoadPlatformSettings(Platform.Id).Host;

    public void StartAutomatic()
    {
        if (disposed || stopping || clearing) return;
        var settings = store.LoadPlatformSettings(Platform.Id);
        if (!settings.Connected)
        {
            StatusChanged?.Invoke(Platform.Name + "尚未连接；完成官方登录后返回课程列表，保存选择后采集任务。", false, false);
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
        if (disposed || stopping || clearing) return;
        generation++; Cancel(); loginOpen = true; awaitingLogin = true;
        var settings = store.LoadPlatformSettings(Platform.Id);
        if (!settings.Connected) store.SavePlatformSettings(Platform.Id, settings with { Connected = true });
        timer.Start(); ScheduleLoginCheck();
    }

    public void ConnectionClosed() => loginOpen = false;

    public async Task SelectHostAsync(string host)
    {
        host = Platform.CheckHost(host);
        if (host == Host) return;
        if (disposed || stopping || clearing) return;
        // Keep the prior run tied to its selected host; wait before changing connection settings.
        var pending = finished?.Task;
        Cancel();
        if (pending is not null) await pending;
        if (disposed || clearing) return;
        generation++;
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
        if (disposed || stopping || clearing || !loginOpen && (!store.LoadSettings().AutomaticSyncEnabled
            || !store.LoadPlatformSettings(Platform.Id).AutomaticSyncEnabled)) return;
        loginPending = true; loginTimer.Start();
    }

    private void CheckLogin()
    {
        if (disposed || stopping || clearing || !loginPending || !loginOpen && (!store.LoadSettings().AutomaticSyncEnabled
            || !store.LoadPlatformSettings(Platform.Id).AutomaticSyncEnabled))
        { loginPending = false; loginTimer.Stop(); return; }
        if (IsRunning) return;
        loginPending = false; loginTimer.Stop();
        _ = StartAsync(loginOpen, afterLogin: true);
    }

    public async Task StartAsync(bool manual, bool afterLogin = false, bool catalogOnly = false)
    {
        if (disposed || stopping || clearing) return;
        if (IsRunning) { if (afterLogin) ScheduleLoginCheck(); return; }
        var settings = store.LoadPlatformSettings(Platform.Id);
        if (!settings.Connected)
        {
            StatusChanged?.Invoke("RAIN001：请先连接" + Platform.Name + "并完成官方登录。", false, true);
            return;
        }
        if (!manual && (!store.LoadSettings().AutomaticSyncEnabled || !settings.AutomaticSyncEnabled)) return;
        if (!manual && !afterLogin && DateTimeOffset.UtcNow < retryAfter) return;
        var needsSelection = !store.LoadCourseRecording(Platform.Id).SelectionConfirmed;
        catalogOnly |= needsSelection || afterLogin && loginOpen;
        using var runCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        cancellation = runCancellation; finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            StatusChanged?.Invoke(catalogOnly ? "正在使用当前登录读取" + Platform.Name + "课程…" : "正在同步" + Platform.Name + "已选课程…", true, false);
            await EnsureSessionAsync(runCancellation.Token);
            var scope = new ReadScope(generation);
            using var reader = new RainHttpReader(Platform, settings.Host, (address, token) => ReadCookiesAsync(scope, address, token),
                handlerFactory?.Invoke(), (address, observed, renewed, token) => SaveResponseCookiesAsync(scope, address, observed, renewed, token));
            var progress = new Progress<string>(message =>
            {
                if (!disposed && !runCancellation.IsCancellationRequested) StatusChanged?.Invoke(message, true, false);
            });
            var summary = await new RainSynchronizer(store, reader, Platform, settings.Host,
                token => ValidateSessionAsync(scope, token)).RunAsync(progress, runCancellation.Token, catalogOnly);
            runCancellation.Token.ThrowIfCancellationRequested();
            if (disposed || stopping || clearing) return;
            awaitingLogin = summary.LoginRequired;
            var persisted = await SaveSessionAsync(scope, summary.LoginRequired, runCancellation.Token);
            if (catalogOnly)
            {
                var codes = summary.Problems.Select(problem => problem.Code).Concat(persisted ? [] : new[] { "SESSION001" })
                    .Distinct(StringComparer.Ordinal).Take(16).ToArray();
                var catalogOutcome = CourseRecordingRules.CatalogRefreshOutcome(summary.CourseListCovered, summary.LoginRequired, codes);
                store.RecordCourseCatalogResult(Platform.Id, catalogOutcome, codes, DateTimeOffset.UtcNow, runCancellation.Token);
                retryAfter = DateTimeOffset.UtcNow.AddMinutes(summary.LoginRequired ? 30 : 5);
                StatusChanged?.Invoke(summary.LoginRequired ? "RAIN001：请在对应连接窗口完成官方登录后刷新课程。"
                    : catalogOutcome == "completed" ? $"{Platform.Name}课程列表已刷新，共 {summary.Courses} 门课程。"
                    : Platform.Name + "课程列表未完整刷新；原有课程选择保留，请检查官方登录与网络。", false,
                    catalogOutcome != "completed");
                if (!summary.LoginRequired && summary.CourseListCovered && (needsSelection || afterLogin && loginOpen)) CourseSelectionRequired?.Invoke();
                if (afterLogin && summary.LoginRequired && loginOpen) ScheduleLoginCheck();
                CatalogCompleted?.Invoke(summary);
                return;
            }
            awaitingLogin = summary.LoginRequired;
            var outcome = summary.LoginRequired ? "login_required"
                : summary.Problems.Count > 0 || !summary.CourseListCovered || summary.CoursesRead != (summary.SelectedCourses ?? summary.Courses) ? "partial" : "completed";
            await Task.Run(() => store.RecordPlatformSyncResult(summary, outcome, DateTimeOffset.UtcNow, runCancellation.Token), runCancellation.Token);
            retryAfter = DateTimeOffset.UtcNow.AddMinutes(summary.LoginRequired ? 30 : 5);
            var message = summary.LoginRequired
                ? "RAIN001：" + Platform.Name + "需要登录；请在平台管理窗口重新登录，再保存课程选择并采集，本地任务保留。"
                : $"{Platform.Name}已读取 {summary.CoursesRead}/{summary.SelectedCourses ?? summary.Courses} 门已选课程（发现 {summary.Courses} 门），采集 {summary.Tasks} 条任务；新增 {summary.Added}、更新 {summary.Updated}。"
                    + (summary.Problems.Count > 0 ? $"有 {summary.Problems.Count} 项未完成，见同步详情。" : "")
                    + (summary.StatusNeedsReview > 0 ? $"有 {summary.StatusNeedsReview} 条状态待核对。" : "");
            if (!summary.LoginRequired && !persisted) message += "SESSION001：当前登录未能保存，请稍后重试。";
            StatusChanged?.Invoke(message, false, outcome != "completed" || !persisted);
            Completed?.Invoke(summary);
            if (afterLogin && summary.LoginRequired && loginOpen) ScheduleLoginCheck();
        }
        catch (SessionChangedException)
        {
            if (!disposed && !stopping && !clearing)
            { awaitingLogin = true; StatusChanged?.Invoke(Platform.Name + "登录已更新，正在重新检查当前会话。", false, false); ScheduleLoginCheck(); }
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
            if (catalogOnly && !disposed && !clearing) RecordCatalogFailure(error.Code, runCancellation.Token);
            if (!disposed) StatusChanged?.Invoke(error.Code + "：" + error.Message, false, true);
        }
        catch
        {
            if (catalogOnly && !disposed && !clearing) RecordCatalogFailure("RAIN002", runCancellation.Token);
            if (!disposed) StatusChanged?.Invoke("RAIN002：" + Platform.Name + "同步未完成，请检查网络或 WebView2；本地任务保留。", false, true);
        }
        finally
        {
            cancellation = null; finished?.TrySetResult(true); finished = null;
            if (loginPending && !disposed && !clearing) loginTimer.Start();
        }
    }

    private void RecordCatalogFailure(string code, CancellationToken token)
    {
        try { store.RecordCourseCatalogResult(Platform.Id, "error", [code], DateTimeOffset.UtcNow, token); }
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
        var saved = sessionStore.Load(DateTimeOffset.UtcNow);
        var current = await ReadProfileAsync();
        var sameProfile = !current.Any(cookie => !saved.Any(value => value.Key == cookie.Key && value.Value == cookie.Value));
        var missing = sameProfile ? saved.Where(cookie => !current.Any(value => value.Key == cookie.Key)).ToArray() : [];
        foreach (var cookie in missing) ApplyCookie(cookie);
        for (var attempt = 0; missing.Length > 0 && attempt < 40; attempt++)
        {
            var restored = await ReadProfileAsync();
            if (missing.All(cookie => restored.Any(value => value.Key == cookie.Key && value.Value == cookie.Value))) break;
            if (attempt == 39) throw new InvalidOperationException("Session restore did not complete.");
            await Task.Delay(25);
        }
        SessionRecoveryOutcome = missing.Length > 0 ? "restored" : saved.Count > 0 ? "profile_preferred" : sessionStore.LastLoadOutcome;
    }

    internal static void ConfigureCore(CoreWebView2 core)
    {
        core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private async Task<IReadOnlyList<Cookie>> ReadCookiesAsync(ReadScope scope, Uri address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed || clearing) throw new OperationCanceledException(token);
        await cookieGate.WaitAsync(token);
        try
        {
            CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current);
            return current.Where(cookie => cookie.AppliesTo(address)).Select(cookie => cookie.ToCookie()).ToArray();
        }
        finally { cookieGate.Release(); }
    }

    private async Task<ChaoxingSessionCookie[]> ReadProfileAsync()
    {
        var observed = await browser.CoreWebView2.CookieManager.GetCookiesAsync("");
        var result = observed.Select(cookie => new ChaoxingSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
            cookie.IsSecure, cookie.IsHttpOnly, cookie.IsSession ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()), cookie.SameSite.ToString()))
            .Where(cookie => sessionStore.IsValid(cookie) && !cookie.IsExpired(DateTimeOffset.UtcNow)).DistinctBy(cookie => cookie.Key).ToArray();
        observed.Clear(); return result;
    }

    private async Task ValidateSessionAsync(ReadScope scope, CancellationToken token)
    {
        await cookieGate.WaitAsync(token);
        try { CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current); }
        finally { cookieGate.Release(); }
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

    private async Task SaveResponseCookiesAsync(ReadScope scope, Uri address, IReadOnlyList<Cookie> observed,
        IReadOnlyList<ChaoxingSessionCookie> renewed, CancellationToken token)
    {
        await cookieGate.WaitAsync(token);
        try
        {
            CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current);
            if (renewed.Count == 0) return;
            var expected = current.ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
            foreach (var cookie in renewed.Where(sessionStore.IsValid))
            { ApplyCookie(cookie); if (cookie.IsExpired(DateTimeOffset.UtcNow)) expected.Remove(cookie.Key); else expected[cookie.Key] = cookie; }
            var desired = Fingerprint(expected.Values); var previous = current.ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var updated = await ReadProfileAsync(); CheckScope(scope, token);
                if (Fingerprint(updated) == desired) { scope.Fingerprint = desired; SaveProfile(updated); return; }
                if (updated.Any(cookie => cookie.Value != previous.GetValueOrDefault(cookie.Key)?.Value
                    && cookie.Value != expected.GetValueOrDefault(cookie.Key)?.Value)) throw new SessionChangedException();
                await Task.Delay(25, token);
            }
            throw new SessionChangedException();
        }
        finally { cookieGate.Release(); }
    }

    private async Task<bool> SaveSessionAsync(ReadScope scope, bool loginRequired, CancellationToken token)
    {
        await cookieGate.WaitAsync(token);
        try
        {
            CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current);
            if (loginRequired) { sessionRejected = true; sessionStore.Invalidate(DateTimeOffset.UtcNow); SessionPersistenceOutcome = "expired"; return true; }
            sessionRejected = false;
            var saved = SaveProfile(current);
            if (saved) store.ConfirmPlatformSession(Platform.Id, DateTimeOffset.UtcNow);
            return saved;
        }
        finally { cookieGate.Release(); }
    }

    private bool SaveProfile(IReadOnlyList<ChaoxingSessionCookie> current)
    {
        if (sessionRejected) return true;
        try { sessionStore.Save(current, DateTimeOffset.UtcNow); SessionPersistenceOutcome = current.Count > 0 ? "saved" : "empty"; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { SessionPersistenceOutcome = "unavailable"; return false; }
    }

    private void CheckScope(ReadScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed || stopping || clearing || scope.Generation != generation) throw new SessionChangedException();
    }
    private static void CheckFingerprint(ReadScope scope, IEnumerable<ChaoxingSessionCookie> current)
    {
        var value = Fingerprint(current);
        if (scope.Fingerprint is not null && scope.Fingerprint != value) throw new SessionChangedException();
        scope.Fingerprint = value;
    }
    private static string Fingerprint(IEnumerable<ChaoxingSessionCookie> cookies)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cookies.OrderBy(cookie => cookie.Key, StringComparer.Ordinal).Select(cookie => new[] { cookie.Key, cookie.Value }));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Cancel()
    {
        loginPending = false; loginTimer.Stop(); cancellation?.Cancel();
    }

    public async Task ClearSessionAsync()
    {
        if (disposed || clearing) return;
        generation++; clearing = true; timer.Stop();
        var pending = finished?.Task; Cancel();
        StatusChanged?.Invoke("正在清除" + Platform.Name + "登录数据；本地任务保留。", false, true);
        try
        {
            if (pending is not null) await pending;
            if (disposed) return;
            await EnsureSessionAsync();
            sessionStore.Clear(); sessionRejected = true; SessionPersistenceOutcome = "cleared";
            browser.CoreWebView2.CookieManager.DeleteAllCookies();
            await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            retryAfter = default; awaitingLogin = true;
            StatusChanged?.Invoke("RAIN001：" + Platform.Name + "登录数据已清除；重新登录后刷新课程列表，再保存选择并采集。", false, true);
        }
        finally { clearing = false; }
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); Cancel(); browser.Dispose();
    }

    public Task<bool> SaveForExitAsync() => shutdown ??= SaveForExitCoreAsync();
    private async Task<bool> SaveForExitCoreAsync()
    {
        if (disposed) return true;
        stopping = true; timer.Stop(); var pending = finished?.Task; Cancel();
        try
        {
            if (pending is not null) await pending;
            if (initialization is null || sessionRejected) return true;
            await initialization;
            await cookieGate.WaitAsync();
            try { sessionStore.Save(await ReadProfileAsync(), DateTimeOffset.UtcNow); SessionPersistenceOutcome = "saved"; return true; }
            finally { cookieGate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        { SessionPersistenceOutcome = "unavailable"; return false; }
    }
}
