using System.IO;
using System.Text.Json;
using System.Windows;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SyncDiagnosticHarness;

// Own offscreen browser, no clicks or submitted credentials; only official login resource readiness.
internal static class MoocLoginDiagnosis
{
    public static int Run(string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var reports = Path.Combine(root, ".local", "sync-diagnostics"); Directory.CreateDirectory(reports);
        var report = Path.Combine(reports, "mooc-login-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        File.WriteAllText(Path.Combine(reports, "latest-mooc-login-report.txt"), report);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var browser = new WebView2 { Width = 1, Height = 1, Visibility = Visibility.Hidden };
        var host = new Window { Content = browser, Width = 1, Height = 1, Left = -10000, Top = -10000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var requested = new Dictionary<string, int>(); var responses = new Dictionary<string, int>();
        var navigation = new List<object>(); var samples = new List<object>(); var exitCode = 1;
        host.Loaded += async (_, _) =>
        {
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(55)); var token = cancellation.Token;
                var environment = await CoreWebView2Environment.CreateAsync(null, MoocSyncService.ProfilePath(root));
                await browser.EnsureCoreWebView2Async(environment); var core = browser.CoreWebView2;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.NavigationStarting += (_, args) =>
                {
                    var allowed = MoocPlatform.IsLoginNavigation(args.Uri);
                    navigation.Add(new { phase = "starting", resource = Resource(args.Uri), allowed });
                    if (!allowed) args.Cancel = true;
                };
                core.FrameNavigationStarting += (_, args) =>
                {
                    var allowed = MoocPlatform.IsLoginNavigation(args.Uri);
                    navigation.Add(new { phase = "frame", resource = Resource(args.Uri), allowed });
                    if (!allowed) args.Cancel = true;
                };
                core.DOMContentLoaded += (_, _) => navigation.Add(new { phase = "dom_ready" });
                core.NavigationCompleted += (_, args) => navigation.Add(new { phase = "completed", args.IsSuccess, status = args.WebErrorStatus.ToString() });
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested += (_, args) =>
                {
                    var key = Resource(args.Request.Uri) + " " + args.ResourceContext;
                    requested[key] = requested.GetValueOrDefault(key) + 1;
                };
                core.WebResourceResponseReceived += (_, args) =>
                {
                    var key = Resource(args.Request.Uri) + " " + args.Response.StatusCode;
                    responses[key] = responses.GetValueOrDefault(key) + 1;
                };
                core.Navigate(MoocPlatform.Login.AbsoluteUri);
                for (var sample = 0; sample < 6; sample++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    var json = await core.ExecuteScriptAsync("(() => ({ readyState: document.readyState, elements: document.getElementsByTagName('*').length, passwordInputs: document.querySelectorAll('input[type=password]').length, frames: document.querySelectorAll('iframe').length, scripts: document.scripts.length, resources: performance.getEntriesByType('resource').length }))()").WaitAsync(token);
                    using var document = JsonDocument.Parse(json); samples.Add(new { sample, state = document.RootElement.Clone() });
                }
                Write("diagnosed", null); exitCode = 0;
            }
            catch (Exception error) { Write("failed", new { type = error.GetType().Name }); }
            finally { browser.Dispose(); app.Shutdown(exitCode); }
        };
        app.Run(host); return exitCode;

        void Write(string outcome, object? failure) => File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            schema = 1, generatedAtUtc = DateTimeOffset.UtcNow, outcome, failure, navigation, samples, requested, responses,
            taskDatabaseMode = "untouched", sessionArchiveMode = "untouched", clicks = 0, submittedCredentials = false,
            responseBodiesRetained = false, privacy = "resource_categories_and_counts_only"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Resource(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var address)) return "invalid";
        var path = address.AbsolutePath;
        if (address.IdnHost == MoocPlatform.Host && path is "/" or "/member/login.htm" or "/home.htm") return address.IdnHost + path;
        if (address.IdnHost == "mc.stu.126.net" && path.StartsWith("/pub/s/pt_web_member_webLogin_", StringComparison.Ordinal)) return "mc.stu.126.net/member-login-script";
        if (address.IdnHost == "mc.stu.126.net" && path.StartsWith("/pub/s/core_", StringComparison.Ordinal)) return "mc.stu.126.net/core-script";
        var host = MoocPlatform.IsLoginNavigation(raw) || address.IdnHost is "mc.stu.126.net" or "edu-cms.nosdn.127.net"
            or "cmc.stu.126.net" or "ursdoccdn.nosdn.127.net" ? address.IdnHost : "other_resource_host";
        return host + "/" + (Path.GetExtension(path).ToLowerInvariant() switch
        { ".js" => "script", ".css" => "style", ".png" or ".jpg" or ".jpeg" or ".webp" or ".svg" => "image", _ => "resource" });
    }
}
