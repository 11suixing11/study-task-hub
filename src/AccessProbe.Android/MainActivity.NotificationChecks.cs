using AccessProbe;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using System.Text.Json;
using Path = System.IO.Path;
using View = Android.Views.View;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    // Explicit emulator-only entry. System notification sending/settings/permission requests remain intercepted.
    private async Task RunMobileNotificationChecks()
    {
        if (!syntheticUi || Build.Hardware is not ("ranchu" or "goldfish")) return;
        var directory = Path.Combine(MobileData.Root(this), "synthetic-notification-test");
        Directory.CreateDirectory(directory); var results = new List<string>();
        var report = Path.Combine(directory, "notification-checks.json");
        void Check(bool value, string label) { if (!value) throw new InvalidDataException(label); results.Add(label); }
        IEnumerable<View> Descendants(View view)
        {
            yield return view;
            if (view is ViewGroup group)
                for (var i = 0; i < group.ChildCount; i++)
                    foreach (var child in Descendants(group.GetChildAt(i)!)) yield return child;
        }
        async Task<bool> ReceiveSettled(Intent? intent)
        {
            var previousView = viewRevision;
            var received = ReceiveMobileNotificationTask(intent);
            await mobileNotificationTaskLoad;
            return received && !HasPendingMobileNotificationTask && viewRevision != previousView;
        }
        async Task SettleNotification()
        {
            await Task.Delay(40);
            var latest = mobileNotificationTaskLoad;
            await latest;
            if (!ReferenceEquals(latest, mobileNotificationTaskLoad)) await mobileNotificationTaskLoad;
            await mobileTaskDetailLoad;
        }
        bool Has(string value) => Descendants(page).OfType<TextView>().Any(view => view.Text?.Contains(value, StringComparison.Ordinal) == true);
        void WriteReport(string outcome, string? error = null) => File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            schema = 1, outcome, checks = results.Count, results, error, officialNetworkAccessed = false,
            productionDataAccessed = false, systemNotificationsRequested = false, systemSettingsOpened = false,
            runtimePermissionsRequested = false, realAccountAcceptance = "pending"
        }));
        try
        {
            var protector = new AndroidDataProtector("LearningTaskAggregator.SyntheticNotifications.v1");
            store = new TaskStore(Path.Combine(directory, Guid.NewGuid().ToString("N"), "tasks.db"), protector);
            vault = new(store, protector); sync = new(store, vault, _ => new OfflineUiHandler());
            store.SaveSettings(new(false, true) { AutomaticSyncEnabled = false });
            var now = DateTimeOffset.UtcNow;
            var first = store.Create(new("通知目标甲", "chaoxing", "通知合成课程", DeadlineKind.Unknown,
                null, null, null, null, "", "", null), now);
            var second = store.Create(new("通知目标乙", "pta", "通知合成课程", DeadlineKind.Unknown,
                null, null, null, null, "", "", null), now);

            TaskStore ReminderStore(string name)
            {
                var isolated = new TaskStore(Path.Combine(directory, Guid.NewGuid().ToString("N"), "tasks.db"), protector);
                isolated.SaveSettings(new(true, true) { AutomaticSyncEnabled = false });
                isolated.Create(new(name, "chaoxing", "通知合成课程", DeadlineKind.Known,
                    null, now.AddHours(1), null, null, "", "", null), now);
                return isolated;
            }
            var reminderStore = ReminderStore("禁通道提醒验收");
            var ready = MobileNotificationStatus.FromSignals(true, true, true, true);
            var blocked = new[]
            {
                MobileNotificationStatus.FromSignals(false, true, true, true),
                MobileNotificationStatus.FromSignals(true, false, true, true),
                MobileNotificationStatus.FromSignals(true, true, true, false)
            };
            var requests = 0;
            foreach (var state in blocked)
            {
                MobileReminderScheduler.CheckStoredReminders(reminderStore, now, () => state, _ => requests++);
                var history = reminderStore.LoadReminderHistory().Where(item => item.Plan.DueAtUtc <= now).ToArray();
                Check(history.Length > 0 && history.All(item => item.State == "scheduled" && item.Attempts == 0 && item.RequestedAtUtc is null),
                    state.Kind + " releases reminder claims without consuming a request");
            }
            Check(requests == 0, "disabled notification states never invoke the injected request callback");
            MobileReminderScheduler.CheckStoredReminders(reminderStore, now, () => ready, _ => requests++);
            Check(requests > 0 && reminderStore.LoadReminderHistory().Where(item => item.Plan.DueAtUtc <= now).All(item => item.State == "requested"),
                "re-enabling notifications allows the previously blocked plans to be requested");
            var sentCount = requests;
            MobileReminderScheduler.CheckStoredReminders(reminderStore, now.AddSeconds(1), () => ready, _ => requests++);
            Check(requests == sentCount, "a recorded system request is not repeated on the next reminder check");

            var changingStore = ReminderStore("中途关闭通道验收");
            var currentState = ready;
            MobileReminderScheduler.CheckStoredReminders(changingStore, now, () => currentState,
                _ => currentState = MobileNotificationStatus.FromSignals(true, true, true, false));
            Check(changingStore.LoadReminderHistory().Where(item => item.Plan.DueAtUtc <= now)
                .All(item => item.State == "scheduled" && item.RequestedAtUtc is null && item.Attempts == 0),
                "a channel disabled during a request leaves the current and remaining plans eligible for retry");
            var failingStore = ReminderStore("请求异常验收");
            MobileReminderScheduler.CheckStoredReminders(failingStore, now, () => ready,
                _ => throw new InvalidOperationException("synthetic_request_failure"));
            Check(failingStore.LoadReminderHistory().Where(item => item.Plan.DueAtUtc <= now)
                .All(item => item.State == "scheduled" && item.ErrorCode == "NOTIFY001" && item.RequestedAtUtc is null),
                "system request exceptions retain retry plans without claiming delivery");

            Dashboard(); await dashboardLoad;
            using var firstIntent = MobileReminderScheduler.CreateOpenIntent(this, first.Id);
            using var secondIntent = MobileReminderScheduler.CreateOpenIntent(this, second.Id);
            externalTaskId = first.Id;
            Check(await ReceiveSettled(secondIntent) && Has(second.Title) && externalTaskId is null,
                "a new notification task overrides the old external-app return target");
            OnResume();
            Check(Has(second.Title), "the next lifecycle resume does not overwrite the notification with the older task");
            using var missing = MobileReminderScheduler.CreateOpenIntent(this, new string('f', 32));
            var selectedRevision = MobileNotificationSelectionRevision; var selectedViewRevision = viewRevision;
            Check(!await ReceiveSettled(missing) && Has(second.Title) && status.Text?.Contains("已不存在", StringComparison.Ordinal) == true,
                "a stale notification explains the missing task while preserving the current detail");
            Check(MobileNotificationSelectionRevision == selectedRevision + 1 && viewRevision == selectedViewRevision && !HasPendingMobileNotificationTask,
                "a missing but valid notification advances task selection even when the current page stays visible");
            // Hold only the asynchronous read result, never the database lock or UI thread.
            // This makes both late success and failure deterministic.
            var resumeRead = new TaskCompletionSource<MobileTaskResumeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateResume = ResumeTask(first.Id, _ => resumeRead.Task);
            await ReceiveSettled(missing); var missingMessage = status.Text;
            resumeRead.SetResult(new(first, false)); await lateResume;
            Check(Has(second.Title) && status.Text == missingMessage,
                "a late external-task read cannot overwrite the newer missing-notification explanation");
            resumeRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lateResume = ResumeTask(first.Id, _ => resumeRead.Task);
            await ReceiveSettled(missing); missingMessage = status.Text;
            resumeRead.SetException(new IOException("synthetic_resume_read_failure")); await lateResume;
            Check(Has(second.Title) && status.Text == missingMessage,
                "a late failed external-task read cannot overwrite a newer notification selection");
            selectedRevision = MobileNotificationSelectionRevision;
            using var invalid = new Intent(); invalid.PutExtra("task_id", "not-a-task");
            await ReceiveSettled(invalid);
            Check(MobileNotificationSelectionRevision == selectedRevision, "a malformed notification cannot invalidate a legitimate task selection");
            busy = true;
            Check(!await ReceiveSettled(firstIntent) && HasPendingMobileNotificationTask && Has(second.Title),
                "an ongoing operation queues a clicked notification without replacing the page");
            busy = false; TryOpenPendingMobileNotificationTask(); await SettleNotification();
            Check(Has(first.Title) && !HasPendingMobileNotificationTask, "an operation completion opens its queued notification once");
            checkingLogin = true;
            Check(!await ReceiveSettled(secondIntent) && Has(first.Title) && HasPendingMobileNotificationTask,
                "a login cleanup operation protects a queued notification even after its WebView closes");
            checkingLogin = false; SchedulePendingMobileNotificationTask(); await SettleNotification();
            Check(Has(second.Title) && !HasPendingMobileNotificationTask, "login cleanup completion replays the retained notification target");

            AddTask();
            var titleInput = Descendants(page).OfType<EditText>().First(); titleInput.Text = "尚未保存的通知验收任务";
            Check(!await ReceiveSettled(secondIntent) && HasPendingMobileNotificationTask && titleInput.Text == "尚未保存的通知验收任务"
                && Has("添加任务"), "clicking a notification preserves an unsaved add form");
            HandleSystemBack(); await mobilePageLoad; await SettleNotification();
            Check(Has(second.Title) && !HasPendingMobileNotificationTask
                && store.LoadTasks().All(task => task.Title != "尚未保存的通知验收任务"),
                "cancelling an add form opens the pending notification without creating a task");
            EditTaskDeadline(first.Id); await mobileTaskEditorLoad;
            var deadlineInput = Descendants(page).OfType<EditText>().Single(); deadlineInput.Text = "unsaved-not-a-time";
            Check(!await ReceiveSettled(secondIntent) && deadlineInput.Text == "unsaved-not-a-time" && HasPendingMobileNotificationTask,
                "clicking a notification preserves unsaved deadline input");
            HandleSystemBack(); await SettleNotification();
            Check(Has(second.Title) && store.Get(first.Id)!.DeadlineKind == DeadlineKind.Unknown,
                "cancelling deadline editing opens the pending notification and leaves stored time unchanged");

            CreateLoginPage("chaoxing");
            var loginWeb = web!; loginWeb.Settings.BlockNetworkLoads = true;
            loginWeb.SetWebViewClient(new NotificationOfflineLoginClient());
            loginWeb.LoadData("<html><body>隔离通知登录页面</body></html>", "text/html", "UTF-8");
            Check(!await ReceiveSettled(firstIntent) && web == loginWeb && HasPendingMobileNotificationTask,
                "a notification retains its target while keeping the active login WebView");
            await ReceiveSettled(secondIntent);
            Check(web == loginWeb && mobileNotificationRouting.PendingTaskId == second.Id,
                "a second click during login updates the pending task without destroying login");
            var oldContinue = connectionContinue!;
            CloseLogin("chaoxing"); await mobilePageLoad; await SettleNotification();
            Check(web is null && Has(second.Title) && !HasPendingMobileNotificationTask,
                "ending login opens the latest pending notification task");
            var afterLoginPage = page; oldContinue.PerformClick();
            Check(ReferenceEquals(page, afterLoginPage) && Has(second.Title), "a detached login completion action cannot replace a newer task page");

            SettingsPage(); await mobilePageLoad; await mobileNotificationSettingsLoad;
            Check(Has("接收课程任务提醒") && Has("允许通知权限") && Has("打开系统通知设置") && Has("发送测试提醒"),
                "settings exposes app reminder preference, permission recovery, system settings and an honest test action");
            var notificationPreference = mobileNotificationPreference!;
            Check(!notificationPreference.Checked && notificationPreference.Enabled, "loaded reminder preference reflects its isolated saved settings");
            notificationPreference.Checked = true;
            await mobileNotificationSettingsWrite; await mobileNotificationSettingsLoad;
            Check(store.LoadSettings().NotificationsEnabled && notificationPreference.Checked && notificationPreference.Enabled,
                "the reminder preference saves asynchronously and refreshes the original switch");
            await Task.WhenAll(SaveMobileSettingsAsync(saved => saved with { AutomaticSyncEnabled = true }),
                SaveMobileSettingsAsync(saved => saved with { NotificationsEnabled = false }));
            var preferences = store.LoadSettings();
            Check(preferences.AutomaticSyncEnabled && !preferences.NotificationsEnabled,
                "concurrent sync and reminder preference changes preserve both settings through the shared write gate");
            await SaveMobileSettingsAsync(saved => saved with { AutomaticSyncEnabled = false });
            RefreshMobileNotificationSettings(); await mobileNotificationSettingsLoad;
            Check(!notificationPreference.Checked && mobileNotificationStatus?.Text?.Contains("已关闭", StringComparison.Ordinal) == true,
                "an asynchronous preference reload restores the saved reminder switch and explains disabled reminders");
            Check(HandleMobileNotificationPermissionResult(301, [Android.Manifest.Permission.PostNotifications], [Permission.Denied])
                && status.Text?.Contains("未允许通知权限", StringComparison.Ordinal) == true,
                "a denied runtime notification permission explains why reminders will not display");
            Check(HandleMobileNotificationPermissionResult(301, [Android.Manifest.Permission.PostNotifications], [Permission.Granted])
                && status.Text?.Contains("确认通知是否显示", StringComparison.Ordinal) == true,
                "permission grant feedback asks for visibility verification rather than claiming delivery");
            Check(!HandleMobileNotificationPermissionResult(302, [], []), "notification permission handling leaves QR storage permission handling available");
            using var system = MobileReminderScheduler.CreateSystemSettingsIntent(this, true);
            Check(system.Action == Android.Provider.Settings.ActionChannelNotificationSettings
                && system.GetStringExtra(Android.Provider.Settings.ExtraAppPackage) == PackageName
                && system.GetStringExtra(Android.Provider.Settings.ExtraChannelId) == MobileReminderScheduler.Channel,
                "the recovery intent targets this app and its reminder channel without launching settings");
            await TestMobileNotification();
            Check(status.Text?.Contains("已拦截测试通知发送", StringComparison.Ordinal) == true,
                "isolated notification tests never post a real notification");
            RefreshMobileNotificationSettings(true);
            var lateSettings = mobileNotificationSettingsLoad;
            TaskDetail(second.Id); await mobileTaskDetailLoad; status.Text = "新页面状态保留";
            await lateSettings;
            notificationPreference.Checked = !notificationPreference.Checked;
            Check(Has(second.Title) && status.Text == "新页面状态保留" && !store.LoadSettings().NotificationsEnabled,
                "a late settings read and a detached reminder switch cannot change a newer page or its saved preference");
            WriteReport("passed");
        }
        catch (Exception error) { WriteReport("failed", error.GetType().Name + ": " + error.Message); }
        finally
        {
            busy = false; checkingLogin = false; externalTaskId = null;
            web?.StopLoading(); web?.Destroy(); web = null; loginPlatform = null;
        }
    }

    private sealed class NotificationOfflineLoginClient : WebViewClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request) =>
            new("text/plain", "UTF-8", new MemoryStream([]));
    }
}
