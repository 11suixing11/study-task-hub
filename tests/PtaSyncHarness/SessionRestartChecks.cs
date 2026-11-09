using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using F = PlatformMetadataTestData.PtaFixtures;

namespace PtaSyncHarness;

internal static class SessionRestartChecks
{
    private const string Marker = "synthetic-pta-session-restart-only";
    public static async Task<int> VerifyAsync(string runRoot)
    {
        var reports = new List<JsonElement>(); var root = Path.Combine(runRoot, "session-restart");
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "harness.marker"), Marker);
        foreach (var phase in new[] { "seed", "recover", "clear", "after-clear", "seed", "reject", "after-reject", "relogin", "recover" })
        {
            var reportPath = Path.Combine(root, "phase-" + (reports.Count + 1) + "-" + phase + ".json");
            var executable = Environment.ProcessPath!;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(SessionRestartChecks).Assembly.Location);
            foreach (var argument in new[] { "--session-child", phase, root, reportPath }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("PTA session child could not start.");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            if (child.ExitCode != 0) throw new InvalidOperationException("PTA " + phase + ": " + document.RootElement.GetProperty("failure").GetString());
            reports.Add(document.RootElement.Clone());
        }
        var checks = reports.Sum(report => report.GetProperty("checks").GetInt32());
        File.WriteAllText(Path.Combine(runRoot, "pta-session-restart-checks.json"), JsonSerializer.Serialize(new
        { outcome = "passed", checks, processStarts = reports.Count, browserProcessExitedBetweenPhases = true,
            data = "synthetic_only", network = "local_handler_and_webview_fixture_only", phases = reports }, new JsonSerializerOptions { WriteIndented = true }));
        return checks;
    }
    [STAThread]
    public static int RunChild(string phase, string root, string reportPath)
    {
        root = Path.GetFullPath(root); reportPath = Path.GetFullPath(reportPath);
        if (!File.Exists(Path.Combine(root, "harness.marker")) || File.ReadAllText(Path.Combine(root, "harness.marker")) != Marker
            || Path.GetDirectoryName(reportPath) != root || phase is not ("seed" or "recover" or "clear" or "after-clear" or "reject" or "after-reject" or "relogin")) return 2;
        Environment.SetEnvironmentVariable("TEMP", root); Environment.SetEnvironmentVariable("TMP", root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var store = FixtureTaskStore.Create(Path.Combine(root, "tasks.db")); store.SaveSettings(new(false, true));
        store.SaveMetadataSettings("pta", new() { Connected = true });
        var browser = new WebView2 { Visibility = Visibility.Hidden, Width = 1, Height = 1 }; BrowserFixtures.BlockNetwork(browser);
        if (phase == "recover") browser.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) return;
            var manager = browser.CoreWebView2.CookieManager;
            var cookie = manager.CreateCookie("JSESSIONID", "synthetic-existing-server-cookie", "pintia.cn", "/");
            cookie.IsHttpOnly = true; manager.AddOrUpdateCookie(cookie);
        };
        var fixture = new F.Handler { Session = phase is "seed" or "relogin" ? "synthetic-pta-session" : "synthetic-pta-session-renewed",
            RenewNext = phase is "seed" or "relogin", RejectLogin = phase == "reject" };
        var service = new MetadataSyncService(MetadataPlatform.Pta, store, root, browser, () => fixture);
        var archive = new MetadataSessionStore(MetadataPlatform.Pta, Path.Combine(root, "pta.bin"), store);
        var window = new Window { Content = browser, ShowInTaskbar = false, Title = "PTA · 合成会话重启验证", Width = 320, Height = 140 };
        var checks = 0; string? failure = null, recovery = null, persistence = null; var exited = false;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        window.Loaded += async (_, _) =>
        {
            CoreWebView2Environment? environment = null;
            try
            {
                environment = await service.EnsureSessionAsync(); recovery = service.SessionRecoveryOutcome;
                if (phase is "seed" or "relogin")
                {
                    BrowserFixtures.SetCookie(browser, fixture.Session); await Task.Delay(70);
                    Check((await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Single(cookie => cookie.Name == "PTASession").IsSession,
                        "seed and relogin use browser session cookie");
                }
                else if (phase is "recover" or "clear" or "reject")
                {
                    Check(recovery == "restored", "fresh browser restores repository cookie");
                    var auth = (await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Single(cookie => cookie.Name == "PTASession");
                    Check(auth.Value == "synthetic-pta-session-renewed" && auth.IsSession && auth.IsSecure && auth.IsHttpOnly
                        && auth.SameSite == CoreWebView2CookieSameSiteKind.Strict, "renewed cookie value and flags preserved across exit");
                    if (phase == "recover") Check((await browser.CoreWebView2.CookieManager.GetCookiesAsync(""))
                        .Single(cookie => cookie.Name == "JSESSIONID").Value == "synthetic-existing-server-cookie",
                        "current server cookie permits authentication restore and is preserved across restart");
                }
                else Check(archive.Load(DateTimeOffset.UtcNow).Count == 0 && (await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Count == 0,
                    "expired and cleared login cannot restore stale cookie");
                if (phase == "clear")
                {
                    await service.ClearSessionAsync();
                    Check(archive.Load(DateTimeOffset.UtcNow).Count == 0 && (await browser.CoreWebView2.CookieManager.GetCookiesAsync("")).Count == 0,
                        "reconnect clears protected session and dedicated browser");
                }
                else
                {
                    await service.StartAsync(true);
                    var summary = store.LoadMetadataSettings("pta").LastSummary!;
                    if (phase is "seed" or "recover" or "relogin")
                    {
                        Check(!summary.LoginRequired && summary.Tasks == 6 && summary.AccountVerified, "restored PTA login synchronizes teaching sets");
                        Check(store.LoadTasks().Count == 6 && store.LoadTasks().Count(task => task.State == TaskState.Completed) == 1,
                            "PTA completion evidence and task identity survive restart");
                        Check(archive.Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Value == fixture.Session)
                            && store.LoadPlatformSessionInfo("pta").State == "ready", "valid renewed cookie saved in single repository record");
                    }
                    else
                    {
                        Check(summary.LoginRequired && store.LoadMetadataSettings("pta").LastSyncOutcome == "login_required", "rejected PTA cookie prompts official login");
                        Check(archive.Load(DateTimeOffset.UtcNow).Count == 0 && fixture.Paths.Count == 1, "rejected login stops metadata and prevents restore");
                    }
                }
                Check(store.LoadTasks().Count == 6, "task history retained across all session phases");
                Check(await service.SaveForExitAsync(), "exit saves only current permitted session"); persistence = service.SessionPersistenceOutcome;
                Check(phase is "seed" or "recover" or "relogin" ? archive.Load(DateTimeOffset.UtcNow).Count > 0 : archive.Load(DateTimeOffset.UtcNow).Count == 0,
                    "exit cannot resurrect invalidated login");
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
                    catch (TimeoutException) { failure ??= "PTA browser did not exit between restart phases."; }
                }
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                { platformId = "pta", phase, outcome = failure is null ? "passed" : "failed", checks, requests = fixture.Paths.Count,
                    recovery, persistence, browserProcessExited = exited, failure }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(failure is null ? 0 : 1);
            }
        };
        app.Run(window); return failure is null && exited ? 0 : 1;
    }
}
