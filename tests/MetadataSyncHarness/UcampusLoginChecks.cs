using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MetadataSyncHarness;

internal static partial class Program
{
    private static async Task VerifyUcampusClosedWindowPopupAsync(MetadataSyncService service, Fixtures fixture)
    {
        const string student = "https://u.unipus.cn/user/student/index";
        const string rejected = "https://evil.example/synthetic-closed-popup?token=synthetic-private";
        foreach (var onStudentPage in new[] { true, false })
        {
            lastCheck = "Ucampus connection closes before paused session validation completes";
            var connection = new MetadataConnectionWindow(service, () => { });
            var browser = (WebView2)typeof(MetadataConnectionWindow).GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
            fixture.BlockBrowser(browser); var closed = false;
            try
            {
                connection.Show(); await WaitAsync(() => browser.CoreWebView2 is not null);
                service.Cancel(); await WaitAsync(() => !service.IsRunning);
                fixture.PauseUcampusCatalog();
                var core = browser.CoreWebView2; var target = onStudentPage ? student : MetadataPlatform.Ucampus.Login.AbsoluteUri;
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void DocumentReady(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
                { if (core.Source == target) ready.TrySetResult(true); }
                core.DOMContentLoaded += DocumentReady;
                try { core.Navigate(target); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { core.DOMContentLoaded -= DocumentReady; }
                var popup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void PopupRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
                { if (args.Uri == rejected) popup.TrySetResult(args.Handled); }
                core.NewWindowRequested += PopupRequested;
                try
                {
                    await core.ExecuteScriptAsync("window.open(" + JsonSerializer.Serialize(rejected) + ", '_blank')");
                    Check(await popup.Task.WaitAsync(TimeSpan.FromSeconds(10)), "unconfirmed popup is blocked before closing the connection");
                }
                finally { core.NewWindowRequested -= PopupRequested; }
                var previousCatalog = store.LoadCourseRecording("ucampus").LastCatalogRefresh?.CheckedAtUtc;
                var taskSummary = JsonSerializer.Serialize(store.LoadMetadataSettings("ucampus").LastSummary);
                var taskSnapshot = JsonSerializer.Serialize(store.LoadTasks());
                var requestCount = fixture.Paths.Count;
                var sync = service.StartAsync(true, catalogOnly: true);
                await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(service.IsRunning && service.LoginDiagnostics.Snapshot().PageFailure is { Stage: MetadataLoginStage.Popup },
                    "popup failure stays visible while the session result is pending");
                connection.Close(); closed = true;
                fixture.Release!.TrySetResult(true); await sync;
                lastCheck = "Ucampus catalog validation completes after its connection window closes";
                await WaitAsync(() => !service.IsRunning && service.LoginDiagnostics.SessionConfirmed);
                var recording = store.LoadCourseRecording("ucampus");
                Check(recording is { CatalogComplete: true, Courses.Count: 1, LastCatalogRefresh.Outcome: "completed" }
                    && (previousCatalog is null || recording.LastCatalogRefresh.CheckedAtUtc > previousCatalog),
                    "fresh course catalog is saved after the connection window closes");
                Check(store.LoadPlatformSessionInfo("ucampus").State == "ready"
                    && taskSummary == JsonSerializer.Serialize(store.LoadMetadataSettings("ucampus").LastSummary)
                    && taskSnapshot == JsonSerializer.Serialize(store.LoadTasks())
                    && fixture.Paths.Skip(requestCount).All(path => !path.StartsWith("/user/student/homework/", StringComparison.Ordinal)),
                    "closed-window login persists its Cookie without requesting or importing tasks");
                Check(onStudentPage ? service.LoginDiagnostics.Snapshot().PageFailure is null
                    : service.LoginDiagnostics.Snapshot().PageFailure is { Stage: MetadataLoginStage.Popup, Result: MetadataLoginResult.Blocked },
                    onStudentPage ? "closed student window recovers its popup diagnostic" : "closed login window retains a blocked login popup despite valid cookies");
                using var persisted = JsonDocument.Parse(File.ReadAllText(service.LoginDiagnostics.Path));
                Check(onStudentPage ? persisted.RootElement.GetProperty("PageFailure").ValueKind == JsonValueKind.Null
                    : persisted.RootElement.GetProperty("PageFailure").GetProperty("Stage").GetString() == "Popup",
                    "persisted login diagnostic matches the closed-window recovery result");
                Check(browsers["ucampus"].CoreWebView2.Source == "about:blank", "closed-window checks keep the background browser blank");
            }
            finally
            {
                fixture.Release?.TrySetResult(true); fixture.ResetPause();
                if (!closed) connection.Close();
            }
        }
    }

    private static async Task VerifyUcampusStudentPopupAsync(MetadataConnectionWindow connection, WebView2 browser,
        MetadataSyncService service, Fixtures fixture)
    {
        lastCheck = "Ucampus student-page popup warning and confirmed metadata recovery";
        var core = browser.CoreWebView2;
        var status = (TextBlock)typeof(MetadataConnectionWindow).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        const string student = "https://u.unipus.cn/user/student/index";
        const string rejected = "https://evil.example/synthetic-student-popup?token=synthetic-private";
        service.LoginDiagnostics.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void DocumentReady(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
        { if (core.Source == student) ready.TrySetResult(true); }
        core.DOMContentLoaded += DocumentReady;
        try { core.Navigate(student); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { core.DOMContentLoaded -= DocumentReady; }

        // Reproduce the popup arriving on the student page before the asynchronous session check finishes.
        var popup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void PopupRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
        { if (args.Uri == rejected) popup.TrySetResult(args.Handled); }
        core.NewWindowRequested += PopupRequested;
        try
        {
            await core.ExecuteScriptAsync("window.open(" + JsonSerializer.Serialize(rejected) + ", '_blank')");
            Check(await popup.Task.WaitAsync(TimeSpan.FromSeconds(10)), "unconfirmed student popup is blocked in the existing window");
            Check(status.Text.Contains("当前登录跳转尚未适配", StringComparison.Ordinal), "pending student popup reproduces the original visible warning");
            var previousCatalog = store.LoadCourseRecording("ucampus").LastCatalogRefresh?.CheckedAtUtc;
            var refreshStarted = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var taskSummary = JsonSerializer.Serialize(store.LoadMetadataSettings("ucampus").LastSummary);
            var taskSnapshot = JsonSerializer.Serialize(store.LoadTasks());
            var requestCount = fixture.Paths.Count;
            MetadataSyncSummary? catalogSummary = null;
            void CatalogCompleted(MetadataSyncSummary summary) => catalogSummary = summary;
            service.CatalogCompleted += CatalogCompleted;
            try { await service.RefreshCourseCatalogAsync(); }
            finally { service.CatalogCompleted -= CatalogCompleted; }
            lastCheck = "Ucampus student-page catalog and neutral login status are refreshed";
            await WaitAsync(() => !service.IsRunning && service.LoginDiagnostics.SessionConfirmed);
            var recording = store.LoadCourseRecording("ucampus");
            Check(catalogSummary is { LoginRequired: false, CourseListCovered: true, Courses: 1, CoursesRead: 0, Tasks: 0 }
                && recording.LastCatalogRefresh is { Outcome: "completed" }
                && (previousCatalog is null || recording.LastCatalogRefresh.CheckedAtUtc > previousCatalog)
                && service.LoginDiagnostics.Snapshot().PageFailure is null,
                "fresh Ucampus catalog validates the current session and resolves the earlier popup failure");
            Check(store.LoadPlatformSessionInfo("ucampus") is { State: "ready", SavedAtUtc: not null } session
                && session.SavedAtUtc >= refreshStarted, "successful catalog login saves the latest reusable Cookie in the repository");
            Check(taskSummary == JsonSerializer.Serialize(store.LoadMetadataSettings("ucampus").LastSummary)
                && taskSnapshot == JsonSerializer.Serialize(store.LoadTasks())
                && fixture.Paths.Skip(requestCount).All(path => !path.StartsWith("/user/student/homework/", StringComparison.Ordinal)),
                "login catalog refresh preserves task history and does not request task lists");
            Check(status.Text.Contains("课程列表已刷新", StringComparison.Ordinal)
                && !status.Text.Contains("跳转尚未适配", StringComparison.Ordinal) && status.Foreground == Brushes.DimGray,
                "successful Ucampus catalog refresh replaces the stale red login warning");

            var confirmedText = status.Text;
            var beforeRequests = fixture.BrowserRequests;
            var beforeCookies = (await core.CookieManager.GetCookiesAsync(""))
                .ToDictionary(cookie => cookie.Name + "|" + cookie.Domain + "|" + cookie.Path, cookie => cookie.Value);
            popup = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await core.ExecuteScriptAsync("window.open(" + JsonSerializer.Serialize(rejected) + ", '_blank')");
            Check(await popup.Task.WaitAsync(TimeSpan.FromSeconds(10)), "confirmed student popup remains blocked");
            await Task.Delay(100);
            Check(status.Text == confirmedText && status.Foreground == Brushes.DimGray,
                "popup after confirmed login preserves the successful catalog message");
            Check(service.LoginDiagnostics.Snapshot().Events.Any(item => item is { Stage: MetadataLoginStage.Popup, Result: MetadataLoginResult.PopupSuppressed })
                && service.LoginDiagnostics.Snapshot().PageFailure is null, "confirmed student popup is retained as non-failing diagnostic evidence");
            Check(await core.ExecuteScriptAsync("location.href") == JsonSerializer.Serialize(student) && fixture.BrowserRequests == beforeRequests,
                "blocked student popup neither leaves the student document nor requests another page");
            var afterCookies = await core.CookieManager.GetCookiesAsync("");
            Check(afterCookies.Count == beforeCookies.Count && afterCookies.All(cookie => beforeCookies.TryGetValue(
                cookie.Name + "|" + cookie.Domain + "|" + cookie.Path, out var value) && value == cookie.Value),
                "student popup handling preserves the login cookies");
            var diagnosticText = File.ReadAllText(service.LoginDiagnostics.Path);
            Check(!diagnosticText.Contains("synthetic-private", StringComparison.Ordinal) && !diagnosticText.Contains("evil.example", StringComparison.Ordinal),
                "student popup diagnostics contain no raw popup address or token");

            // A failed main navigation still needs attention even with an existing confirmed account.
            var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
            { if (args.Uri == rejected) blocked.TrySetResult(args.Cancel); }
            core.NavigationStarting += NavigationStarting;
            try { core.Navigate(rejected); Check(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)), "unrelated main navigation stays blocked"); }
            finally { core.NavigationStarting -= NavigationStarting; }
            var failedText = status.Text;
            await service.StartAsync(true, catalogOnly: true);
            Check(status.Text == failedText && service.LoginDiagnostics.Snapshot().PageFailure is { Stage: MetadataLoginStage.MainNavigation, Result: MetadataLoginResult.Blocked },
                "background metadata cannot erase a failed main login document");
            Check(browsers["ucampus"].CoreWebView2.Source == "about:blank", "popup checks never navigate the background Ucampus browser");
        }
        finally { core.NewWindowRequested -= PopupRequested; }
    }
}
