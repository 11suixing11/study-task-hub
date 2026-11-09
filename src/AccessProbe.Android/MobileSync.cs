using AccessProbe;
using System.Net;

namespace AccessProbe.Mobile;

public sealed record MobileSyncResult(string Platform, bool LoginRequired, bool CatalogCovered, int Tasks,
    IReadOnlyList<string> Codes, string Message)
{
    public bool AccountVerified { get; init; }
}

public sealed class MobileSync(TaskStore store, MobileVault vault, Func<string, HttpMessageHandler>? isolatedHandler = null,
    ReadOnlyMemory<byte>? syntheticMetadataKey = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<MobileSyncResult> RunAsync(string platform, bool catalogOnly, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            var session = vault.LoadForRevalidation(platform);
            if (session is null) return new(platform, true, false, 0, [], "请先完成官方登录。");
            catalogOnly |= !store.LoadCourseRecording(platform).SelectionConfirmed;
            Task<IReadOnlyList<MetadataSessionCookie>> MetadataCookies(Uri address, CancellationToken token)
            {
                session = vault.Validate(session, token);
                return Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(session.Cookies.Where(cookie => !cookie.IsExpired(DateTimeOffset.UtcNow) && cookie.AppliesTo(address)).ToArray());
            }
            async Task<IReadOnlyList<Cookie>> NetCookies(Uri address, CancellationToken token) => (await MetadataCookies(address, token)).Select(cookie => cookie.ToCookie()).ToArray();
            Task Save(Uri address, IReadOnlyList<MetadataSessionCookie> observed, IReadOnlyList<MetadataSessionCookie> changes, CancellationToken token)
            { session = vault.Renew(session, observed, changes, token); return Task.CompletedTask; }
            Task SaveNet(Uri address, IReadOnlyList<Cookie> observed, IReadOnlyList<ChaoxingSessionCookie> changes, CancellationToken token) => Save(address,
                observed.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.Expires == DateTime.MinValue ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()))).ToArray(),
                changes.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray(), token);
            Task Validate(CancellationToken token)
            {
                session = vault.Validate(session, token);
                return Task.CompletedTask;
            }
            MobileSyncResult result;
            if (platform is "zhihuishu" or "ucampus" or "pta")
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
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message))) { AccountVerified = summary.AccountVerified };
            }
            else if (platform is "yuketang" or "changjiang")
            {
                var rain = RainPlatform.Find(platform); var host = rain.DefaultHost;
                using var reader = new RainHttpReader(rain, host, NetCookies, isolatedHandler?.Invoke(platform), saveResponseCookies: SaveNet);
                var summary = await new RainSynchronizer(store, reader, rain, host, Validate).RunAsync(progress, cancellation, catalogOnly);
                var codes = summary.Problems.Select(problem => problem.Code).Distinct().ToArray();
                if (!catalogOnly) store.RecordPlatformSyncResult(summary, summary.LoginRequired ? "login_required" : codes.Length == 0 && summary.CourseListCovered && summary.CoursesRead == (summary.SelectedCourses ?? summary.Courses) ? "completed" : "partial", DateTimeOffset.UtcNow, cancellation);
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message)));
            }
            else if (platform == "xuetang")
            {
                async Task<IReadOnlyList<XuetangSessionCookie>> Cookies(Uri uri, CancellationToken token) => (await MetadataCookies(uri, token)).Select(cookie => new XuetangSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray();
                Task SaveCookies(Uri uri, IReadOnlyList<XuetangSessionCookie> observed, IReadOnlyList<XuetangSessionCookie> changed, CancellationToken token) => Save(uri,
                    observed.Select(ConvertCookie).ToArray(), changed.Select(ConvertCookie).ToArray(), token);
                using var reader = new XuetangHttpReader(Cookies, isolatedHandler?.Invoke(platform), saveResponseCookies: SaveCookies);
                var summary = await new XuetangSynchronizer(store, reader, Validate).RunAsync(progress, cancellation, catalogOnly);
                var codes = summary.Problems.Select(problem => problem.Code).Distinct().ToArray();
                if (!catalogOnly) store.RecordXuetangSyncResult(summary, summary.LoginRequired ? "login_required" : codes.Length == 0 ? "completed" : "partial", DateTimeOffset.UtcNow, cancellation);
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message)));
            }
            else if (platform == "mooc")
            {
                if (!MoocPlatform.IsId(session.AccountId)) return new(platform, false, false, 0, ["MOOC003"], "请在登录窗口打开官网首页，再点击“登录完成并读取课程”。");
                async Task<IReadOnlyList<MoocSessionCookie>> Cookies(Uri uri, CancellationToken token) => (await MetadataCookies(uri, token)).Select(cookie => new MoocSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray();
                Task SaveCookies(Uri uri, IReadOnlyList<MoocSessionCookie> observed, IReadOnlyList<MoocSessionCookie> changed, CancellationToken token) => Save(uri,
                    observed.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray(),
                    changed.Select(cookie => new MetadataSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite)).ToArray(), token);
                using var reader = new MoocHttpReader(Cookies, isolatedHandler?.Invoke(platform), saveResponseCookies: SaveCookies);
                var summary = await new MoocSynchronizer(store, reader, session.AccountId!, Validate).RunAsync(progress, cancellation, catalogOnly);
                var codes = summary.Problems.Select(problem => problem.Code).Distinct().ToArray();
                if (!catalogOnly) store.RecordMoocSyncResult(summary, summary.LoginRequired ? "login_required" : codes.Length == 0 ? "completed" : "partial", DateTimeOffset.UtcNow, cancellation);
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message)));
            }
            else
            {
                using var reader = new ChaoxingHttpReader(NetCookies, isolatedHandler?.Invoke(platform), saveResponseCookies: SaveNet);
                var summary = await new ChaoxingSynchronizer(store, new ValidatingChaoxingReader(reader, Validate)).RunAsync(new Progress<ChaoxingSyncProgress>(value => progress?.Report(value.Message)), cancellation, catalogOnly);
                var codes = summary.Problems.Select(problem => problem.Code).Distinct().ToArray();
                if (!catalogOnly) store.RecordSyncResult(summary, summary.LoginRequired ? "login_required" : codes.Length == 0 ? "completed" : "partial", DateTimeOffset.UtcNow, cancellation);
                result = new(platform, summary.LoginRequired, summary.CourseListCovered, summary.Tasks, codes, Format(summary.Tasks, summary.Problems.Select(problem => problem.Message)));
            }
            if (platform is not ("pta" or "ucampus" or "zhihuishu")) result = result with { AccountVerified = !result.LoginRequired && result.CatalogCovered };
            vault.Finish(session, result.LoginRequired, result.AccountVerified, cancellation);
            store.RecordCourseCatalogResult(platform, CourseRecordingRules.CatalogRefreshOutcome(result.CatalogCovered, result.LoginRequired, result.Codes), result.Codes, DateTimeOffset.UtcNow, cancellation);
            return result;
        }
        finally { gate.Release(); }
    }
    private static MetadataSessionCookie ConvertCookie(XuetangSessionCookie cookie) => new(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite);
    private static string Format(int tasks, IEnumerable<string> messages) => "本次读取 " + tasks + " 条任务。" + string.Join("\n", messages.Take(4));

    private sealed class ValidatingChaoxingReader(IChaoxingPageReader reader, Func<CancellationToken, Task> validate) : IChaoxingPageReader
    {
        public async Task<ChaoxingPage> ReadAsync(Uri address, CancellationToken cancellation)
        { await validate(cancellation); var page = await reader.ReadAsync(address, cancellation); await validate(cancellation); return page; }
    }
}
