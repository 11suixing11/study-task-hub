using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AccessProbe;
using System.Security.Cryptography;
using OperationCanceledException = System.OperationCanceledException;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private MobileUiData? mobileUiData;
    private CancellationTokenSource? mobileInitializationLifetime;
    private int mobileInitializationRevision;
    private bool mobileDataReady;
    private Exception? mobileInitializationFailure;
    internal Exception? MobileInitializationFailure => mobileInitializationFailure;

    private MobileUiData GetMobileUiData()
    {
        if (store is null || vault is null) throw new InvalidOperationException("Mobile data is not initialized.");
        AttachMobileStoreUpdates();
        if (mobileUiData is not null) return mobileUiData;
        // Synthetic entry points initialize their own isolated store and use this same
        // service lazily; this method itself performs no database or credential read.
        mobileDataReady = true;
        return mobileUiData = new(store, vault, MobilePlatforms.All);
    }

    private void BeginInitializeMobileData()
    {
        if (!alive || IsFinishing || mobileDataReady) return;
        mobileInitializationLifetime?.Cancel();
        var lifetime = new CancellationTokenSource();
        mobileInitializationLifetime = lifetime;
        var initializationRevision = ++mobileInitializationRevision;
        mobileInitializationFailure = null;
        taskEditorOpen = false; selectedPlatform = null; rootPage = "tasks";
        Layout("学习任务", "正在准备本地任务…");
        Text(page, "正在读取本地任务和平台连接。", 14);
        page.AddView(new ProgressBar(this), new LinearLayout.LayoutParams(-2, -2));
        _ = InitializeMobileDataAsync(lifetime, initializationRevision, viewRevision);
    }

    // Called by BeginInitializeMobileData on the UI thread. Await resumes there before
    // accessing Activity views; the worker only constructs services and reads local data.
    private async Task InitializeMobileDataAsync(CancellationTokenSource lifetime, int initializationRevision, int expectedViewRevision)
    {
        var cancellation = lifetime.Token;
        var context = ApplicationContext ?? this;
        MobileDataInitialization? initialized = null;
        var adopted = false;
        bool CurrentInitialization() => alive && !IsFinishing && !IsDestroyed && !cancellation.IsCancellationRequested
            && ReferenceEquals(mobileInitializationLifetime, lifetime) && mobileInitializationRevision == initializationRevision
            && (adopted || viewRevision == expectedViewRevision);
        try
        {
            initialized = await Task.Run(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var protector = new AndroidDataProtector();
                var loadedStore = MobileData.Open(context, protector);
                cancellation.ThrowIfCancellationRequested();
                var loadedVault = new MobileVault(loadedStore, protector);
                var loadedSync = new MobileSync(loadedStore, loadedVault, writeBrowserCookies: WriteBrowserResponseCookies);
                var loadedUiData = new MobileUiData(loadedStore, loadedVault, MobilePlatforms.All);
                try
                {
                    // Preferences and recorded MediaStore cleanup do not touch views.
                    CleanupOrphanedLoginQr();
                    await loadedUiData.LoadDashboardAsync(cancellation: cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    return new MobileDataInitialization(loadedStore, loadedVault, loadedSync, loadedUiData);
                }
                catch { loadedUiData.Dispose(); throw; }
            }, cancellation);
            if (!CurrentInitialization()) return;

            mobileUiData?.Dispose();
            store = initialized.Store; vault = initialized.Vault; sync = initialized.Sync; mobileUiData = initialized.UiData;
            mobileDataReady = true;
            adopted = true;
            Dashboard();
            OpenNotificationTask(Intent);
            ScheduleReminders();
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
                RequestMobileNotificationPermission();
            periodic?.Cancel(); periodic?.Dispose();
            periodic = new();
            _ = ForegroundSyncLoop(periodic.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!CurrentInitialization()) return;
            mobileDataReady = false;
            mobileInitializationFailure = error;
            if (adopted) { mobileUiData?.Dispose(); mobileUiData = null; adopted = false; }
            Layout("本地任务加载未完成", "请检查提示后重试。");
            var explanation = error switch
            {
                TaskValidationException validation => validation.Message + "（" + validation.Code + "）",
                UnauthorizedAccessException => "无法访问应用本地数据，请确认应用存储可用后重试。",
                IOException => "本地文件暂时无法读取，请检查存储空间后重试。",
                Microsoft.Data.Sqlite.SqliteException => "本地任务库暂时无法打开，请稍后重试。",
                CryptographicException => "保存的登录环境暂时无法读取，请重试。",
                _ => "本地任务准备未完成，请重试；再次失败时可反馈故障代码 LOCAL001。"
            };
            Text(page, explanation, 14);
            Text(page, "本次未完成加载，可再次尝试读取。", 13);
            Button(page, "重新加载", BeginInitializeMobileData);
        }
        finally
        {
            if (!adopted) initialized?.UiData.Dispose();
            if (ReferenceEquals(mobileInitializationLifetime, lifetime)) mobileInitializationLifetime = null;
            lifetime.Dispose();
        }
    }

    private void ReleaseMobileUiData()
    {
        mobileInitializationRevision++;
        var lifetime = mobileInitializationLifetime;
        mobileInitializationLifetime = null;
        lifetime?.Cancel();
        // The initialization task disposes its own cancellation source after unwinding.
        mobileUiData?.Dispose(); mobileUiData = null;
        mobileDataReady = false;
    }

    private sealed record MobileDataInitialization(TaskStore Store, MobileVault Vault, MobileSync Sync, MobileUiData UiData);
}
