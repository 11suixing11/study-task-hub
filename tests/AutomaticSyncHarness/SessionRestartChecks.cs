using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AutomaticSyncHarness;

internal static class SessionRestartChecks
{
    private const string Marker = "synthetic-session-restart-only";
    private const string AuthName = "SYNTHETIC_AUTH";

    public static async Task<int> VerifyAsync(string runRoot)
    {
        var root = Path.Combine(runRoot, "session-restart");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "harness.marker"), Marker);
        var reports = new List<JsonElement>();
        foreach (var phase in new[] { "seed", "recover", "clear", "after-clear", "seed", "reject", "after-reject" })
        {
            var report = Path.Combine(root, $"phase-{reports.Count + 1}-{phase}.json");
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Harness executable is unavailable.");
            var process = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                process.ArgumentList.Add(typeof(SessionRestartChecks).Assembly.Location);
            process.ArgumentList.Add("--session-child"); process.ArgumentList.Add(phase);
            process.ArgumentList.Add(root); process.ArgumentList.Add(report);
            using var child = Process.Start(process) ?? throw new InvalidOperationException("Session restart child did not start.");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            if (child.ExitCode != 0 || !File.Exists(report)) throw new InvalidOperationException("Session restart phase failed: " + phase);
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            reports.Add(json.RootElement.Clone());
        }
        var checks = reports.Sum(report => report.GetProperty("checks").GetInt32());
        File.WriteAllText(Path.Combine(runRoot, "session-restart-checks.json"), JsonSerializer.Serialize(new
        {
            outcome = "passed", checks, processStarts = reports.Count, browserProcessExitedBetweenPhases = true,
            data = "synthetic_only", network = "local_handler_only", phases = reports
        }, new JsonSerializerOptions { WriteIndented = true }));
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
        browser.CoreWebView2InitializationCompleted += (_, result) =>
        {
            if (!result.IsSuccess) return;
            var core = browser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) => request.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(), 403, "Offline Only", "Cache-Control: no-store\r\n");
        };
        var window = new Window { Title = "学习通会话重启离线验收", Width = 320, Height = 140, Content = browser, ShowInTaskbar = false };
        var store = new TaskStore(Path.Combine(root, "tasks.db"));
        store.SaveSettings(new(false, true));
        var transport = new FixtureHandler(phase);
        var service = new ChaoxingSyncService(store, root, browser, () => transport);
        var checks = 0;
        string? failure = null;
        string? recovery = null;
        string? persistence = null;
        bool exited = false;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks++; }
        window.Loaded += async (_, _) =>
        {
            CoreWebView2Environment? environment = null;
            try
            {
                environment = await service.GetLoginEnvironmentAsync();
                var manager = browser.CoreWebView2.CookieManager;
                var archive = new ChaoxingSessionStore(Path.Combine(root, ".local", "sessions", "chaoxing.bin"));
                recovery = service.SessionRecoveryOutcome;
                if (phase == "seed")
                {
                    var auth = manager.CreateCookie(AuthName, "synthetic-seed", ".chaoxing.com", "/");
                    auth.IsSecure = auth.IsHttpOnly = true; auth.Expires = DateTime.MinValue; auth.SameSite = CoreWebView2CookieSameSiteKind.Strict;
                    manager.AddOrUpdateCookie(auth);
                    var persistent = manager.CreateCookie("SYNTHETIC_LONG", "synthetic-persistent", "i.chaoxing.com", "/");
                    persistent.IsSecure = true; persistent.Expires = DateTime.UtcNow.AddHours(8); persistent.SameSite = CoreWebView2CookieSameSiteKind.Lax;
                    manager.AddOrUpdateCookie(persistent);
                    var removable = manager.CreateCookie("SYNTHETIC_DELETE", "synthetic-delete", ".chaoxing.com", "/");
                    manager.AddOrUpdateCookie(removable);
                    manager.AddOrUpdateCookie(manager.CreateCookie("SYNTHETIC_OTHER", "synthetic-unrelated", "other.example", "/"));
                    var before = await manager.GetCookiesAsync("");
                    Check(before.Single(cookie => cookie.Name == AuthName).IsSession, "seed is a temporary cookie");
                    before.Clear();
                }
                else if (phase is "recover" or "clear" or "reject")
                {
                    Check(service.SessionRecoveryOutcome == "restored", "startup restores missing temporary session");
                    var restored = await manager.GetCookiesAsync("");
                    var auth = restored.Single(cookie => cookie.Name == AuthName);
                    Check(auth.Value == "synthetic-renewed" && auth.IsSession && auth.IsSecure && auth.IsHttpOnly
                        && auth.SameSite == CoreWebView2CookieSameSiteKind.Strict, "restored renewal preserves flags and session expiry");
                    Check(restored.Any(cookie => cookie.Name == "SYNTHETIC_LONG" && !cookie.IsSession), "persistent cookie remains available");
                    Check(restored.All(cookie => cookie.Name != "SYNTHETIC_DELETE"), "deleted response cookie is not restored");
                    restored.Clear();
                }
                else
                {
                    Check(service.SessionRecoveryOutcome == "not_saved", "cleared or rejected session is not replayed");
                    var current = await manager.GetCookiesAsync("");
                    Check(current.All(cookie => cookie.Name != AuthName), "session cookie absent after complete process exit");
                    current.Clear();
                }

                if (phase == "clear")
                {
                    service.SessionClearing();
                    await service.PrepareSessionClearAsync();
                    manager.DeleteAllCookies();
                    await browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
                    service.SessionClearCompleted(true);
                    Check(!File.Exists(Path.Combine(root, ".local", "sessions", "chaoxing.bin")), "explicit clear removes encrypted archive");
                    Check(store.LoadTasks().Count == 1, "explicit clear retains collected tasks");
                }
                else
                {
                    var completed = new TaskCompletionSource<ChaoxingSyncSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
                    service.Completed += result => completed.TrySetResult(result);
                    service.StartAutomatic();
                    var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    await Task.Delay(25);
                    Check(!service.IsRunning, "automatic run completes without user clicks");
                    Check(store.LoadTasks().Count == 1, "automatic sync keeps one task across restarts");
                    if (phase is "seed" or "recover")
                    {
                        Check(!summary.LoginRequired && summary.Courses == 1 && summary.CoursesRead == 1 && summary.Problems.Count == 0,
                            "saved session authenticates automatic course and task collection");
                        Check(store.LoadSettings().LastSyncOutcome == "completed", "successful restart recorded");
                        var current = await manager.GetCookiesAsync("");
                        Check(current.Single(cookie => cookie.Name == AuthName).Value == "synthetic-renewed", "HTTP renewal writes back to WebView");
                        Check(current.All(cookie => cookie.Name != "SYNTHETIC_DELETE"), "HTTP deletion writes back to WebView");
                        current.Clear();
                        var snapshot = archive.Load(DateTimeOffset.UtcNow);
                        Check(snapshot.Any(cookie => cookie.Name == AuthName && cookie.ExpiresUtc is null && cookie.SameSite == "Strict"),
                            "encrypted snapshot preserves temporary renewal");
                        Check(snapshot.All(cookie => ProbePolicy.IsOfficialHost(cookie.Domain.TrimStart('.'))), "unrelated profile cookie excluded");
                    }
                    else
                    {
                        Check(summary.LoginRequired && store.LoadSettings().LastSyncOutcome == "login_required", "official rejection still requires login");
                        Check(archive.Load(DateTimeOffset.UtcNow).Count == 0, "rejected or cleared archive stays unavailable");
                    }
                }
                Check(await service.SaveForExitAsync(), "graceful exit saves current session");
                persistence = service.SessionPersistenceOutcome;
                Check(phase is "seed" or "recover" ? archive.Load(DateTimeOffset.UtcNow).Count > 0
                    : archive.Load(DateTimeOffset.UtcNow).Count == 0, "exit preserves current state without resurrecting cleared sessions");
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
                    catch (TimeoutException) { failure ??= "Browser process did not fully exit between phases."; }
                }
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                {
                    phase, outcome = failure is null ? "passed" : "failed", checks, requests = transport.Requests,
                    recovery, persistence, browserProcessExited = exited, data = "synthetic_only", network = "local_handler_only", failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(failure is null ? 0 : 1);
            }
        };
        app.Run(window);
        return failure is null && exited ? 0 : 1;
    }

    private sealed class FixtureHandler(string phase) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        private string expected = phase == "seed" ? "synthetic-seed" : "synthetic-renewed";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests++;
            var address = request.RequestUri!;
            if (request.Method != HttpMethod.Get || !ChaoxingSyncParser.IsReadAddress(address))
                throw new InvalidOperationException("Unexpected session fixture request.");
            var auth = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values).Split(';')
                .Select(pair => pair.Trim().Split('=', 2)).FirstOrDefault(pair => pair.Length == 2 && pair[0] == AuthName)?[1] : null;
            if (phase is "reject" or "after-reject" or "after-clear" || auth != expected)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                if (phase == "reject") response.Headers.TryAddWithoutValidation("Set-Cookie", AuthName + "=; Domain=chaoxing.com; Path=/; Max-Age=0");
                return Task.FromResult(response);
            }
            HttpResponseMessage result;
            if (address.AbsolutePath == "/base")
            {
                result = new(HttpStatusCode.OK) { Content = new StringContent("<script>var fid='901';</script>") };
                if (phase == "seed")
                {
                    result.Headers.TryAddWithoutValidation("Set-Cookie", AuthName + "=synthetic-renewed; Domain=chaoxing.com; Path=/; Secure; HttpOnly; SameSite=Strict");
                    result.Headers.TryAddWithoutValidation("Set-Cookie", "SYNTHETIC_DELETE=; Domain=chaoxing.com; Path=/; Max-Age=0");
                    expected = "synthetic-renewed";
                }
            }
            else if (address.AbsolutePath.Contains("courselistdata", StringComparison.Ordinal))
                result = new(HttpStatusCode.OK) { Content = new StringContent("<div class='course-list'><div><a href='https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid=101&amp;clazzid=201&amp;cpi=301'><span class='course-name' title='合成课程'>课程</span></a></div></div>") };
            else if (address.AbsolutePath == "/visit/stucoursemiddle")
                result = new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/stu" + address.Query + "&enc=SYNTHETIC_ONLY&v=2") } };
            else if (address.AbsolutePath == "/mooc2-ans/mycourse/stu")
                result = new(HttpStatusCode.OK) { Content = new StringContent("<input id='workEnc' value='SYNTHETIC_ONLY'><input id='enc' value='SYNTHETIC_ONLY'><input id='openc' value='SYNTHETIC_ONLY'>") };
            else if (address.AbsolutePath == "/mooc2/work/list")
                result = new(HttpStatusCode.OK) { Content = new StringContent("<ul id='workList'><li onclick='goTask(this);' data='https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&amp;classId=201&amp;cpi=301&amp;workId=401&amp;enc=SYNTHETIC_ONLY'><div class='right-content'><p class='overHidden2'>合成作业</p><p class='status'>已完成</p></div><div class='time'>剩余 48 小时</div></li></ul>") };
            else if (address.AbsolutePath == "/mooc-ans/mooc2/work/task")
                result = new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://mooc1.chaoxing.com/mooc-ans/mooc2/work/view" + address.Query) } };
            else if (address.AbsolutePath == "/mooc-ans/mooc2/work/view")
                result = new(HttpStatusCode.OK) { Content = new StringContent("<div class='detailsHead'><h2 class='mark_title'>合成作业</h2><div class='infoHead'><p class='rightBord'>作答时间:<em>10-14 19:04</em>至<em>10-25 23:59</em></p></div></div>") };
            else throw new InvalidOperationException("Unexpected session fixture route.");
            return Task.FromResult(result);
        }
    }
}
