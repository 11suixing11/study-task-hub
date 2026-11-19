using AccessProbe;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Webkit;
using Android.Widget;
using System.Text.Json;
using Path = System.IO.Path;
using View = Android.Views.View;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private string? syntheticBackMode, syntheticBackReport;
    private TaskItem? syntheticBackTask;
    private CancellationTokenSource? syntheticBackLoginLifetime, syntheticBackWork;
    private readonly List<object> syntheticBackEvents = [];

    // This entry is emulator-only and uses its own encrypted database and offline handler.
    private async Task PrepareBackNavigationChecks(string mode)
    {
        var directory = Path.Combine(MobileData.Root(this), "synthetic-back-navigation-test");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "back-" + mode + ".json");
        try
        {
            if (mode is not ("home" or "double" or "timeout" or "lifecycle" or "platforms" or "settings"
                or "platform" or "courses" or "detail" or "notification" or "deadline" or "add" or "login" or "toolbar" or "legacy"))
                throw new ArgumentException("Only configured isolated back scenarios are accepted.");
            syntheticBackMode = mode; syntheticBackReport = output;
            var protector = new AndroidDataProtector("LearningTaskAggregator.SyntheticBackNavigation.v1");
            store = new TaskStore(Path.Combine(directory, Guid.NewGuid().ToString("N"), "tasks.db"), protector);
            vault = new(store, protector); sync = new(store, vault, _ => new OfflineUiHandler());
            store.SaveSettings(new(false, false) { AutomaticSyncEnabled = false });
            syntheticBackTask = store.Create(new("返回键验收任务", "chaoxing", "合成课程", DeadlineKind.Unknown,
                null, null, null, null, "", "未保存编辑不得改变此记录", null), DateTimeOffset.UtcNow);
            Dashboard();
            switch (mode)
            {
                case "platforms": PlatformsPage(); break;
                case "settings": SettingsPage(); break;
                case "platform": case "courses": PlatformsPage(); Platform("chaoxing"); break;
                case "detail": TaskDetail(syntheticBackTask.Id); await mobileTaskDetailLoad; break;
                case "notification":
                    using (var notification = MobileReminderScheduler.CreateOpenIntent(this, syntheticBackTask.Id))
                        if (!DisplayNotificationTask(notification)) throw new InvalidDataException("Notification task did not open.");
                    break;
                case "deadline":
                    TaskDetail(syntheticBackTask.Id); await mobileTaskDetailLoad; EditTaskDeadline(syntheticBackTask.Id); await mobileTaskEditorLoad;
                    BackCheckDescendants(page).OfType<EditText>().Single().Text = "unsaved";
                    break;
                case "add": AddTask(); BackCheckDescendants(page).OfType<EditText>().First().Text = "尚未保存的新任务"; break;
                case "login": case "toolbar":
                    PlatformsPage(); Platform("chaoxing");
                    CreateLoginPage("chaoxing");
                    web!.Settings.BlockNetworkLoads = true;
                    web.SetWebViewClient(new OfflineBackLoginClient());
                    web.LoadData("<html><body>合成登录页面，无账号与外网请求</body></html>", "text/html", "UTF-8");
                    syntheticBackLoginLifetime = loginLifetime = new();
                    syntheticBackWork = work = new(); checkingLogin = true;
                    break;
            }
            await mobilePageLoad; await mobileNotificationTaskLoad; await mobileTaskDetailLoad;
            if (rootPage == "tasks" && dashboardAdapter is not null) { await dashboardLoad; await dashboardRender; }
            (page.Parent?.Parent as View)?.RequestFocus();
            (GetSystemService(InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(page.WindowToken, HideSoftInputFlags.None);
            await Task.Delay(350);
            WriteSyntheticBackReport("ready");
            if (mode == "legacy")
            {
                TaskDetail(syntheticBackTask.Id); await mobileTaskDetailLoad; OnBackPressed();
            }
        }
        catch (Exception error)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { outcome = "failed", mode, error = error.GetType().Name,
                officialNetworkAccessed = false, productionDataAccessed = false }));
        }
    }

    private static IEnumerable<View> BackCheckDescendants(View view)
    {
        yield return view;
        if (view is ViewGroup group)
            for (var index = 0; index < group.ChildCount; index++)
                foreach (var child in BackCheckDescendants(group.GetChildAt(index)!)) yield return child;
    }

    private void RecordSyntheticBackEvent(MobileBackAction action, string source)
    {
        if (syntheticBackReport is null) return;
        syntheticBackEvents.Add(new
        {
            action = action.ToString(), source, rootPage, hasParent = pageBack is not null, selectedPlatform,
            loginOpen = web is not null || loginPlatform is not null,
            loginCancelled = syntheticBackLoginLifetime?.IsCancellationRequested,
            checkCancelled = syntheticBackWork?.IsCancellationRequested,
            taskEditorOpen, exitPending = backNavigation.ExitPending, alive, finishing = IsFinishing,
            taskUnchanged = syntheticBackTask is not null && store.Get(syntheticBackTask.Id) == syntheticBackTask,
            taskCount = store.LoadTasks().Count,
            heading = BackCheckDescendants(page).OfType<TextView>().OrderByDescending(text => text.TextSize).FirstOrDefault()?.Text,
            elapsedRealtime = SystemClock.ElapsedRealtime()
        });
        WriteSyntheticBackReport("event");
    }

    private void WriteSyntheticBackReport(string phase)
    {
        if (syntheticBackReport is null) return;
        File.WriteAllText(syntheticBackReport, JsonSerializer.Serialize(new
        {
            schema = 1, outcome = "passed", phase, mode = syntheticBackMode, api = (int)Build.VERSION.SdkInt,
            modernCallbackRegistered = systemBackCallback is not null,
            events = syntheticBackEvents, resumed, rootPage, hasParent = pageBack is not null,
            exitPending = backNavigation.ExitPending, officialNetworkAccessed = false, productionDataAccessed = false
        }));
    }

    private sealed class OfflineBackLoginClient : WebViewClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
            => new("text/plain", "UTF-8", new MemoryStream([]));
    }
}
