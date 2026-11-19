using AccessProbe;
using System.Net;

namespace AccessProbe.Mobile;

public sealed record MobileSyncResult(string Platform, bool LoginRequired, bool CatalogCovered, int Tasks,
    IReadOnlyList<string> Codes, string Message)
{
    public bool AccountVerified { get; init; }
    public bool FullTaskListsCovered { get; init; }
    public ConnectionReport? Connection { get; init; }
}

public sealed class MobileSync(TaskStore store, MobileVault vault, Func<string, HttpMessageHandler>? isolatedHandler = null,
    ReadOnlyMemory<byte>? syntheticMetadataKey = null,
    Func<string, Uri, IReadOnlyList<MetadataSessionCookie>, IReadOnlyList<MetadataSessionCookie>, CancellationToken, Task>? writeBrowserCookies = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<MobileSyncResult> RunAsync(string platform, bool catalogOnly, IProgress<string>? progress = null, CancellationToken cancellation = default,
        string? verifiedBrowserAccount = null)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            var session = vault.LoadForRevalidation(platform);
            if (session is null)
            {
                var missing = store.SaveConnectionReport(new(platform) { SessionAvailable = false, LoginRequired = true, CatalogOnly = catalogOnly }, cancellation);
                return new(platform, true, false, 0, [], "请先完成官方登录。") { Connection = missing };
            }
            catalogOnly |= platform != "uai" && !store.LoadCourseRecording(platform).SelectionConfirmed;
            Task<IReadOnlyList<MetadataSessionCookie>> MetadataCookies(Uri address, CancellationToken token)
            {
                session = vault.Validate(session, token);
                return Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(session.Cookies.Where(cookie => !cookie.IsExpired(DateTimeOffset.UtcNow) && cookie.AppliesTo(address)).ToArray());
            }
            async Task<IReadOnlyList<Cookie>> NetCookies(Uri address, CancellationToken token) => (await MetadataCookies(address, token)).Select(cookie => cookie.ToCookie()).ToArray();
            async Task Save(Uri address, IReadOnlyList<MetadataSessionCookie> observed, IReadOnlyList<MetadataSessionCookie> changes, CancellationToken token)
            {
                session = vault.Renew(session, observed, changes, token);
                if (changes.Count > 0 && writeBrowserCookies is not null)
                {
                    await writeBrowserCookies(platform, address, observed, changes, token);
                    session = vault.Validate(session, token);
                }
            }
            Task SaveNet(Uri address, IReadOnlyList<Cookie> observed, IReadOnlyList<ChaoxingSessionCookie> changes, CancellationToken token) => Save(address,
                observed.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.Expires == DateTime.MinValue ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()))).ToArray(),
                changes.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray(), token);
            Task Validate(CancellationToken token)
            {
                session = vault.Validate(session, token);
                return Task.CompletedTask;
            }
            MobileSyncResult result;
            if (platform == "uai")
            {
                async Task<(string Token, string OpenId, long? School)> PortalSession(CancellationToken token)
                { await Validate(token); return (session.Token!, session.AccountId!, session.SchoolId); }
                using var reader = new UaiPortalReader(PortalSession, isolatedHandler?.Invoke(platform));
                var profileResponse = await reader.ReadAsync(UaiPortal.ProfilePath, cancellation);
                var profile = profileResponse.Outcome == "ok" ? UaiPortal.Profile(profileResponse.Json, session.AccountId!, session.SchoolId) : new UaiPortalProfile(profileResponse.Outcome);
                var problems = new List<string>(); var loginRequired = profile.Outcome == "login_required";
                int? resources = null, total = null, displayed = null; bool? moduleAllowed = null;
                if (profile.Outcome == "ok")
                {
                    session = vault.BindUaiSchool(session, profile.SchoolId!.Value, cancellation);
                    var modules = await reader.ReadAsync(UaiPortal.ModulesPath, cancellation);
                    var permissions = UaiPortal.Modules(modules.Json); moduleAllowed = permissions.Allowed;
                    if (modules.Outcome == "login_required") loginRequired = true;
                    else if (modules.Outcome != "ok" || permissions.Outcome != "ok") problems.Add("UAI003");
                    if (!loginRequired)
                    {
                        var shelfResponse = await reader.ReadAsync(UaiPortal.ShelfPath, cancellation);
                        var shelf = UaiPortal.Shelf(shelfResponse.Json); resources = shelf.Resources;
                        if (shelfResponse.Outcome == "login_required") loginRequired = true;
                        else if (shelfResponse.Outcome != "ok" || shelf.Outcome != "preview") problems.Add("UAI003");
                    }
                    if (!catalogOnly && !loginRequired)
                    {
                        var homeworkResponse = await reader.ReadAsync(UaiPortal.HomeworkPath, cancellation);
                        var overview = UaiPortal.Homework(homeworkResponse.Json); total = overview.Total; displayed = overview.Displayed;
                        if (homeworkResponse.Outcome == "login_required") loginRequired = true;
                        else if (homeworkResponse.Outcome != "ok" || overview.Outcome is not ("preview" or "overview_truncated")) problems.Add("UAI003");
                        if (overview.Outcome == "overview_truncated") problems.Add("UAI005");
                    }
                }
                else problems.Add(loginRequired ? "UAI001" : profile.Outcome == "temporarily_unavailable" ? "UAI002" : "UAI003");
                problems.Add("UAI004"); if (loginRequired) problems.Add("UAI001");
                var codes = problems.Distinct().ToArray();
                store.SaveUaiPortalSummary(new(DateTimeOffset.UtcNow, loginRequired ? "login_required" : "partial", profile.Outcome == "ok" && !loginRequired,
                    resources, total, displayed, moduleAllowed, codes), cancellation);
                var message = loginRequired ? "U 校园 AI 版会话需要在应用内重新登录。"
                    : profile.Outcome != "ok" ? "官网账号尚未通过校验，请返回应用内官方页面确认。"
                    : "官网账号已校验。" + (resources is { } count ? "书架资源 " + count + " 项（尚未确认为教学班）。" : "书架读取未完成。")
                        + (total is { } taskCount ? "官网首页作业总数 " + taskCount + "，展示 " + displayed + " 条。" : "") + UaiPortal.CoverageText;
                result = new(platform, loginRequired, false, 0, codes, message)
                {
                    AccountVerified = profile.Outcome == "ok" && !loginRequired,
                    Connection = new(platform) { LoginRequired = loginRequired, AccountVerified = profile.Outcome == "ok" && !loginRequired,
                        CatalogOnly = catalogOnly, ProblemCodes = codes, ProblemOutcomes = profile.Outcome == "ok" ? [] : [profile.Outcome] }
                };
            }
            else if (platform is "zhihuishu" or "ucampus" or "pta")
            {
                using var reader = new MetadataHttpReader(MetadataPlatform.Get(platform), MetadataCookies, isolatedHandler?.Invoke(platform), saveResponseCookies: Save,
                    readToken: async token => { await Validate(token); return session.Token; }, testMetadataKey: platform == "zhihuishu" ? syntheticMetadataKey : null);
                reader.ReadCompleted += (request, response) =>
                {
                    if (request.Operation != "profile" || response.Outcome != "ok") return;
                    var account = platform == "pta" ? MetadataPlatformParser.PtaAccount(response.Json) : response.AccountId;
                    if (account is not null) session = vault.BindAccount(session, account, cancellation);
                };
                var summary = await new MetadataSynchronizer(MetadataPlatform.Get(platform), store, reader, Validate).RunAsync(progress, cancellation, catalogOnly);
                var codes = summary.Problems.Select(problem => problem.Code).Distinct().ToArray();
                var outcome = summary.LoginRequired ? "login_required" : summary.Problems.Count == 0 && summary.CourseListCovered && summary.FullTaskListsCovered
                    && summary.CoursesRead == summary.SelectedCourses && (platform != "ucampus" || summary.LearningUnitListsCovered) ? "completed" : "partial";
                if (!catalogOnly) store.RecordMetadataSyncResult(summary, outcome, DateTimeOffset.UtcNow, cancellation);
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message)))
                    { AccountVerified = summary.AccountVerified, FullTaskListsCovered = !catalogOnly && outcome == "completed",
                        Connection = ConnectionReports.From(summary, store.LoadCourseRecording(platform), catalogOnly) };
            }
            else if (platform is "yuketang" or "changjiang")
            {
                var rain = RainPlatform.Find(platform); var host = rain.DefaultHost;
                using var reader = new RainHttpReader(rain, host, NetCookies, isolatedHandler?.Invoke(platform), saveResponseCookies: SaveNet);
                var summary = await new RainSynchronizer(store, reader, rain, host, Validate).RunAsync(progress, cancellation, catalogOnly);
                var codes = summary.Problems.Select(problem => problem.Code).Distinct().ToArray();
                if (!catalogOnly) store.RecordPlatformSyncResult(summary, summary.LoginRequired ? "login_required" : codes.Length == 0 && summary.CourseListCovered && summary.CoursesRead == (summary.SelectedCourses ?? summary.Courses) ? "completed" : "partial", DateTimeOffset.UtcNow, cancellation);
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message)))
                    { AccountVerified = summary.AccountVerified,
                        FullTaskListsCovered = !catalogOnly && !summary.LoginRequired && codes.Length == 0 && summary.CourseListCovered && summary.CoursesRead == (summary.SelectedCourses ?? summary.Courses),
                        Connection = ConnectionReports.From(summary, store.LoadCourseRecording(platform), catalogOnly) };
            }
            else if (platform == "xuetang")
            {
                async Task<IReadOnlyList<XuetangSessionCookie>> Cookies(Uri uri, CancellationToken token) => (await MetadataCookies(uri, token)).Select(cookie => new XuetangSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray();
                Task SaveCookies(Uri uri, IReadOnlyList<XuetangSessionCookie> observed, IReadOnlyList<XuetangSessionCookie> changed, CancellationToken token) => Save(uri,
                    observed.Select(ConvertCookie).ToArray(), changed.Select(ConvertCookie).ToArray(), token);
                using var reader = new XuetangHttpReader(Cookies, isolatedHandler?.Invoke(platform), saveResponseCookies: SaveCookies);
                Task BindAccount(string account, CancellationToken token)
                { session = vault.BindAccount(session, account, token); return Task.CompletedTask; }
                var summary = await new XuetangSynchronizer(store, reader, Validate, BindAccount).RunAsync(progress, cancellation, catalogOnly);
                var codes = summary.Problems.Select(problem => problem.Code).Distinct().ToArray();
                if (!catalogOnly) store.RecordXuetangSyncResult(summary, summary.LoginRequired ? "login_required" : codes.Length == 0 ? "completed" : "partial", DateTimeOffset.UtcNow, cancellation);
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message)))
                    { AccountVerified = summary.AccountVerified, FullTaskListsCovered = !catalogOnly && !summary.LoginRequired && codes.Length == 0 && summary.CourseListCovered && summary.CoursesRead == summary.SelectedCourses,
                        Connection = ConnectionReports.From(summary, store.LoadCourseRecording(platform), catalogOnly) };
            }
            else if (platform == "mooc")
            {
                if (!MoocPlatform.IsId(session.AccountId))
                {
                    var unknown = store.SaveConnectionReport(new(platform) { CatalogOnly = catalogOnly, ProblemCodes = ["MOOC003"], ProblemOutcomes = ["unrecognized_user"] }, cancellation);
                    return new(platform, false, false, 0, ["MOOC003"], "请在登录窗口打开官网首页，再检查接入。") { Connection = unknown };
                }
                async Task<IReadOnlyList<MoocSessionCookie>> Cookies(Uri uri, CancellationToken token) => (await MetadataCookies(uri, token)).Select(cookie => new MoocSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray();
                Task SaveCookies(Uri uri, IReadOnlyList<MoocSessionCookie> observed, IReadOnlyList<MoocSessionCookie> changed, CancellationToken token) => Save(uri,
                    observed.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray(),
                    changed.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray(), token);
                using var reader = new MoocHttpReader(Cookies, isolatedHandler?.Invoke(platform), saveResponseCookies: SaveCookies);
                var summary = await new MoocSynchronizer(store, reader, session.AccountId!, Validate).RunAsync(progress, cancellation, catalogOnly);
                summary = summary with { AccountVerified = !summary.LoginRequired && (summary.AccountVerified
                    || MoocPlatform.IsId(verifiedBrowserAccount) && verifiedBrowserAccount == session.AccountId) };
                var codes = summary.Problems.Select(problem => problem.Code).Distinct().ToArray();
                if (!catalogOnly) store.RecordMoocSyncResult(summary, summary.LoginRequired ? "login_required" : codes.Length == 0 ? "completed" : "partial", DateTimeOffset.UtcNow, cancellation);
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message)))
                    { AccountVerified = summary.AccountVerified,
                        FullTaskListsCovered = !catalogOnly && !summary.LoginRequired && codes.Length == 0 && summary.CourseListCovered && summary.CoursesRead == (summary.SelectedCourses ?? summary.Courses),
                        Connection = ConnectionReports.From(summary, store.LoadCourseRecording(platform), catalogOnly) };
            }
            else if (platform == "chaoxing")
            {
                using var reader = new ChaoxingHttpReader(NetCookies, isolatedHandler?.Invoke(platform), saveResponseCookies: SaveNet);
                var summary = await new ChaoxingSynchronizer(store, new ValidatingChaoxingReader(reader, Validate)).RunAsync(new Progress<ChaoxingSyncProgress>(value => progress?.Report(value.Message)), cancellation, catalogOnly);
                var codes = summary.Problems.Select(problem => problem.Code).Distinct().ToArray();
                if (!catalogOnly) store.RecordSyncResult(summary, summary.LoginRequired ? "login_required" : codes.Length == 0 ? "completed" : "partial", DateTimeOffset.UtcNow, cancellation);
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message)))
                    { AccountVerified = summary.AccountVerified,
                        FullTaskListsCovered = !catalogOnly && !summary.LoginRequired && codes.Length == 0 && summary.CourseListCovered && summary.CoursesRead == (summary.SelectedCourses ?? summary.Courses),
                        Connection = ConnectionReports.From(summary, store.LoadCourseRecording(platform), catalogOnly) };
            }
            else throw new TaskValidationException("SYNC003", "该平台的同步适配器尚未接入。");
            if (!catalogOnly && !result.LoginRequired && !result.FullTaskListsCovered && platform != "uai")
                result = result with { Message = result.Message + "\n本轮覆盖未完整确认，已有任务保留；请到官网核对。" };
            vault.Finish(session, result.LoginRequired, result.AccountVerified, cancellation);
            store.RecordCourseCatalogResult(platform, CourseRecordingRules.CatalogRefreshOutcome(result.CatalogCovered, result.LoginRequired, result.Codes), result.Codes, DateTimeOffset.UtcNow, cancellation);
            if (result.Connection is { } connection) result = result with { Connection = store.SaveConnectionReport(connection, cancellation) };
            return result;
        }
        finally { gate.Release(); }
    }
    private static MetadataSessionCookie ConvertCookie(XuetangSessionCookie cookie) => new(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite);
    private static string Format(int tasks, IEnumerable<string> messages) => "本次读取 " + tasks + " 条任务。" + string.Join("\n", messages.Take(4));

    public async Task<TaskLinkResolution> ResolveTaskAsync(string platform, Uri address, CancellationToken cancellation)
    {
        if (platform is not ("ucampus" or "chaoxing")) return new("ready", "entry", address.AbsoluteUri);
        await gate.WaitAsync(cancellation);
        try
        {
            var session = vault.LoadForRevalidation(platform);
            if (session is null) return new("login_required", "none");
            Task<IReadOnlyList<MetadataSessionCookie>> Cookies(Uri uri, CancellationToken token)
            {
                session = vault.Validate(session, token);
                return Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(session.Cookies.Where(cookie => !cookie.IsExpired(DateTimeOffset.UtcNow) && cookie.AppliesTo(uri)).ToArray());
            }
            Task Save(Uri uri, IReadOnlyList<MetadataSessionCookie> observed, IReadOnlyList<MetadataSessionCookie> changed, CancellationToken token)
            { session = vault.Renew(session, observed, changed, token); return Task.CompletedTask; }
            TaskLinkResolution result;
            if (platform == "ucampus")
            {
                using var reader = new MetadataHttpReader(MetadataPlatform.Ucampus, Cookies, isolatedHandler?.Invoke(platform), saveResponseCookies: Save);
                reader.ReadCompleted += (request, response) =>
                {
                    if (request.Operation == "profile" && response.Outcome == "ok" && response.AccountId is { } account)
                        session = vault.BindAccount(session, account, cancellation);
                };
                result = await reader.ResolveUcampusAsync(address.AbsoluteUri, cancellation);
            }
            else
            {
                async Task<IReadOnlyList<Cookie>> NetCookies(Uri uri, CancellationToken token) => (await Cookies(uri, token)).Select(cookie => cookie.ToCookie()).ToArray();
                Task SaveNet(Uri uri, IReadOnlyList<Cookie> observed, IReadOnlyList<ChaoxingSessionCookie> changed, CancellationToken token) => Save(uri,
                    observed.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly,
                        cookie.Expires == DateTime.MinValue ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()))).ToArray(),
                    changed.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray(), token);
                using var reader = new ChaoxingHttpReader(NetCookies, isolatedHandler?.Invoke(platform), saveResponseCookies: SaveNet);
                result = await reader.ResolveTaskAddressAsync(address, cancellation);
            }
            session = vault.Validate(session, cancellation);
            if (result.Outcome == "login_required") vault.Finish(session, true, false, cancellation);
            return result;
        }
        finally { gate.Release(); }
    }

    private sealed class ValidatingChaoxingReader(IChaoxingPageReader reader, Func<CancellationToken, Task> validate) : IChaoxingPageReader
    {
        public async Task<ChaoxingPage> ReadAsync(Uri address, CancellationToken cancellation)
        { await validate(cancellation); var page = await reader.ReadAsync(address, cancellation); await validate(cancellation); return page; }
    }
}
