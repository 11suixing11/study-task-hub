using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AccessProbe;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private const int NotificationPermission = 301;
    private readonly MobileNotificationRouting mobileNotificationRouting = new();
    private bool mobileNotificationEditing;
    private bool openingMobileNotificationTask;
    private Task mobileNotificationTaskLoad = Task.CompletedTask;
    private bool returningFromNotificationSettings;
    private int notificationSystemSettingsViewRevision;
    private long mobileNotificationSelectionRevision;
    private TextView? mobileNotificationStatus;
    private Switch? mobileNotificationPreference;
    private bool applyingMobileNotificationPreference;
    private bool mobileNotificationSettingsSaving;
    private int mobileNotificationSettingsRequest;
    private Task mobileNotificationSettingsLoad = Task.CompletedTask;
    private Task mobileNotificationSettingsWrite = Task.CompletedTask;
    private readonly SemaphoreSlim mobileSettingsWriteGate = new(1, 1);

    private bool HasPendingMobileNotificationTask => mobileNotificationRouting.HasPending;
    private long MobileNotificationSelectionRevision => mobileNotificationSelectionRevision;

    // The general taskEditorOpen flag also protects read-only detail pages from periodic redraws.
    // Only unsaved add/deadline forms should defer a clicked notification.
    private void SetMobileNotificationEditing(bool editing) => mobileNotificationEditing = editing;

    // The automatic-sync toggle uses this same gate so two preference changes cannot
    // read the same old record and overwrite one another when saved on worker threads.
    private async Task<AppSettings> SaveMobileSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        var currentStore = store;
        await mobileSettingsWriteGate.WaitAsync();
        try
        {
            return await Task.Run(() => currentStore.UpdateSettings(change));
        }
        finally { mobileSettingsWriteGate.Release(); }
    }

    private void AddMobileNotificationSettings(LinearLayout parent, AppSettings? loadedSettings = null)
    {
        var originPage = page; var expectedRevision = viewRevision;
        var enabled = new Switch(this)
        {
            Text = "接收课程任务提醒", Checked = loadedSettings?.NotificationsEnabled == true,
            Enabled = loadedSettings is not null, TextSize = 14
        };
        enabled.SetPadding(0, Dp(12), 0, Dp(12));
        mobileNotificationPreference = enabled; mobileNotificationSettingsSaving = false;
        bool CurrentView() => alive && viewRevision == expectedRevision && ReferenceEquals(page, originPage)
            && ReferenceEquals(mobileNotificationPreference, enabled);
        enabled.CheckedChange += async (_, args) =>
        {
            if (applyingMobileNotificationPreference || mobileNotificationSettingsSaving || !CurrentView()) return;
            var requestedValue = args.IsChecked;
            enabled.Enabled = false; mobileNotificationSettingsSaving = true; mobileNotificationSettingsRequest++;
            SetMobileNotificationMessage("正在保存提醒设置…");
            mobileNotificationSettingsWrite = SavePreference();
            await mobileNotificationSettingsWrite;
            async Task SavePreference()
            {
                try
                {
                    await SaveMobileSettingsAsync(saved => saved with { NotificationsEnabled = requestedValue });
                    if (!CurrentView()) return;
                    ScheduleReminders(); SetMobileNotificationMessage("提醒设置已保存。");
                }
                catch { if (CurrentView()) SetMobileNotificationMessage("提醒设置未能保存，请稍后重试。"); }
                finally
                {
                    if (CurrentView())
                    {
                        mobileNotificationSettingsSaving = false; enabled.Enabled = true;
                        RefreshMobileNotificationSettings();
                    }
                }
            }
        };
        parent.AddView(enabled);
        mobileNotificationStatus = Text(parent, loadedSettings is null ? "正在读取提醒设置…"
            : !loadedSettings.NotificationsEnabled ? "本应用的课程任务提醒已关闭，开启后才会请求提醒。"
            : syntheticUi ? "隔离验收模式：不请求系统权限、不发送通知。" : "正在读取系统通知状态…", 14, false, Muted);
        RefreshMobileNotificationSettings();
        Button(parent, "允许通知权限", RequestMobileNotificationPermission, false);
        Button(parent, "打开系统通知设置", OpenMobileNotificationSettings, false);
        Button(parent, "发送测试提醒", async () => await TestMobileNotification(), false);
        Text(parent, "后台提醒使用本机已保存的任务。手机省电策略可能延迟提醒；可发送测试提醒确认当前通知是否显示。", 13, false, Muted);
    }

    private void RefreshMobileNotificationSettings(bool showMessage = false)
    {
        if (!alive || mobileNotificationStatus is null || rootPage != "settings" || store is null) return;
        var currentStore = store; var originPage = page; var target = mobileNotificationStatus;
        var preference = mobileNotificationPreference; var expectedRevision = viewRevision;
        var request = ++mobileNotificationSettingsRequest; var isolated = syntheticUi;
        var context = ApplicationContext ?? this;
        bool CurrentView() => alive && viewRevision == expectedRevision && ReferenceEquals(page, originPage)
            && ReferenceEquals(mobileNotificationStatus, target) && request == mobileNotificationSettingsRequest;
        mobileNotificationSettingsLoad = LoadPreference();
        async Task LoadPreference()
        {
            try
            {
                await mobileSettingsWriteGate.WaitAsync();
                (AppSettings Settings, string Message) loaded;
                try
                {
                    loaded = await Task.Run(() =>
                    {
                        var saved = currentStore.LoadSettings();
                        var message = !saved.NotificationsEnabled
                            ? "本应用的课程任务提醒已关闭，开启后才会请求提醒。"
                            : isolated ? "隔离验收模式：不请求系统权限、不发送通知。" : MobileReminderScheduler.ReadStatus(context).Message;
                        return (saved, message);
                    });
                }
                finally { mobileSettingsWriteGate.Release(); }
                if (!CurrentView()) return;
                target.Text = loaded.Message;
                if (!mobileNotificationSettingsSaving && preference is not null)
                {
                    applyingMobileNotificationPreference = true;
                    try { preference.Checked = loaded.Settings.NotificationsEnabled; preference.Enabled = true; }
                    finally { applyingMobileNotificationPreference = false; }
                }
                if (showMessage) SetMobileNotificationMessage(loaded.Message);
            }
            catch { if (CurrentView()) target.Text = "提醒设置暂时无法读取，请稍后重试，或进入系统通知设置核对。"; }
        }
    }

    private void RequestMobileNotificationPermission()
    {
        if (!alive) return;
        if (syntheticUi) { SetMobileNotificationMessage("隔离验收模式：已拦截系统通知权限请求。"); return; }
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            try
            {
                if (ShouldShowRequestPermissionRationale(Android.Manifest.Permission.PostNotifications))
                    SetMobileNotificationMessage("允许通知后，才能接收本机课程任务提醒。");
                RequestPermissions([Android.Manifest.Permission.PostNotifications], NotificationPermission);
            }
            catch { SetMobileNotificationMessage("无法请求通知权限，请进入系统通知设置开启。"); }
            return;
        }
        var state = MobileReminderScheduler.ReadStatus(this);
        SetMobileNotificationMessage(state.Message);
        if (!state.CanRequest) OpenMobileNotificationSettings();
    }

    private bool HandleMobileNotificationPermissionResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (requestCode != NotificationPermission) return false;
        if (!alive) return true;
        var index = Array.IndexOf(permissions, Android.Manifest.Permission.PostNotifications);
        var granted = index >= 0 && index < grantResults.Length && grantResults[index] == Permission.Granted;
        var message = granted ? "已允许通知权限。可在设置中发送测试提醒，确认通知是否显示。"
            : "未允许通知权限，课程任务提醒不会显示。可以继续使用任务清单；需要提醒时，请进入设置开启系统通知。";
        RefreshMobileNotificationSettings();
        SetMobileNotificationMessage(message);
        return true;
    }

    private void OpenMobileNotificationSettings()
    {
        if (!alive) return;
        if (syntheticUi) { SetMobileNotificationMessage("隔离验收模式：已拦截系统通知设置跳转。"); return; }
        var state = MobileReminderScheduler.ReadStatus(this);
        try
        {
            using var intent = MobileReminderScheduler.CreateSystemSettingsIntent(this,
                state.Kind == MobileNotificationStatusKind.ChannelDisabled);
            notificationSystemSettingsViewRevision = viewRevision;
            returningFromNotificationSettings = true; StartActivity(intent);
        }
        catch (Exception error) when (error is ActivityNotFoundException or Java.Lang.SecurityException)
        {
            try
            {
                using var fallback = MobileReminderScheduler.CreateApplicationSettingsIntent(this);
                notificationSystemSettingsViewRevision = viewRevision;
                returningFromNotificationSettings = true; StartActivity(fallback);
            }
            catch
            {
                returningFromNotificationSettings = false;
                SetMobileNotificationMessage("无法打开系统设置。请在手机设置中找到“聚合平台”，开启通知与课程任务提醒通道。");
            }
        }
    }

    private void ResumeMobileNotificationSettings()
    {
        if (!returningFromNotificationSettings) return;
        returningFromNotificationSettings = false;
        if (alive && viewRevision == notificationSystemSettingsViewRevision) RefreshMobileNotificationSettings(true);
    }

    private async Task TestMobileNotification()
    {
        if (!alive) return;
        if (syntheticUi) { SetMobileNotificationMessage("隔离验收模式：已拦截测试通知发送。"); return; }
        var expectedRevision = viewRevision;
        var currentStore = store;
        var context = ApplicationContext ?? this;
        SetMobileNotificationMessage("正在检查提醒设置…");
        bool enabled;
        try
        {
            await mobileSettingsWriteGate.WaitAsync();
            try { enabled = await Task.Run(() => currentStore.LoadSettings().NotificationsEnabled); }
            finally { mobileSettingsWriteGate.Release(); }
        }
        catch { if (alive && viewRevision == expectedRevision) SetMobileNotificationMessage("提醒设置暂时无法读取，请稍后重试。"); return; }
        if (!alive || viewRevision != expectedRevision || (!resumed && !syntheticUi)) return;
        if (!enabled)
        {
            SetMobileNotificationMessage("请先开启“接收课程任务提醒”，再发送测试提醒。"); return;
        }
        SetMobileNotificationMessage("正在向系统请求测试提醒…");
        var result = await Task.Run(() => MobileReminderScheduler.RequestTestNotification(context));
        if (!alive || viewRevision != expectedRevision) return;
        RefreshMobileNotificationSettings(); SetMobileNotificationMessage(result.Message);
    }

    private void SetMobileNotificationMessage(string message)
    {
        if (!alive || string.IsNullOrWhiteSpace(message)) return;
        if (status is not null) status.Text = message;
    }

    private bool ReceiveMobileNotificationTask(Intent? intent)
    {
        var received = mobileNotificationRouting.Receive(intent?.GetStringExtra("task_id"));
        if (received.Outcome == MobileNotificationRouteOutcome.None) return false;
        if (received.Outcome == MobileNotificationRouteOutcome.Invalid)
        {
            SetMobileNotificationMessage(received.Message); return false;
        }
        // A new explicit task selection outranks the task previously opened in another app.
        mobileNotificationSelectionRevision++;
        externalTaskId = null;
        navigationLifetime?.Cancel();
        return TryOpenPendingMobileNotificationTask();
    }

    private bool TryOpenPendingMobileNotificationTask()
    {
        if (!alive || (!resumed && !syntheticUi) || openingMobileNotificationTask || !HasPendingMobileNotificationTask) return false;
        var currentStore = store;
        var loginOpen = web is not null || loginPlatform is not null;
        var occupied = busy || checkingLogin || openingTask || mobileTaskSaving || currentStore is null;
        if (loginOpen || mobileNotificationEditing || occupied)
        {
            var deferred = mobileNotificationRouting.Resolve(loginOpen, mobileNotificationEditing, occupied, _ => true);
            SetMobileNotificationMessage(deferred.Message); return false;
        }
        var selectedId = mobileNotificationRouting.PendingTaskId!;
        var selectedRevision = MobileNotificationSelectionRevision; var expectedRevision = viewRevision;
        openingMobileNotificationTask = true;
        mobileNotificationTaskLoad = OpenPending();
        return true;
        async Task OpenPending()
        {
            var replay = false;
            try
            {
                var task = await Task.Run(() => currentStore!.Get(selectedId));
                if (!alive || (!resumed && !syntheticUi)) return;
                if (MobileNotificationSelectionRevision != selectedRevision || viewRevision != expectedRevision
                    || mobileNotificationRouting.PendingTaskId != selectedId)
                { replay = HasPendingMobileNotificationTask; return; }
                var result = mobileNotificationRouting.Resolve(web is not null || loginPlatform is not null,
                    mobileNotificationEditing, busy || checkingLogin || openingTask || mobileTaskSaving, id => task?.Id == id);
                if (result.Outcome == MobileNotificationRouteOutcome.Open)
                {
                    externalTaskId = null;
                    BuildTaskDetail(task!);
                }
                SetMobileNotificationMessage(result.Message);
            }
            catch
            {
                if (alive && viewRevision == expectedRevision && MobileNotificationSelectionRevision == selectedRevision)
                    SetMobileNotificationMessage("暂时无法读取提醒中的任务，已保留入口，请稍后返回任务清单重试。");
                else replay = HasPendingMobileNotificationTask;
            }
            finally
            {
                openingMobileNotificationTask = false;
                if (replay) SchedulePendingMobileNotificationTask();
            }
        }
    }

    // Post from Layout after the destination page is fully built, so opening a queued task cannot
    // make the original page builder append controls into the new detail page.
    private void SchedulePendingMobileNotificationTask()
    {
        if (!alive || !HasPendingMobileNotificationTask || page is null) return;
        page.Post(() => { if (alive) TryOpenPendingMobileNotificationTask(); });
    }
}
