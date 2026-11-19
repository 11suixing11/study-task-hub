using System.IO;
using System.Windows;

namespace AccessProbe;

public partial class App : Application
{
    private TaskDashboardWindow? dashboard;
    private ReminderService? reminders;
    private ChaoxingSyncService? sync;
    private MoocSyncService? moocSync;
    private XuetangSyncService? xuetangSync;
    private readonly Dictionary<string, RainSyncService> rainServices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetadataSyncService> metadataServices = new(StringComparer.Ordinal);
    private TaskStore? store;
    private string? applicationRoot;
    private bool exiting;
    private Mutex? instanceMutex;
    private bool instanceHeld;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        if (args.Args.Length == 4 && args.Args[0] == "--import-project" && args.Args[2] == "--data-root")
        {
            try { InstallationDataImport.Import(args.Args[1], args.Args[3]); Shutdown(); }
            catch { Shutdown(12); }
            return;
        }
        if (args.Args.Length == 2 && args.Args[0] == "--verify-installation")
        {
            try
            {
                var check = ApplicationPaths.Resolve(["--data-root", args.Args[1]], AppContext.BaseDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                var verifiedStore = new TaskStore(check.Database);
                var resources = Resources.MergedDictionaries.Count;
                File.WriteAllText(Path.Combine(check.Root, "installation-checks.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { outcome = "passed", installed = check.Installed,
                        runtime = Environment.Version.ToString(), tasks = verifiedStore.LoadTasks().Count, resources,
                        officialNetworkAccessed = false, productionDataAccessed = false }));
                Shutdown();
            }
            catch { Shutdown(13); }
            return;
        }
        ApplicationLocation location;
        try
        {
            location = ApplicationPaths.Resolve(args.Args, AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            instanceMutex = new Mutex(false, location.InstanceMutex);
            try { instanceHeld = instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { instanceHeld = true; }
            if (!instanceHeld)
            {
                MessageBox.Show("聚合平台已在运行。请从托盘打开首页；更新版本前先点击首页“退出程序”。", "聚合平台已启动");
                Shutdown(); return;
            }
        }
        catch (TaskValidationException error)
        {
            MessageBox.Show(error.Code + "：" + error.Message, "启动失败");
            Shutdown(1); return;
        }
        var root = location.Root;
        applicationRoot = root;
        var temporary = Path.Combine(root, ".local", "temp");
        Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("TEMP", temporary);
        Environment.SetEnvironmentVariable("TMP", temporary);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            store = new TaskStore(location.Database);
            store.RefreshAutomaticMetadata(DateTimeOffset.UtcNow);
            dashboard = new TaskDashboardWindow(store, root, () => ShowConnection(root), ExitApplication);
            MainWindow = dashboard; dashboard.Show();
            try
            {
                reminders = new ReminderService(store, () => dashboard.ShowDashboard(), ExitApplication);
                dashboard.AttachReminders(reminders);
            }
            catch { dashboard.ShowError("NOTIFY001", "托盘提醒初始化失败。任务管理仍可用；关闭窗口将退出，请重新启动后检查 Windows 通知。"); }
            dashboard.NotificationSettingChanged += enabled => { if (enabled) reminders?.Tick(); };
            sync = new ChaoxingSyncService(store, root,
                (Microsoft.Web.WebView2.Wpf.WebView2)dashboard.FindName("SessionBrowser"));
            dashboard.AttachSync(sync);
            dashboard.AutomaticSyncSettingChanged += sync.SettingsChanged;
            foreach (var platform in RainPlatform.All)
            {
                var name = platform.Id == "yuketang" ? "RainSessionBrowser" : "ChangjiangSessionBrowser";
                var service = new RainSyncService(store, root, platform,
                    (Microsoft.Web.WebView2.Wpf.WebView2)dashboard.FindName(name));
                rainServices.Add(platform.Id, service); dashboard.AttachRainSync(service);
                dashboard.AutomaticSyncSettingChanged += service.SettingsChanged;
            }
            moocSync = new MoocSyncService(store, root,
                (Microsoft.Web.WebView2.Wpf.WebView2)dashboard.FindName("MoocSessionBrowser"));
            dashboard.AttachMoocSync(moocSync); dashboard.AutomaticSyncSettingChanged += moocSync.SettingsChanged;
            xuetangSync = new XuetangSyncService(store, root,
                (Microsoft.Web.WebView2.Wpf.WebView2)dashboard.FindName("XuetangSessionBrowser"));
            dashboard.AttachXuetangSync(xuetangSync); dashboard.AutomaticSyncSettingChanged += xuetangSync.SettingsChanged;
            foreach (var platform in MetadataPlatform.Enabled)
            {
                var service = new MetadataSyncService(platform, store, root,
                    (Microsoft.Web.WebView2.Wpf.WebView2)dashboard.FindName(MetadataBrowserName(platform.Id)));
                metadataServices.Add(platform.Id, service); dashboard.AttachMetadataSync(service);
                dashboard.AutomaticSyncSettingChanged += service.SettingsChanged;
            }
            dashboard.PlatformConnectionRequested += ShowPlatformConnection;
            dashboard.Closed += (_, _) => { if (!exiting) ExitApplication(); };
            var settings = store.LoadSettings();
            var firstUse = settings.DisclaimerSeen ? Task.CompletedTask : ShowFirstUseAsync();
            reminders?.Tick();
            dashboard.Dispatcher.BeginInvoke(new Action(async () =>
            {
                sync?.StartAutomatic();
                foreach (var service in rainServices.Values) service.StartAutomatic();
                moocSync?.StartAutomatic();
                xuetangSync?.StartAutomatic();
                foreach (var service in metadataServices.Values) service.StartAutomatic();
                await firstUse;
                if (!exiting && dashboard.ActivePage is null && location.ConnectPlatform is { } platform) ShowPlatformConnection(platform);
            }));
        }
        catch (TaskValidationException error)
        {
            MessageBox.Show(error.Code + "：" + error.Message + "\n请先备份任务库，并查看使用说明。", "本地数据加载失败");
            ExitApplication();
        }
        catch
        {
            MessageBox.Show("DB001：无法加载本地任务库。请检查数据目录是否可写，先备份已有 tasks.db。\n" + location.Database, "本地数据加载失败");
            ExitApplication();
        }
    }

    private void ShowRainConnection(string platformId)
    {
        if (exiting || dashboard is null || !rainServices.TryGetValue(platformId, out var service)) return;
        dashboard.ShowConnectionPage(platformId, () => new RainConnectionPage(service, dashboard.ShowDashboard));
    }

    private void ShowPlatformConnection(string platformId)
    {
        if (platformId == "chaoxing" && applicationRoot is not null) { ShowConnection(applicationRoot); return; }
        if (metadataServices.ContainsKey(platformId)) { ShowMetadataConnection(platformId); return; }
        if (platformId == XuetangPlatform.Id) { ShowXuetangConnection(); return; }
        if (platformId != MoocPlatform.Id) { ShowRainConnection(platformId); return; }
        if (exiting || dashboard is null || moocSync is null) return;
        dashboard.ShowConnectionPage("mooc", () => new MoocConnectionPage(moocSync, dashboard.ShowDashboard));
    }

    private void ShowXuetangConnection()
    {
        if (exiting || dashboard is null || xuetangSync is null) return;
        dashboard.ShowConnectionPage("xuetang", () => new XuetangConnectionPage(xuetangSync, dashboard.ShowDashboard));
    }

    private static string MetadataBrowserName(string platformId) => platformId switch
    { "ucampus" => "UcampusSessionBrowser", "fif" => "FifSessionBrowser", "pta" => "PtaSessionBrowser", _ => "ZhihuishuSessionBrowser" };
    private void ShowMetadataConnection(string platformId)
    {
        if (exiting || dashboard is null || !metadataServices.TryGetValue(platformId, out var service)) return;
        dashboard.ShowConnectionPage(platformId, () => new MetadataConnectionPage(service, dashboard.ShowDashboard));
    }

    private void ShowConnection(string root)
    {
        if (exiting || store is null || dashboard is null) return;
        dashboard.ShowConnectionPage("chaoxing", () =>
        {
            var page = new ChaoxingConnectionPage(root, store, dashboard.ShowDashboard, sync);
            page.OfficialNavigationCompleted += address => sync?.LoginNavigationCompleted(address);
            page.SessionClearing += () => sync?.SessionClearing();
            page.SessionClearCompleted += succeeded => sync?.SessionClearCompleted(succeeded);
            if (sync is { } service)
            {
                service.StatusChanged += page.ShowSyncStatusForPage;
                page.Closed += (_, _) => service.StatusChanged -= page.ShowSyncStatusForPage;
            }
            return page;
        });
    }

    private async Task ShowFirstUseAsync()
    {
        if (dashboard is null || store is null) return;
        if (await dashboard.ConfirmAsync("首次使用说明", TaskRules.Disclaimer, "开始使用", cancel: null))
            store.SaveSettings(store.LoadSettings() with { DisclaimerSeen = true });
    }

    private async void ExitApplication()
    {
        if (exiting) return; exiting = true;
        if (dashboard is not null)
        {
            dashboard.ShowSuccess("正在保存登录状态并退出，请稍候……");
            dashboard.IsEnabled = false;
        }
        dashboard?.DisposeWorkspaceForExit();
        var persistenceWarnings = new List<string>();
        foreach (var service in rainServices.Values)
        {
            if (!await service.SaveForExitAsync()) persistenceWarnings.Add(service.Platform.Name + "：登录未能保存，下次可能需要重新登录。");
            service.Dispose();
        }
        rainServices.Clear();
        if (moocSync is not null && !await moocSync.SaveForExitAsync())
            persistenceWarnings.Add("MOOC004：中国大学 MOOC 登录状态未能保存，下次可能需要重新登录。");
        moocSync?.Dispose(); moocSync = null;
        if (xuetangSync is not null && !await xuetangSync.SaveForExitAsync())
            persistenceWarnings.Add("XT004：学堂在线登录状态未能保存，下次可能需要重新登录。");
        xuetangSync?.Dispose(); xuetangSync = null;
        foreach (var service in metadataServices.Values)
        {
            if (!await service.SaveForExitAsync())
                persistenceWarnings.Add(service.Platform.CodePrefix + "004：" + service.Platform.Name + "登录状态未能保存，下次可能需要重新登录。");
            service.Dispose();
        }
        metadataServices.Clear();
        if (sync is not null && !await sync.SaveForExitAsync())
            persistenceWarnings.Add("SESSION001：学习通登录状态未能保存，下次可能需要重新登录；请检查项目目录是否可写。");
        sync?.Dispose(); sync = null;
        reminders?.Dispose(); reminders = null;
        if (persistenceWarnings.Count > 0 && dashboard is { IsVisible: true })
            await dashboard.ShowExitNoticeAsync(string.Join("\n", persistenceWarnings) + "\n\n已有任务已保留。");
        dashboard?.CloseForExit(); Shutdown();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        sync?.Dispose(); moocSync?.Dispose(); xuetangSync?.Dispose(); foreach (var service in rainServices.Values) service.Dispose();
        foreach (var service in metadataServices.Values) service.Dispose(); reminders?.Dispose(); base.OnExit(args);
        if (instanceHeld) { instanceMutex?.ReleaseMutex(); instanceHeld = false; }
        instanceMutex?.Dispose(); instanceMutex = null;
    }
}
