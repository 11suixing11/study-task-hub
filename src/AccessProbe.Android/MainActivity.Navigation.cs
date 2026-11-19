using Android.Content;
using Android.Content.PM;
using Android.Views;
using Android.Webkit;
using AccessProbe;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private bool openingTask;
    private CancellationTokenSource? navigationLifetime;
    private string? externalTaskId;

    private async Task OpenTask(string id, bool preferApp)
    {
        if (syntheticUi) { status.Text = "隔离验收模式：已拦截外部任务入口。"; return; }
        if (openingTask || busy || mobileTaskSaving) { status.Text = "请等待当前操作完成。"; return; }
        var revision = viewRevision; var currentStore = store;
        navigationLifetime?.Dispose(); navigationLifetime = new(TimeSpan.FromSeconds(90));
        var cancellation = navigationLifetime.Token; openingTask = true;
        status.Text = "正在核对最新官方任务入口…";
        try
        {
            var task = await Task.Run(() => currentStore.Get(id), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!alive || !resumed || viewRevision != revision || mobileNotificationEditing) return;
            if (task is null) { status.Text = "任务已更新或删除，请刷新清单。"; return; }
            MobileTaskRoute? route = null;
            var navigation = new TaskNavigationService(currentStore,
                (uri, token) => Task.Run(() => sync.ResolveTaskAsync(task.PlatformId, uri, token), token), address =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!alive || !resumed || viewRevision != revision || mobileNotificationEditing) throw new System.OperationCanceledException();
                    route = MobileTaskRouting.Open(task.PlatformId, address, task.CourseName, preferApp, TryOpenTaskApp, TryOpenTaskWeb);
                    if (route.Outcome != "unavailable") externalTaskId = id;
                });
            await navigation.OpenAsync(id, cancellation);
            if (alive && revision == viewRevision) status.Text = route?.Message ?? "官方入口未能打开，请重试。";
        }
        catch (TaskValidationException error) { if (alive && revision == viewRevision) status.Text = error.Code + "：" + error.Message + " 可打开官方课程入口核对。"; }
        catch (System.OperationCanceledException) { if (alive && revision == viewRevision) status.Text = "本次跳转已停止，请重试或打开官方课程入口。"; }
        catch { if (alive && revision == viewRevision) status.Text = "任务入口未能打开，请检查网络；也可打开官方课程入口。"; }
        finally { openingTask = false; SchedulePendingMobileNotificationTask(); }
    }

    private string? TryOpenTaskApp(MobileOfficialApp app, string address)
    {
        try
        {
            using var direct = new Intent(Intent.ActionView, Android.Net.Uri.Parse(address));
            direct.AddCategory(Intent.CategoryBrowsable); direct.SetPackage(app.Package);
            if (PackageManager?.ResolveActivity(direct, PackageInfoFlags.MatchDefaultOnly) is not null)
            { StartActivity(direct); return "task"; }
        }
        catch (Exception error) when (error is ActivityNotFoundException or Java.Lang.SecurityException) { }
        try
        {
            using var launch = PackageManager?.GetLaunchIntentForPackage(app.Package);
            if (launch is null) return null;
            StartActivity(launch); return "home";
        }
        catch (Exception error) when (error is ActivityNotFoundException or Java.Lang.SecurityException) { return null; }
    }

    private bool TryOpenTaskWeb(string address)
    {
        if (!TaskRules.IsSafeLink(address)) return false;
        try
        {
            // Pick a browser explicitly so "打开任务网页" does not launch the same App again.
            using var browserQuery = new Intent(Intent.ActionMain); browserQuery.AddCategory(Intent.CategoryAppBrowser);
            var browsers = PackageManager?.QueryIntentActivities(browserQuery, (PackageInfoFlags)0) ?? [];
            foreach (var browser in browsers)
            {
                if (browser.ActivityInfo?.PackageName is not { } package) continue;
                try
                {
                    using var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(address));
                    intent.AddCategory(Intent.CategoryBrowsable); intent.SetPackage(package); StartActivity(intent); return true;
                }
                catch (Exception error) when (error is ActivityNotFoundException or Java.Lang.SecurityException) { }
            }
        }
        catch (Java.Lang.SecurityException) { }
        return false;
    }

    private async Task OpenCourseFallback(string id)
    {
        if (syntheticUi) { status.Text = "隔离验收模式：已拦截官方课程入口。"; return; }
        var expectedRevision = viewRevision;
        var task = await Task.Run(() => store.Get(id));
        if (!alive || !resumed || viewRevision != expectedRevision || mobileNotificationEditing) return;
        if (task is null) { Dashboard(); return; }
        try
        {
            var opened = TryOpenTaskWeb(MobileTaskRouting.Home(task.PlatformId));
            if (opened) externalTaskId = id;
            status.Text = opened ? "已打开官方课程列表。请进入“" + (task.CourseName.Length > 0 ? task.CourseName : "对应课程") + "”查找“" + task.Title + "”。"
                : "未找到可用浏览器，请打开对应官方 App 的课程列表核对。";
        }
        catch (TaskValidationException error) { status.Text = error.Message; }
    }

    private async Task ResumeTask(string id, Func<string, Task<MobileTaskResumeSnapshot>>? readTask = null)
    {
        if (!alive || !resumed || web is not null || mobileNotificationEditing || HasPendingMobileNotificationTask) return;
        var expectedRevision = viewRevision;
        var notificationRevision = MobileNotificationSelectionRevision;
        bool CurrentResume() => alive && resumed && web is null && !mobileNotificationEditing && viewRevision == expectedRevision
            && MobileNotificationSelectionRevision == notificationRevision && !HasPendingMobileNotificationTask;
        MobileTaskResumeSnapshot snapshot;
        try { snapshot = await (readTask?.Invoke(id) ?? GetMobileUiData().LoadTaskForResumeAsync(id)); }
        catch { if (CurrentResume()) status.Text = "任务暂时无法刷新，请返回清单后重试。"; return; }
        if (!CurrentResume() || snapshot.Task is not { } task) return;
        TaskDetail(id, "已返回任务。请核对官网提交状态，或点击“刷新任务状态”。");
        var detailLoadingRevision = viewRevision; await mobileTaskDetailLoad;
        if (!alive || !resumed || web is not null || mobileNotificationEditing || HasPendingMobileNotificationTask
            || MobileNotificationSelectionRevision != notificationRevision || viewRevision > detailLoadingRevision + 1 || mobileShownTaskId != id) return;
        if (snapshot.ShouldSync && !busy)
        {
            var revision = viewRevision;
            await RunSync(task.PlatformId, false, false);
            if (alive && resumed && revision == viewRevision && web is null && !mobileNotificationEditing && !HasPendingMobileNotificationTask
                && MobileNotificationSelectionRevision == notificationRevision)
            { var message = status.Text; TaskDetail(id, message); await mobileTaskDetailLoad; }
        }
    }

    private async Task RefreshTask(string id)
    {
        var revision = viewRevision; var notificationRevision = MobileNotificationSelectionRevision;
        var task = await Task.Run(() => store.Get(id));
        if (!alive || !resumed || revision != viewRevision || web is not null || mobileNotificationEditing
            || HasPendingMobileNotificationTask || MobileNotificationSelectionRevision != notificationRevision) return;
        if (task is null) { Dashboard(); return; }
        await RunSync(task.PlatformId, false, false);
        if (alive && resumed && revision == viewRevision && web is null && !mobileNotificationEditing && !HasPendingMobileNotificationTask
            && MobileNotificationSelectionRevision == notificationRevision)
        { var message = status.Text; TaskDetail(id, message); await mobileTaskDetailLoad; }
    }
}
