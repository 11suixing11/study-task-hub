using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MoocSyncHarness;

internal static class SessionRestartChecks
{
    private const string Marker = "synthetic-mooc-session-restart-only";

    public static async Task<int> VerifyAsync(string runRoot)
    {
        var root = Path.Combine(runRoot, "session-restart"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "harness.marker"), Marker);
        var reports = new List<JsonElement>();
        foreach (var phase in new[] { "seed", "recover", "clear", "after-clear", "seed", "reject", "after-reject" })
        {
            var report = Path.Combine(root, $"phase-{reports.Count + 1}-{phase}.json");
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Harness executable unavailable.");
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(SessionRestartChecks).Assembly.Location);
            foreach (var argument in new[] { "--session-child", phase, root, report }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Restart child did not start.");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            if (!File.Exists(report)) throw new InvalidOperationException("Missing restart report: " + phase);
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            if (child.ExitCode != 0) throw new InvalidOperationException("Restart " + phase + ": " + json.RootElement.GetProperty("failure").GetString());
            reports.Add(json.RootElement.Clone());
        }
        var checks = reports.Sum(report => report.GetProperty("checks").GetInt32());
        File.WriteAllText(Path.Combine(runRoot, "mooc-session-restart-checks.json"), JsonSerializer.Serialize(new
        { outcome = "passed", checks, processStarts = reports.Count, browserProcessExitedBetweenPhases = true,
            data = "synthetic_only", network = "local_handler_and_webview_fixture_only", phases = reports }, new JsonSerializerOptions { WriteIndented = true }));
        return checks;
    }

    [STAThread]
    public static int RunChild(string phase, string root, string reportPath)
    {
        root = Path.GetFullPath(root); reportPath = Path.GetFullPath(reportPath);
        if (phase is not ("seed" or "recover" or "clear" or "after-clear" or "reject" or "after-reject")
            || !File.Exists(Path.Combine(root, "harness.marker")) || File.ReadAllText(Path.Combine(root, "harness.marker")) != Marker
            || Path.GetDirectoryName(reportPath) != root) return 2;
        Environment.SetEnvironmentVariable("TEMP", root); Environment.SetEnvironmentVariable("TMP", root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var browser = new WebView2 { Width = 1, Height = 1, Visibility = Visibility.Hidden };
        var fixture = new Fixtures { ExpectedToken = phase == "seed" ? "synthetic-seed" : "synthetic-renewed", RenewNext = phase == "seed", RejectRpc = phase == "reject" };
        fixture.BlockBrowser(browser);
        var window = new Window { Title = "MOOC 会话重启合成验收", Width = 320, Height = 140, Content = browser, ShowInTaskbar = false };
        var store = new TaskStore(Path.Combine(root, "tasks.db")); store.SaveSettings(new(false, true)); store.SaveMoocSettings(new() { Connected = true });
        var service = new MoocSyncService(store, root, browser, fixture.CreateHandler);
        var archive = new MoocSessionStore(Path.Combine(root, ".local", "sessions", "mooc.bin"));
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
                    Fixtures.SetCookie(browser, "synthetic-seed");
                    var persistent = manager.CreateCookie("SYNTHETIC_LONG", "synthetic-persistent", "www.icourse163.org", "/");
                    persistent.IsSecure = true; persistent.Expires = DateTime.UtcNow.AddHours(8); manager.AddOrUpdateCookie(persistent);
                    manager.AddOrUpdateCookie(manager.CreateCookie("SYNTHETIC_DELETE", "synthetic-delete", ".icourse163.org", "/"));
                    manager.AddOrUpdateCookie(manager.CreateCookie("SYNTHETIC_OTHER", "synthetic-unrelated", "other.example", "/"));
                    Check((await manager.GetCookiesAsync("")).Single(cookie => cookie.Name == Fixtures.AuthName).IsSession, "seed uses real temporary WebView cookie");
                }
                else if (phase is "recover" or "clear" or "reject")
                {
                    Check(recovery == "restored", "startup restores missing session after browser process exits");
                    var cookies = await manager.GetCookiesAsync(""); var auth = cookies.Single(cookie => cookie.Name == Fixtures.AuthName);
                    Check(auth.Value == "synthetic-renewed" && auth.IsSession && auth.IsSecure && auth.IsHttpOnly && auth.SameSite == CoreWebView2CookieSameSiteKind.Strict,
                        "restored renewal preserves session expiry and security flags");
                    Check(cookies.Any(cookie => cookie.Name == "SYNTHETIC_LONG" && !cookie.IsSession), "persistent cookie remains available");
                    Check(cookies.All(cookie => cookie.Name != "SYNTHETIC_DELETE"), "deleted response cookie never returns"); cookies.Clear();
                }
                else
                {
                    Check(recovery == "not_saved", "cleared or rejected archive is absent on next process");
                    var cookies = await manager.GetCookiesAsync("");
                    Check(cookies.All(cookie => cookie.Name != Fixtures.AuthName), "temporary login cookie absent after full process exit"); cookies.Clear();
                }
                if (phase == "clear")
                {
                    await service.ClearSessionAsync(); Check(archive.Load(DateTimeOffset.UtcNow).Count == 0, "explicit reconnect removes encrypted archive");
                    Check((await manager.GetCookiesAsync("")).Count == 0, "explicit reconnect clears only own profile");
                    Check(store.LoadTasks().Count == 8, "explicit reconnect preserves tasks");
                }
                else
                {
                    var completed = new TaskCompletionSource<MoocSyncSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
                    service.Completed += summary => completed.TrySetResult(summary); service.StartAutomatic();
                    var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)); await Task.Delay(25);
                    Check(!service.IsRunning, "startup automatic sync finishes without clicks");
                    Check(store.LoadTasks().Count == 8, "one task set persists through restarts without duplication");
                    if (phase is "seed" or "recover")
                    {
                        Check(!summary.LoginRequired && summary.CourseListCovered && summary.CoursesRead == 2 && summary.Tasks == 8 && summary.Problems.Count == 0,
                            "restored session authenticates both course terms and new exam metadata");
                        Check(store.LoadMoocSettings().LastSyncOutcome == "completed", "successful restart result stored");
                        var cookies = await manager.GetCookiesAsync("");
                        Check(cookies.Single(cookie => cookie.Name == Fixtures.AuthName).Value == "synthetic-renewed", "HTTP auth renewal written to WebView");
                        Check(cookies.All(cookie => cookie.Name != "SYNTHETIC_DELETE"), "HTTP cookie deletion written to WebView"); cookies.Clear();
                        var saved = archive.Load(DateTimeOffset.UtcNow);
                        Check(saved.Any(cookie => cookie.Name == Fixtures.AuthName && cookie.ExpiresUtc is null && cookie.SameSite == "Strict"), "encrypted snapshot preserves session flag and renewed auth");
                        Check(saved.All(cookie => MoocPlatform.IsCookieDomain(cookie.Domain.TrimStart('.'))), "unrelated domains excluded from archive");
                    }
                    else
                    {
                        Check(summary.LoginRequired && store.LoadMoocSettings().LastSyncOutcome == "login_required", "official rejection remains login-required after restart");
                        Check(archive.Load(DateTimeOffset.UtcNow).Count == 0, "official rejection never saves stale archive");
                        Check(phase != "reject" || fixture.Requests == 1, "RPC rejection stops all further metadata requests");
                    }
                }
                Check(await service.SaveForExitAsync(), "graceful exit persists current state"); persistence = service.SessionPersistenceOutcome;
                Check(phase is "seed" or "recover" ? archive.Load(DateTimeOffset.UtcNow).Count > 0 : archive.Load(DateTimeOffset.UtcNow).Count == 0,
                    "exit cannot resurrect cleared or rejected session");
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
                    catch (TimeoutException) { failure ??= "Browser process did not exit between restart phases."; }
                }
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                { phase, outcome = failure is null ? "passed" : "failed", checks, requests = fixture.Requests, recovery, persistence,
                    browserProcessExited = exited, data = "synthetic_only", network = "local_handler_and_webview_fixture_only", failure }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(failure is null ? 0 : 1);
            }
        };
        app.Run(window); return failure is null && exited ? 0 : 1;
    }
}
