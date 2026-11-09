using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Data = PlatformMetadataTestData.Fixtures;
using U = PlatformMetadataTestData.UcampusFixtures;

namespace MetadataSyncHarness;

internal static class SessionRestartChecks
{
    private const string Marker = "synthetic-platform-metadata-restart-only";
    public static async Task<int> VerifyAsync(string runRoot)
    {
        var reports = new List<JsonElement>();
        foreach (var platform in new[] { MetadataPlatform.Ucampus, MetadataPlatform.Zhihuishu })
        {
            var root = Path.Combine(runRoot, "session-restart", platform.Id); Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "harness.marker"), Marker);
            foreach (var phase in new[] { "seed", "recover", "clear", "after-clear", "seed", "reject", "after-reject" })
            {
                var reportPath = Path.Combine(root, "phase-" + (reports.Count + 1) + "-" + phase + ".json");
                var executable = Environment.ProcessPath!;
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(SessionRestartChecks).Assembly.Location);
                foreach (var argument in new[] { "--session-child", phase, root, reportPath }) start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Session child not started.");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
                if (child.ExitCode != 0) throw new InvalidOperationException(platform.Id + " restart " + phase + ": " + document.RootElement.GetProperty("failure").GetString());
                reports.Add(document.RootElement.Clone());
            }
        }
        var checks = reports.Sum(report => report.GetProperty("checks").GetInt32());
        File.WriteAllText(Path.Combine(runRoot, "metadata-session-restart-checks.json"), JsonSerializer.Serialize(new
        { outcome = "passed", checks, processStarts = reports.Count, browserProcessExitedBetweenPhases = true,
            data = "synthetic_only", network = "local_handler_and_webview_fixture_only", phases = reports }, new JsonSerializerOptions { WriteIndented = true }));
        return checks;
    }
    [STAThread]
    public static int RunChild(string phase, string root, string reportPath)
    {
        root = Path.GetFullPath(root); reportPath = Path.GetFullPath(reportPath); var platformId = Path.GetFileName(root);
        if (!MetadataPlatform.Enabled.Any(platform => platform.Id == platformId) || !File.Exists(Path.Combine(root, "harness.marker"))
            || File.ReadAllText(Path.Combine(root, "harness.marker")) != Marker || Path.GetDirectoryName(reportPath) != root
            || phase is not ("seed" or "recover" or "clear" or "after-clear" or "reject" or "after-reject")) return 2;
        Environment.SetEnvironmentVariable("TEMP", root); Environment.SetEnvironmentVariable("TMP", root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var platform = MetadataPlatform.Get(platformId);
        var browser = new WebView2 { Visibility = Visibility.Hidden, Width = 1, Height = 1 }; var fixture = new Fixtures(platform)
        { Session = phase == "seed" ? "synthetic-session-seed" : "synthetic-session-renewed", RenewNext = phase == "seed", RejectLogin = phase == "reject" };
        fixture.BlockBrowser(browser); var window = new Window { Content = browser, Width = 320, Height = 140, ShowInTaskbar = false, Title = "平台会话合成验收" };
        if (phase == "recover") browser.CoreWebView2InitializationCompleted += (_, initialized) =>
        {
            if (!initialized.IsSuccess) return;
            var manager = browser.CoreWebView2.CookieManager;
            var auxiliary = manager.CreateCookie(platformId == "ucampus" ? "hostUrl" : "acw_tc", "synthetic-persistent-route-renewed",
                platformId == "ucampus" ? ".unipus.cn" : "onlineservice-api.zhihuishu.com", "/");
            auxiliary.IsSecure = true; auxiliary.Expires = DateTime.UtcNow.AddDays(1); manager.AddOrUpdateCookie(auxiliary);
        };
        var store = FixtureTaskStore.Create(Path.Combine(root, "tasks.db")); store.SaveSettings(new(false, true)); store.SaveMetadataSettings(platformId, new() { Connected = true });
        var service = new MetadataSyncService(platform, store, root, browser, fixture.CreateHandler, testMetadataKey: platformId == "zhihuishu" ? (ReadOnlyMemory<byte>?)Data.Key : null);
        var archive = new MetadataSessionStore(platform, Path.Combine(root, ".local", "sessions", platformId + ".bin"), store);
        var checks = 0; string? failure = null, recovery = null, persistence = null; var exited = false;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        window.Loaded += async (_, _) =>
        {
            CoreWebView2Environment? environment = null;
            try
            {
                environment = await service.EnsureSessionAsync(); recovery = service.SessionRecoveryOutcome;
                var manager = browser.CoreWebView2.CookieManager;
                if (phase == "seed")
                {
                    fixture.SetCookies(browser); await Task.Delay(50);
                    var auxiliary = manager.CreateCookie(platformId == "ucampus" ? "hostUrl" : "acw_tc", "synthetic-persistent-route-seed",
                        platformId == "ucampus" ? ".unipus.cn" : "onlineservice-api.zhihuishu.com", "/");
                    auxiliary.IsSecure = true; auxiliary.Expires = DateTime.UtcNow.AddDays(1); manager.AddOrUpdateCookie(auxiliary); await Task.Delay(50);
                    if (platformId == "zhihuishu")
                        await service.ObserveMetadataTokenAsync("https://onlinestuh5.zhihuishu.com/", () => Task.FromResult("{\"eligible\":true,\"token\":\"synthetic-restart-token\"}"));
                    Check((await manager.GetCookiesAsync("")).Single(cookie => cookie.Name == fixture.AuthName).IsSession, "seed uses browser session cookies");
                }
                else if (phase is "recover" or "clear" or "reject" or "after-reject")
                {
                    Check(recovery == "restored", "dedicated platform archive restored in fresh process");
                    var auth = (await manager.GetCookiesAsync("")).Single(cookie => cookie.Name == fixture.AuthName);
                    Check(auth.Value == "synthetic-session-renewed" && auth.IsSession && auth.IsSecure && auth.IsHttpOnly && auth.SameSite == CoreWebView2CookieSameSiteKind.Strict,
                        "session renewal and cookie flags survive browser exit");
                    if (phase == "after-reject") Check(archive.Load(DateTimeOffset.UtcNow).Count == 0
                        && archive.LoadForRevalidation(DateTimeOffset.UtcNow).Count > 0, "retained credentials remain unverified before official account recovery");
                    if (platformId == "zhihuishu") { archive.LoadForRevalidation(DateTimeOffset.UtcNow); Check(archive.Token == "synthetic-restart-token", "official token bound and protected across restart"); }
                }
                else
                {
                    Check(recovery == "not_saved", "cleared archive absent in next process");
                    Check((await manager.GetCookiesAsync("")).All(cookie => cookie.Name != fixture.AuthName), "temporary rejected session absent after browser exit");
                }
                if (phase == "clear")
                {
                    await service.ClearSessionAsync(); Check(archive.Load(DateTimeOffset.UtcNow).Count == 0, "reconnect removes platform snapshot");
                    Check((await manager.GetCookiesAsync("")).Count == 0, "reconnect clears dedicated browser profile");
                }
                else
                {
                    var completed = new TaskCompletionSource<MetadataSyncSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
                    service.Completed += summary => completed.TrySetResult(summary); service.StartAutomatic();
                    var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    if (phase is "seed" or "recover" or "after-reject")
                    {
                        Check(!summary.LoginRequired && summary.Tasks == (platformId == "zhihuishu" ? Data.SchoolExamTaskCount : U.TaskCount), "restored session reports both platform metadata lists");
                        Check(platformId == "zhihuishu" ? summary.CourseListCovered && summary.FullTaskListsCovered && summary.SharedCourses == 2
                            : summary is { Courses: 1, FullTaskListsCovered: true, TasksWithLink: U.TaskCount }, "both platform full lists recover");
                        if (platformId == "zhihuishu") Check(summary is { RoomExamTasks: 2, OfflineExamTasks: 1, MakeupExamTasks: 2, StatusNeedsReview: 1 }, "fresh process resumes school exam metadata with offline uncertainty");
                        else
                        {
                            Check(store.LoadTasks().Count(task => MetadataTaskNavigation.ParseUcampus(task.Link)?.Kind == "exam") == U.ExamTaskCount,
                                "fresh process restores protected legacy exam entries");
                            Check(store.LoadTasks().Where(task => task.PlatformTitle is "合成 U 校园待办" or "合成 U 校园待测")
                                .Select(task => task.CollectedNavigationKey).Distinct().Count() == 2,
                                "homework and exam identity remain separate after process restart");
                        }
                        Check(archive.Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Value == "synthetic-session-renewed"), "HTTP renewed session encrypted");
                        if (phase == "after-reject") Check(store.LoadPlatformSessionInfo(platformId).State == "ready", "official same-account recheck recovers preserved rejected session after restart");
                    }
                    else
                    {
                        Check(summary.LoginRequired && store.LoadMetadataSettings(platformId).LastSyncOutcome == "login_required", "expired session reported explicitly");
                        Check(archive.Load(DateTimeOffset.UtcNow).Count == 0 && (phase == "reject"
                                ? archive.LoadForRevalidation(DateTimeOffset.UtcNow).Count > 0 : archive.LoadForRevalidation(DateTimeOffset.UtcNow).Count == 0)
                            && fixture.Requests == (platformId == "zhihuishu" && phase == "reject" ? 2 : 1),
                            "rejected login stops metadata while preserving encrypted credentials for official revalidation");
                    }
                }
                Check(store.LoadTasks().Count == (platformId == "zhihuishu" ? Data.SchoolExamTaskCount : U.TaskCount), "task history retained across session operations");
                Check(await service.SaveForExitAsync(), "exit saves latest platform state"); persistence = service.SessionPersistenceOutcome;
                Check(phase is "seed" or "recover" or "after-reject" ? archive.Load(DateTimeOffset.UtcNow).Count > 0 : archive.Load(DateTimeOffset.UtcNow).Count == 0,
                    "exit cannot resurrect cleared or rejected login");
            }
            catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
            finally
            {
                var exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (environment is not null) environment.BrowserProcessExited += (_, _) => exit.TrySetResult(true);
                service.Dispose();
                if (environment is not null)
                {
                    try { await exit.Task.WaitAsync(TimeSpan.FromSeconds(10)); exited = true; checks++; }
                    catch (TimeoutException) { failure ??= "Browser process did not exit between phases."; }
                }
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                { platformId, phase, outcome = failure is null ? "passed" : "failed", checks, requests = fixture.Requests, recovery, persistence,
                    browserProcessExited = exited, data = "synthetic_only", network = "local_handler_and_webview_fixture_only", failure }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(failure is null ? 0 : 1);
            }
        };
        app.Run(window); return failure is null && exited ? 0 : 1;
    }
}
