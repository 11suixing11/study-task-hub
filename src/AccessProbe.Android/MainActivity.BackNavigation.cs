using Android.OS;
using Android.Widget;
using Android.Window;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private readonly MobileBackNavigation backNavigation = new();
    private SystemBackCallback? systemBackCallback;
    private Toast? exitHint;

    private void RegisterSystemBack()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu || systemBackCallback is not null) return;
        systemBackCallback = new(() => HandleBackNavigation("dispatcher"));
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(IOnBackInvokedDispatcher.PriorityDefault, systemBackCallback);
    }

    private void ReleaseSystemBack()
    {
        ResetExitConfirmation();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && systemBackCallback is { } callback)
        {
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(callback);
            callback.Dispose(); systemBackCallback = null;
        }
    }

    private void ResetExitConfirmation()
    {
        backNavigation.Reset(); exitHint?.Cancel(); exitHint?.Dispose(); exitHint = null;
    }

    private void HandleSystemBack() => HandleBackNavigation("toolbar");

    private void HandleBackNavigation(string source)
    {
        if (!alive || IsFinishing) return;
        var action = backNavigation.Handle(store is not null && (pageBack is not null || loginPlatform is not null),
            rootPage == "tasks", SystemClock.ElapsedRealtime());
        switch (action)
        {
            case MobileBackAction.Parent:
                ResetExitConfirmation();
                if (loginPlatform is { } platform) { work?.Cancel(); CloseLogin(platform); }
                else pageBack?.Invoke();
                break;
            case MobileBackAction.TaskHome:
                ResetExitConfirmation(); Dashboard();
                break;
            case MobileBackAction.PromptExit:
                exitHint?.Cancel(); exitHint?.Dispose();
                exitHint = Toast.MakeText(this, "再按一次返回键退出", ToastLength.Short);
                exitHint?.Show();
                break;
            case MobileBackAction.Exit:
                ResetExitConfirmation();
                navigationLifetime?.Cancel(); work?.Cancel(); periodic?.Cancel();
                alive = false; resumed = false;
                FinishAndRemoveTask();
                break;
        }
        RecordSyntheticBackEvent(action, source);
    }

    private sealed class SystemBackCallback(Action action) : Java.Lang.Object, IOnBackInvokedCallback
    {
        public void OnBackInvoked() => action();
    }
}
