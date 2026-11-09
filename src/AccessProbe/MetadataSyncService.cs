using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class MetadataSyncService : IDisposable
{
    public MetadataPlatform Platform { get; }
    private readonly TaskStore store;
    private readonly string projectRoot;
    private readonly WebView2 browser;
    private readonly Func<HttpMessageHandler>? handlerFactory;
    private readonly byte[]? syntheticMetadataKey;
    private readonly MetadataSessionStore sessionStore;
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
    private string? metadataToken, tokenCookieBinding;
    private string? metadataTokenSource;
    private sealed class SessionChangedException(string? context = null) : Exception(context);
    private sealed class ReadScope(long generation)
    {
        public long Generation { get; } = generation;
        public string? Fingerprint { get; set; }
    }

    public bool IsRunning => cancellation is not null;
    public string SessionRecoveryOutcome { get; private set; } = "not_started";
    public string SessionPersistenceOutcome { get; private set; } = "not_saved";
    public MetadataLoginDiagnostics LoginDiagnostics { get; }
    public event Action<string, bool, bool>? StatusChanged;
    public event Action<MetadataSyncSummary>? Completed;
    public event Action? CourseSelectionRequired;
    public event Action<MetadataSyncSummary>? CatalogCompleted;

    public MetadataSyncService(MetadataPlatform platform, TaskStore store, string root, WebView2 sessionBrowser,
        Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? loginCheckInterval = null, ReadOnlyMemory<byte>? testMetadataKey = null)
    {
        if (testMetadataKey is { } syntheticKey)
        {
            if (handlerFactory is null || platform.Id != "zhihuishu" || syntheticKey.Length != 16) throw new ArgumentException("Synthetic key requires an isolated test handler.");
            syntheticMetadataKey = syntheticKey.ToArray();
        }
        Platform = platform; this.store = store; projectRoot = root; browser = sessionBrowser; this.handlerFactory = handlerFactory;
        LoginDiagnostics = new(platform, Path.Combine(root, ".local", "diagnostics", platform.Id + "-login.json"));
        sessionStore = new(platform, Path.Combine(root, ".local", "sessions", platform.Id + ".bin"), store);
        sessionRejected = platform.AdaptationEnabled && store.LoadPlatformSessionInfo(platform.Id).State == "expired";
        loginTimer = new() { Interval = loginCheckInterval ?? TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => StartAutomatic(); loginTimer.Tick += (_, _) => CheckLogin();
    }
    public static string ProfilePath(string root, string platformId) => Path.Combine(root, ".local", "webview-profile-" + MetadataPlatform.Get(platformId).Id);

    public void StartAutomatic()
    {
        if (disposed || stopping || clearing) return;
        if (!Platform.AdaptationEnabled) return;
        var settings = store.LoadMetadataSettings(Platform.Id);
        if (!settings.Connected) { StatusChanged?.Invoke($"{Platform.Name}尚未连接；完成官方登录后返回课程列表，保存选择后采集任务。", false, false); return; }
        if (!store.LoadSettings().AutomaticSyncEnabled || !settings.AutomaticSyncEnabled)
        { StatusChanged?.Invoke($"{Platform.Name} 自动同步已关闭，可点击“同步所有平台”手动更新。", false, true); return; }
        timer.Start(); _ = StartAsync(false);
    }

    public void SettingsChanged(bool enabled)
    {
        if (disposed || stopping) return;
        if (enabled) { retryAfter = default; StartAutomatic(); }
        else { timer.Stop(); Cancel(); StatusChanged?.Invoke($"{Platform.Name} 自动同步已关闭，可手动更新任务。", false, true); }
    }

    public void ConnectionOpened()
    {
        if (disposed || stopping || clearing || !Platform.AdaptationEnabled) return;
        loginOpen = true; generation++; Cancel();
        var settings = store.LoadMetadataSettings(Platform.Id);
        if (!settings.Connected) store.SaveMetadataSettings(Platform.Id, settings with { Connected = true });
        timer.Start(); ScheduleLoginCheck();
    }
    public void ConnectionClosed()
    {
        loginOpen = false;
        _ = SaveConnectionProfileAsync();
    }
    private async Task SaveConnectionProfileAsync()
    {
        if (disposed || stopping || clearing || browser.CoreWebView2 is null) return;
        await cookieGate.WaitAsync();
        try
        {
            if (disposed || stopping || clearing) return;
            var current = await ReadProfileAsync();
            if (disposed || stopping || clearing) return;
            if (!sessionRejected) SaveProfile(current);
            else if (Platform.Id is "ucampus" or "zhihuishu")
            {
                var account = Platform.Id == "ucampus" ? MetadataPlatformParser.UcampusAccount(current) : MetadataPlatformParser.ZhihuishuAccount(current);
                var rejected = sessionStore.LoadForRevalidation(DateTimeOffset.UtcNow);
                if (account is not null && current.Any(cookie => Platform.IsAccountCookie(cookie) && cookie.Value.Length > 0
                    && !rejected.Any(previous => previous.Key == cookie.Key && previous.Value == cookie.Value)))
                {
                    // A newly completed login can close before the timer checks it. Save it as unverified, without reviving the rejected session.
                    var binding = CookieFingerprint(current); var token = tokenCookieBinding == binding ? metadataToken : null;
                    sessionStore.Save(current, DateTimeOffset.UtcNow, token, token is null ? null : binding, token is null ? null : metadataTokenSource);
                    SessionPersistenceOutcome = "saved";
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or IOException or Microsoft.Data.Sqlite.SqliteException)
        { SessionPersistenceOutcome = "unavailable"; }
        finally { cookieGate.Release(); }
    }
    public void LoginNavigationCompleted(string address)
    {
        if (Platform.IsLoginNavigation(address) && address != "about:blank") ScheduleLoginCheck();
    }
    private void ScheduleLoginCheck()
    {
        if (disposed || stopping || clearing || !Platform.AdaptationEnabled || !loginOpen && (!store.LoadSettings().AutomaticSyncEnabled || !store.LoadMetadataSettings(Platform.Id).AutomaticSyncEnabled)) return;
        loginPending = true; loginTimer.Start();
    }
    private void CheckLogin()
    {
        if (disposed || stopping || clearing || !loginOpen && (!store.LoadSettings().AutomaticSyncEnabled || !store.LoadMetadataSettings(Platform.Id).AutomaticSyncEnabled))
        { loginPending = false; loginTimer.Stop(); return; }
        if (IsRunning || !loginPending) return;
        loginPending = false; loginTimer.Stop(); _ = StartAsync(loginOpen, true);
    }

    public async Task StartAsync(bool manual, bool afterLogin = false, bool catalogOnly = false)
    {
        if (disposed || stopping || clearing) return;
        if (!Platform.AdaptationEnabled) return;
        if (IsRunning) { if (afterLogin) ScheduleLoginCheck(); return; }
        var settings = store.LoadMetadataSettings(Platform.Id);
        if (!settings.Connected) { StatusChanged?.Invoke($"{Platform.CodePrefix}001：请先连接{Platform.Name} 并完成官方登录。", false, true); return; }
        if (!manual && (!store.LoadSettings().AutomaticSyncEnabled || !settings.AutomaticSyncEnabled)) return;
        if (!manual && !afterLogin && DateTimeOffset.UtcNow < retryAfter) return;
        var needsSelection = !store.LoadCourseRecording(Platform.Id).SelectionConfirmed;
        catalogOnly |= needsSelection || afterLogin && loginOpen;
        using var runCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        cancellation = runCancellation; var runFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); finished = runFinished;
        var scope = new ReadScope(generation);
        var stage = "session_initialization";
        try
        {
            StatusChanged?.Invoke(catalogOnly ? $"正在使用当前登录读取{Platform.Name}课程…" : $"正在同步{Platform.Name}已选课程…", true, false);
            await EnsureSessionAsync(runCancellation.Token);
            stage = "session_validation";
            await ValidateSessionAsync(scope, runCancellation.Token);
            if (Platform.Id is "ucampus" or "zhihuishu" && afterLogin)
            {
                var profile = await ReadProfileAsync(); CheckScope(scope, runCancellation.Token); CheckFingerprint(scope, profile);
                var account = Platform.Id == "zhihuishu" ? MetadataPlatformParser.ZhihuishuAccount(profile) : MetadataPlatformParser.UcampusAccount(profile);
                if (account is null)
                {
                    LoginDiagnostics.Record(MetadataLoginStage.SessionCheck, MetadataLoginResult.AwaitingLogin);
                    StatusChanged?.Invoke($"正在等待{Platform.Name}官方登录完成；登录后请等待返回学生课程主页，Cookie 会自动更新。", false, false);
                    if (loginOpen) ScheduleLoginCheck(); return;
                }
            }
            stage = "course_task_metadata";
            using var reader = CreateReader(scope);
            var progress = new Progress<string>(message =>
            { if (!disposed && !runCancellation.IsCancellationRequested) StatusChanged?.Invoke(message, true, false); });
            var summary = await new MetadataSynchronizer(Platform, store, reader, token => ValidateSessionAsync(scope, token))
                .RunAsync(progress, runCancellation.Token, catalogOnly);
            stage = "session_persistence";
            if (!await FinishSessionAsync(scope, summary.LoginRequired, runCancellation.Token, summary.AccountVerified))
                summary = summary with { Problems = summary.Problems.Concat([new MetadataSyncProblem(Platform.CodePrefix + "004", "", "登录状态未能保存，重启后可能需要重新登录。")]).ToArray() };
            CheckScope(scope, runCancellation.Token);
            LoginDiagnostics.Record(MetadataLoginStage.SessionCheck, summary.LoginRequired ? MetadataLoginResult.LoginRequired
                : !summary.AccountVerified ? MetadataLoginResult.AccountUnknown
                : summary.CourseListCovered ? MetadataLoginResult.MetadataReady : MetadataLoginResult.AccountReady);
            if (catalogOnly)
            {
                var codes = summary.Problems.Select(problem => problem.Code).Distinct(StringComparer.Ordinal).Take(16).ToArray();
                var catalogOutcome = CourseRecordingRules.CatalogRefreshOutcome(summary.CourseListCovered, summary.LoginRequired, codes);
                store.RecordCourseCatalogResult(Platform.Id, catalogOutcome, codes, DateTimeOffset.UtcNow, runCancellation.Token);
                retryAfter = DateTimeOffset.UtcNow.AddMinutes(summary.LoginRequired ? 30 : 5);
                StatusChanged?.Invoke(summary.LoginRequired ? $"{Platform.CodePrefix}001：请在连接{Platform.Name}窗口完成官方登录后刷新课程。"
                    : catalogOutcome == "completed" ? $"{Platform.Name} 课程列表已刷新，共 {summary.Courses} 门课程。"
                    : summary.AccountVerified ? $"{Platform.Name} 登录已确认，课程列表未完整刷新；已有课程可选择，请查看课程刷新详情后重试。"
                    : $"{Platform.Name} 课程列表未完整刷新；原有课程选择保留，请查看登录诊断。", false,
                    catalogOutcome != "completed");
                if (!summary.LoginRequired && summary.CourseListCovered && (needsSelection || afterLogin && loginOpen)) CourseSelectionRequired?.Invoke();
                CatalogCompleted?.Invoke(summary);
                if (afterLogin && summary.LoginRequired && loginOpen) ScheduleLoginCheck();
                return;
            }
            var outcome = summary.LoginRequired ? "login_required" : summary.Problems.Count > 0 || !summary.CourseListCovered || summary.CoursesRead != summary.SelectedCourses
                || !summary.FullTaskListsCovered || Platform.Id == "ucampus" && !summary.LearningUnitListsCovered ? "partial" : "completed";
            stage = "result_persistence";
            store.RecordMetadataSyncResult(summary, outcome, DateTimeOffset.UtcNow, runCancellation.Token);
            retryAfter = DateTimeOffset.UtcNow.AddMinutes(summary.LoginRequired ? 30 : 5);
            var readProgress = Platform.Id == "pta" && summary.TaskListCoursesRead is { } listCount
                ? $"PTA 已读取 {listCount}/{summary.SelectedCourses} 门已选课程的题目集，完整核对答卷 {summary.CoursesRead} 门（发现 {summary.Courses} 门）；采集 {summary.Tasks} 条任务；新增 {summary.Added}、更新 {summary.Updated}。"
                : $"{Platform.Name} 已读取 {summary.CoursesRead}/{summary.SelectedCourses} 门已选课程（发现 {summary.Courses} 门），采集 {summary.Tasks} 条任务；新增 {summary.Added}、更新 {summary.Updated}。";
            var message = summary.LoginRequired ? $"{Platform.CodePrefix}001：{Platform.Name}需要登录；请在平台管理窗口重新登录，再保存课程选择并采集，已有任务保留。"
                : readProgress
                    + (summary.TeachingDirectoryFallbackCourses > 0 ? $"其中 {summary.TeachingDirectoryFallbackCourses} 门已通过官方教学目录读取。" : "")
                    + (summary.Problems.Count > 0 ? $"有 {summary.Problems.Count} 项需处理，见同步详情。" : "")
                    + (summary.StatusNeedsReview > 0 ? $"有 {summary.StatusNeedsReview} 条状态待核对。" : "")
                    + (Platform.Id switch
                    {
                        "ucampus" => $"其中单元学习 {summary.LearningUnits} 条，限时 {summary.LearningUnitsWithDeadline} 条，必学未完或待核对 {summary.RequiredLearningUnitsIncomplete} 条；特殊课件与其他活动范围见同步详情。",
                        "pta" => "覆盖教学课程题目集；互评、特殊题目集及逐题进度的范围见同步详情。",
                        _ => "覆盖翻转课、共享课和新版智慧课程已确认的作业、考试，完整范围见同步详情。"
                    });
            StatusChanged?.Invoke(message, false, outcome != "completed"); Completed?.Invoke(summary);
            if (afterLogin && summary.LoginRequired && loginOpen) ScheduleLoginCheck();
        }
        catch (SessionChangedException error)
        {
            LoginDiagnostics.RecordSessionChange(error.Message);
            if (!disposed && !stopping) StatusChanged?.Invoke($"{Platform.CodePrefix}003：登录会话在同步期间更新，已停止旧请求；请重新同步，已保存任务保留。", false, true);
            if (loginOpen) ScheduleLoginCheck();
        }
        catch (CourseRecordingChangedException)
        { if (!disposed && !stopping) StatusChanged?.Invoke($"{Platform.Name} 记录课程已更新，本轮旧请求已停止；下次同步按新选择采集。", false, false); }
        catch (OperationCanceledException)
        { if (!disposed && !stopping) StatusChanged?.Invoke($"{Platform.Name} 本轮同步已停止；已有任务保留。", false, true); }
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
                var message = $"{Platform.CodePrefix}002：" + context + "未完成（" + detail + "）；已有任务保留，可稍后重新同步。";
                var summary = new MetadataSyncSummary(Platform.Id, 0, 0, 0, 0, 0, 0, false, false, [new(Platform.CodePrefix + "002", "", message)]);
                try
                {
                    CheckScope(scope, runCancellation.Token);
                    if (!catalogOnly)
                    {
                        store.RecordMetadataSyncResult(summary, "error", DateTimeOffset.UtcNow, runCancellation.Token);
                        Completed?.Invoke(summary);
                    }
                    else store.RecordCourseCatalogResult(Platform.Id, "error", [Platform.CodePrefix + "002"], DateTimeOffset.UtcNow, runCancellation.Token);
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
        catch { if (initialization.IsFaulted || initialization.IsCanceled) initialization = null; throw; }
        token.ThrowIfCancellationRequested(); if (disposed) throw new OperationCanceledException(token);
        return browser.CoreWebView2.Environment;
    }
    private async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, ProfilePath(projectRoot, Platform.Id));
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
            var current = await ReadProfileAsync(); var saved = sessionStore.LoadForRevalidation(DateTimeOffset.UtcNow);
            var missing = MetadataSessionStore.MissingForRestore(Platform, saved, current);
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
                    if (restored.Any(cookie => Platform.IsAccountCookie(cookie)
                        && !expected.Any(value => value.Key == cookie.Key && value.Value == cookie.Value)))
                        throw new SessionChangedException("restore_login_changed");
                    if (attempt == 39) throw new SessionChangedException("restore_pending");
                    await Task.Delay(25);
                }
            }
            SessionRecoveryOutcome = missing.Count > 0 ? "restored" : saved.Count > 0 ? "profile_preferred" : sessionStore.LastLoadOutcome;
            var restoredProfile = await ReadProfileAsync();
            if (Platform.Id == "zhihuishu" && sessionStore.TokenBinding == CookieFingerprint(restoredProfile))
            { metadataToken = sessionStore.Token; tokenCookieBinding = sessionStore.TokenBinding; metadataTokenSource = sessionStore.TokenSource; }
        }
        finally { cookieGate.Release(); }
    }

    private MetadataHttpReader CreateReader(ReadScope scope)
    {
        var reader = new MetadataHttpReader(Platform, async (address, token) =>
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
                var requestCookies = observed.ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
                // A page may refresh server or analytics cookies while an HTTP read is pending.
                // Keep the newer browser value; authentication changes are checked by the scope fingerprint.
                var applicable = renewed.Where(cookie => Platform.IsAccountCookie(cookie)
                    || expected.GetValueOrDefault(cookie.Key)?.Value == requestCookies.GetValueOrDefault(cookie.Key)?.Value).ToArray();
                foreach (var cookie in applicable)
                { if (cookie.IsExpired(DateTimeOffset.UtcNow)) expected.Remove(cookie.Key); else expected[cookie.Key] = cookie; }
                var previousAccount = Platform.Id switch { "zhihuishu" => MetadataPlatformParser.ZhihuishuAccount(current), "ucampus" => MetadataPlatformParser.UcampusAccount(current), _ => null };
                var nextAccount = Platform.Id switch { "zhihuishu" => MetadataPlatformParser.ZhihuishuAccount(expected.Values), "ucampus" => MetadataPlatformParser.UcampusAccount(expected.Values), _ => null };
                if (previousAccount is not null && nextAccount is not null && previousAccount != nextAccount)
                    throw new SessionChangedException("response_account_changed");
                var preserveToken = Platform.Id == "zhihuishu" && tokenCookieBinding == CookieFingerprint(current) && previousAccount is not null
                    && MetadataPlatformParser.ZhihuishuAccount(expected.Values) == previousAccount;
                foreach (var cookie in applicable) ApplyCookie(cookie);
                // CookieManager writes cross a process boundary. Accept only known authentication before/after values.
                var desiredCookies = CookieFingerprint(expected.Values); var previous = current.ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    var updated = await ReadProfileAsync(); CheckScope(scope, token);
                    var updatedByKey = updated.ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
                    var writesPending = applicable.Any(cookie => !Platform.IsAccountCookie(cookie)
                        && updatedByKey.GetValueOrDefault(cookie.Key)?.Value == previous.GetValueOrDefault(cookie.Key)?.Value
                        && updatedByKey.GetValueOrDefault(cookie.Key)?.Value != expected.GetValueOrDefault(cookie.Key)?.Value);
                    if (CookieFingerprint(updated) == desiredCookies && (!writesPending || attempt == 39))
                    {
                        if (preserveToken) tokenCookieBinding = desiredCookies;
                        scope.Fingerprint = Fingerprint(updated);
                        // A rejected service may delete its own cookies. Keep the last encrypted session for official revalidation.
                        if (!applicable.Any(cookie => Platform.IsAccountCookie(cookie) && cookie.IsExpired(DateTimeOffset.UtcNow))) SaveProfile(updated);
                        return;
                    }
                    if (updated.Any(cookie => Platform.IsAccountCookie(cookie) && cookie.Value != previous.GetValueOrDefault(cookie.Key)?.Value
                        && cookie.Value != expected.GetValueOrDefault(cookie.Key)?.Value)) throw new SessionChangedException("profile_changed");
                    await Task.Delay(25, token);
                }
                throw new SessionChangedException("cookie_writeback_pending");
            }
            finally { cookieGate.Release(); }
        }, token => ReadBoundTokenAsync(scope, token), syntheticMetadataKey is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(syntheticMetadataKey));
        reader.ReadCompleted += LoginDiagnostics.RecordRequest;
        return reader;
    }

    private async Task ValidateSessionAsync(ReadScope scope, CancellationToken token)
    {
        await cookieGate.WaitAsync(token);
        try { CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current); }
        finally { cookieGate.Release(); }
    }
    public async Task<TaskLinkResolution> ResolveTaskAddressAsync(Uri address, CancellationToken token)
    {
        if (disposed || stopping || clearing || Platform.Id != "ucampus") return new("session_unavailable", "none");
        try
        {
            await EnsureSessionAsync(token); var scope = new ReadScope(generation);
            await ValidateSessionAsync(scope, token); using var reader = CreateReader(scope);
            var result = await reader.ResolveUcampusAsync(address.AbsoluteUri, token);
            await ValidateSessionAsync(scope, token);
            if (result.Outcome == "login_required") await FinishSessionAsync(scope, true, token);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (SessionChangedException) { return new("account_mismatch", "none"); }
        catch { return new("session_unavailable", "none"); }
    }
    private void CheckScope(ReadScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed || stopping || clearing || scope.Generation != generation) throw new SessionChangedException();
    }
    private void CheckFingerprint(ReadScope scope, IEnumerable<MetadataSessionCookie> cookies)
    {
        var current = Fingerprint(cookies);
        if (scope.Fingerprint is not null && scope.Fingerprint != current) throw new SessionChangedException("profile_changed");
        scope.Fingerprint = current;
    }
    private string CookieFingerprint(IEnumerable<MetadataSessionCookie> cookies)
        => MetadataSessionStore.CookieBinding(Platform, cookies);
    private string Fingerprint(IEnumerable<MetadataSessionCookie> cookies)
    {
        var cookieFingerprint = CookieFingerprint(cookies);
        var token = tokenCookieBinding == cookieFingerprint ? metadataToken : null;
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cookieFingerprint + "\n" + token)));
    }
    private async Task<string?> ReadBoundTokenAsync(ReadScope scope, CancellationToken token)
    {
        await cookieGate.WaitAsync(token);
        try
        {
            CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current);
            return tokenCookieBinding == CookieFingerprint(current) ? metadataToken : null;
        }
        finally { cookieGate.Release(); }
    }
    public async Task ObserveMetadataTokenAsync(string address, Func<Task<string>> readScriptResult, CancellationToken cancellationToken = default)
    {
        if (Platform.Id != "zhihuishu" || !Platform.IsMetadataDocument(address) || disposed || stopping || clearing) return;
        var currentGeneration = generation;
        await cookieGate.WaitAsync(cancellationToken);
        try
        {
            if (disposed || stopping || clearing || currentGeneration != generation) return;
            var before = await ReadProfileAsync().WaitAsync(cancellationToken); var binding = CookieFingerprint(before);
            if (MetadataPlatformParser.ZhihuishuAccount(before) is null) return;
            var json = await readScriptResult().WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var after = await ReadProfileAsync().WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || stopping || clearing || currentGeneration != generation || CookieFingerprint(after) != binding) return;
            using var document = JsonDocument.Parse(json);
            var result = document.RootElement;
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("eligible", out var eligible) || eligible.ValueKind != JsonValueKind.True) return;
            var value = MetadataPlatformParser.Property(result, "token");
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return;
            var token = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (token is { Length: > 16384 } || token is not null && token.Any(char.IsControl)) return;
            var source = MetadataPlatformParser.Text(result, "source");
            if (source.Length == 0) source = new Uri(address).GetLeftPart(UriPartial.Authority);
            if (source is not ("https://onlineweb.zhihuishu.com" or "https://onlinestuh5.zhihuishu.com")) return;
            if (source == "https://onlineweb.zhihuishu.com" && new Uri(address).IdnHost != "onlineweb.zhihuishu.com") return;
            var bound = tokenCookieBinding == binding && !string.IsNullOrEmpty(metadataToken);
            if (bound && string.IsNullOrEmpty(token) && source != metadataTokenSource) return;
            if (bound && metadataTokenSource == "https://onlineweb.zhihuishu.com" && source != metadataTokenSource) return;
            if (!bound && string.IsNullOrEmpty(token)) return;
            var changed = metadataToken != token || tokenCookieBinding != binding;
            metadataToken = token; tokenCookieBinding = binding; metadataTokenSource = source;
            if (changed) { generation++; cancellation?.Cancel(); sessionRejected = false; SaveProfile(before); ScheduleLoginCheck(); }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ObjectDisposedException) { }
        finally { cookieGate.Release(); }
    }
    private async Task<MetadataSessionCookie[]> ReadProfileAsync()
    {
        var observed = await browser.CoreWebView2.CookieManager.GetCookiesAsync(""); var result = new List<MetadataSessionCookie>();
        foreach (var cookie in observed)
        {
            var value = new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly,
                cookie.IsSession ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()), cookie.SameSite.ToString());
            if (MetadataSessionStore.IsValid(Platform, value) && !value.IsExpired(DateTimeOffset.UtcNow)) result.Add(value);
        }
        observed.Clear(); var profile = result.DistinctBy(cookie => cookie.Key).ToArray();
        LoginDiagnostics.ObserveSession(profile, tokenCookieBinding == CookieFingerprint(profile) && !string.IsNullOrEmpty(metadataToken));
        return profile;
    }
    private void ApplyCookie(MetadataSessionCookie cookie)
    {
        var manager = browser.CoreWebView2.CookieManager; var value = manager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        if (cookie.IsExpired(DateTimeOffset.UtcNow)) { manager.DeleteCookie(value); return; }
        value.IsSecure = cookie.Secure; value.IsHttpOnly = cookie.HttpOnly; value.Expires = cookie.ExpiresUtc?.UtcDateTime ?? DateTime.MinValue;
        if (Enum.TryParse<CoreWebView2CookieSameSiteKind>(cookie.SameSite, out var sameSite)) value.SameSite = sameSite;
        manager.AddOrUpdateCookie(value);
    }
    private async Task<bool> FinishSessionAsync(ReadScope scope, bool loginRequired, CancellationToken token, bool accountVerified = false)
    {
        await cookieGate.WaitAsync(token);
        try
        {
            CheckScope(scope, token); var current = await ReadProfileAsync(); CheckScope(scope, token); CheckFingerprint(scope, current);
            if (loginRequired) { sessionRejected = true; sessionStore.Invalidate(DateTimeOffset.UtcNow); SessionPersistenceOutcome = "expired"; return true; }
            if (!accountVerified) return SaveProfile(current);
            sessionRejected = false; var saved = SaveProfile(current);
            if (saved) store.ConfirmPlatformSession(Platform.Id, DateTimeOffset.UtcNow);
            return saved;
        }
        finally { cookieGate.Release(); }
    }
    private bool SaveProfile(IReadOnlyList<MetadataSessionCookie> current)
    {
        if (sessionRejected) return true;
        try
        {
            var binding = CookieFingerprint(current); var token = tokenCookieBinding == binding ? metadataToken : null;
            sessionStore.Save(current, DateTimeOffset.UtcNow, token, token is null ? null : binding, token is null ? null : metadataTokenSource);
            SessionPersistenceOutcome = current.Count > 0 ? "saved" : "empty"; return true;
        }
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
        StatusChanged?.Invoke($"正在清除{Platform.Name} 登录数据，已有任务保留。", false, true);
        try
        {
            if (pending is not null) await pending;
            await EnsureSessionAsync(); await cookieGate.WaitAsync();
            try
            {
                sessionRejected = true; metadataToken = tokenCookieBinding = metadataTokenSource = null; sessionStore.Clear(); SessionPersistenceOutcome = "cleared";
                browser.CoreWebView2.Stop(); browser.CoreWebView2.CookieManager.DeleteAllCookies();
                await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            }
            finally { cookieGate.Release(); }
            retryAfter = default; StatusChanged?.Invoke($"{Platform.CodePrefix}001：登录数据已清除，请在官方页面重新登录。", false, true);
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
        if (syntheticMetadataKey is not null) CryptographicOperations.ZeroMemory(syntheticMetadataKey);
    }
}
