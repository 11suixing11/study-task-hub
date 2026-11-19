using AccessProbe;
using Android.App;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private Task WriteBrowserResponseCookies(string platform, Uri address, IReadOnlyList<MetadataSessionCookie> observed,
        IReadOnlyList<MetadataSessionCookie> changes, CancellationToken cancellation)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(async () =>
        {
            try
            {
                if (!alive) throw new OperationCanceledException("The activity closed.", cancellation);
                await MobilePlatforms.ApplyResponseCookiesAsync(platform, address, observed, changes, cancellation);
                completed.TrySetResult();
            }
            catch (OperationCanceledException) { completed.TrySetCanceled(cancellation); }
            catch (Exception error) { completed.TrySetException(error); }
        });
        return completed.Task;
    }

    private async Task ReopenOfficialLogin(string platform)
    {
        if (web is null || loginPlatform != platform) return;
        if (busy || checkingLogin) { status.Text = "请等待本次登录校验结束后重试。"; return; }
        var target = web;
        var expectedViewRevision = viewRevision;
        bool CurrentLogin() => alive && web == target && loginPlatform == platform && viewRevision == expectedViewRevision;
        try
        {
            DeleteSavedLoginQr(); loginClient?.ResetNavigation(); target.StopLoading();
            checkedLoginCookieBinding = null;
            ResetLoginFeedback();
            if (platform is "xuetang" or "zhihuishu") target.ClearCache(true);
            var saved = await Task.Run(() => vault.LoadForRevalidation(platform));
            if (!CurrentLogin()) return;
            if (saved is not null) await MobilePlatforms.RestoreBrowserAsync(saved);
            if (!CurrentLogin()) return;
            target.LoadUrl(MobilePlatforms.Login(platform).AbsoluteUri);
            status.Text = "正在重新打开官网，请在本页完成登录。";
        }
        catch { if (CurrentLogin()) { status.Text = "官网页面重新加载未完成，请稍后重试。"; ShowLoginPageFailure("environment_failed"); } }
    }

    private void ConfirmResetOfficialLogin(string platform)
    {
        if (web is null || loginPlatform != platform) return;
        if (busy || checkingLogin) { status.Text = "请等待本次登录校验结束后重试。"; return; }
        var originWeb = web; var expectedViewRevision = viewRevision;
        new AlertDialog.Builder(this)!.SetTitle("清理 " + PlatformCatalog.Name(platform) + " 登录")!
            .SetMessage("清理本应用内此平台的登录会话，然后重新打开官网。任务、个人标记和课程选择会保留。")!
            .SetPositiveButton("清理并重试", async (_, _) =>
            {
                if (alive && web == originWeb && loginPlatform == platform && viewRevision == expectedViewRevision)
                    await ResetOfficialLogin(platform);
            })!
            .SetNegativeButton("取消", (_, _) => { })!.Show();
    }

    private async Task ResetOfficialLogin(string platform)
    {
        if (web is null || loginPlatform != platform || busy || checkingLogin) return;
        var target = web; var diagnostics = loginDiagnostics;
        var currentStore = store; var currentVault = vault;
        var expectedViewRevision = viewRevision;
        bool CurrentLogin() => alive && web == target && loginPlatform == platform && viewRevision == expectedViewRevision;
        checkingLogin = true; SetConnectionBusy(true, false);
        try
        {
            loginLifetime?.Cancel(); DeleteSavedLoginQr(); awaitingOfficialApp = false;
            target.StopLoading(); target.LoadUrl("about:blank");
            var saved = await Task.Run(() => currentVault.LoadForRevalidation(platform));
            await MobilePlatforms.ClearBrowserAsync(platform, saved);
            // Finish the confirmed cleanup even when the user leaves this page.
            // New login attempts stay blocked by checkingLogin until cleanup ends.
            await Task.Run(() => currentStore.ClearPlatformSession(platform));
            if (!CurrentLogin()) return;
            checkedLoginCookieBinding = null; checkedLoginNavigation = -1;
            ResetLoginFeedback();
            target.ClearCache(true); loginClient?.ResetNavigation(); diagnostics?.Record("preparation", "cleared");
            target.LoadUrl(MobilePlatforms.Login(platform).AbsoluteUri);
            status.Text = "本平台登录已清理，请在下方官网重新登录。任务和课程选择已保留。";
        }
        catch { if (CurrentLogin()) status.Text = "本平台登录清理未完成，请稍后重试。"; }
        finally
        {
            checkingLogin = false;
            if (CurrentLogin()) SetConnectionBusy(false);
            if (CurrentLogin())
            { loginLifetime?.Dispose(); loginLifetime = new(); _ = MonitorLogin(platform, loginLifetime.Token); }
            SchedulePendingMobileNotificationTask();
        }
    }
}
