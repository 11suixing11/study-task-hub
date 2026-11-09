using System.IO;
using System.Reflection;
using System.Windows.Controls;
using AccessProbe;
using Microsoft.Web.WebView2.Wpf;

namespace MetadataSyncHarness;

internal static partial class Program
{
    private static async Task VerifyLoginFailurePreservationAsync()
    {
        lastCheck = "profile rejection preserves a confirmed reusable login";
        var service = services["zhihuishu"]; var fixture = fixtures["zhihuishu"]; var browser = browsers["zhihuishu"];
        await service.StartAsync(true);
        var archive = new MetadataSessionStore(MetadataPlatform.Zhihuishu, Path.Combine(runRoot, "profile-check.bin"), store);
        var before = archive.Load(DateTimeOffset.UtcNow).ToDictionary(cookie => cookie.Key, cookie => cookie.Value);
        var count = store.LoadTasks().Count;
        try
        {
            foreach (var missingToken in new[] { false, true })
            {
                fixture.DenyProfile = !missingToken; fixture.MissingStudentToken = missingToken;
                await service.StartAsync(true);
                var result = store.LoadMetadataSettings("zhihuishu").LastSummary!;
                Check(!result.LoginRequired && result.Problems.All(problem => problem.Code != "ZHS001")
                    && store.LoadPlatformSessionInfo("zhihuishu").State == "ready", "course permission and missing JWT do not invalidate CAS cookie");
                var retained = archive.Load(DateTimeOffset.UtcNow);
                Check(retained.Count == before.Count && retained.All(cookie => before.GetValueOrDefault(cookie.Key) == cookie.Value)
                    && store.LoadTasks().Count == count, "rejected profile preserves encrypted cookies and historical tasks");
                Check(service.LoginDiagnostics.Snapshot().Requests.Last().Outcome == (missingToken ? "course_credentials_unavailable" : "course_access_denied"),
                    "login diagnostic identifies rejected permission or pending course credential");
            }
        }
        finally { fixture.DenyProfile = fixture.MissingStudentToken = false; }
        await service.StartAsync(true);
        Check(store.LoadMetadataSettings("zhihuishu").LastSummary!.AccountVerified, "profile can recover without user repeatedly signing in");

        lastCheck = "open login window waits for actual account evidence";
        await service.ClearSessionAsync();
        var connection = new MetadataConnectionWindow(service, () => { });
        var loginBrowser = (WebView2)typeof(MetadataConnectionWindow).GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        fixture.BlockBrowser(loginBrowser);
        try
        {
            var beforeRequests = fixture.Requests;
            connection.Show(); await WaitAsync(() => loginBrowser.CoreWebView2 is not null);
            await WaitAsync(() => service.LoginDiagnostics.Snapshot().Events.Any(item => item.Result == MetadataLoginResult.AwaitingLogin));
            Check(fixture.Requests == beforeRequests && store.LoadPlatformSessionInfo("zhihuishu").State != "ready",
                "unfinished official login neither probes account API nor marks invalid cookies reusable");
            var status = (TextBlock)typeof(MetadataConnectionWindow).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
            Check(status.Text.Contains("等待智慧树官方登录", StringComparison.Ordinal) && !status.Text.Contains("ZHS001", StringComparison.Ordinal),
                "waiting at login page avoids repeated expiry prompt");
            fixture.SetCookies(browser); await Task.Delay(80);
            await WaitAsync(() => service.LoginDiagnostics.Snapshot().AccountConfirmed);
            Check(store.LoadPlatformSessionInfo("zhihuishu").State == "ready", "new official account cookies replace cleared session automatically");
        }
        finally
        {
            connection.Close(); service.Cancel(); await WaitAsync(() => !service.IsRunning);
            foreach (var selection in app.Windows.OfType<CourseSelectionWindow>().ToArray()) selection.Close();
        }
    }
    private static async Task VerifyUcampusSsoExpiryAsync()
    {
        lastCheck = "official Ucampus SSO bridge invalidates only rejected session";
        var service = services["ucampus"]; var fixture = fixtures["ucampus"];
        var count = store.LoadTasks().Count; var recording = store.LoadCourseRecording("ucampus");
        try
        {
            fixture.RedirectUcampusLogin = true; var beforeRequests = fixture.Requests;
            await service.StartAsync(true);
            var summary = store.LoadMetadataSettings("ucampus").LastSummary!;
            Check(summary.LoginRequired && summary.Problems.All(problem => problem.Code == "UC001")
                && fixture.Requests == beforeRequests + 1, "SSO redirect produces UC001 without fetching login or course data");
            Check(store.LoadPlatformSessionInfo("ucampus").State == "expired" && store.LoadTasks().Count == count
                && store.LoadCourseRecording("ucampus").SelectedCourseKeys.SequenceEqual(recording.SelectedCourseKeys),
                "SSO expiry retains tasks and selected course range");
            Check(service.LoginDiagnostics.Snapshot().Requests.Last() is { Operation: "profile", Outcome: "login_required", Status: 302 },
                "SSO expiry reason and status are recorded without redirect credentials");
        }
        finally { fixture.RedirectUcampusLogin = false; }
        fixture.Session = "synthetic-ucampus-official-relogin"; fixture.SetCookies(browsers["ucampus"]); await Task.Delay(80);
        service.ConnectionOpened();
        try { await service.StartAsync(true, afterLogin: true); }
        finally { service.ConnectionClosed(); }
        Check(store.LoadPlatformSessionInfo("ucampus").State == "ready" && service.LoginDiagnostics.Snapshot().AccountConfirmed,
            "official relogin replaces rejected Ucampus session and refreshes course catalog");
        foreach (var popup in app.Windows.OfType<CourseSelectionWindow>().ToArray()) popup.Close();
    }
}
