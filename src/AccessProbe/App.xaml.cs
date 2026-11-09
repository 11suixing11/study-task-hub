using System.IO;
using System.Windows;

namespace AccessProbe;

public partial class App : Application
{
    private TaskDashboardWindow? dashboard;
    private MainWindow? connectionWindow;
    private ReminderService? reminders;
    private ChaoxingSyncService? sync;
    private MoocSyncService? moocSync;
    private MoocConnectionWindow? moocConnection;
    private XuetangSyncService? xuetangSync;
    private XuetangConnectionWindow? xuetangConnection;
    private readonly Dictionary<string, RainSyncService> rainServices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RainConnectionWindow> rainConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetadataSyncService> metadataServices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetadataConnectionWindow> metadataConnections = new(StringComparer.Ordinal);
    private TaskStore? store;
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
            sync.StatusChanged += (message, running, warning) => connectionWindow?.ShowSyncStatus(message, warning);
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
            if (!settings.DisclaimerSeen)
            {
                MessageBox.Show(dashboard, TaskRules.Disclaimer, "首次使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
                store.SaveSettings(settings with { DisclaimerSeen = true });
            }
            reminders?.Tick();
            dashboard.Dispatcher.BeginInvoke(new Action(() =>
            {
                sync?.StartAutomatic();
                foreach (var service in rainServices.Values) service.StartAutomatic();
                moocSync?.StartAutomatic();
                xuetangSync?.StartAutomatic();
                foreach (var service in metadataServices.Values) service.StartAutomatic();
                if (location.ConnectPlatform is { } platform) ShowPlatformConnection(platform);
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
        if (!rainConnections.TryGetValue(platformId, out var window))
        {
            window = new RainConnectionWindow(service, () => dashboard.ShowDashboard());
            rainConnections.Add(platformId, window);
            window.Closed += (_, _) => rainConnections.Remove(platformId);
        }
        else service.ConnectionOpened();
        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
    }

    private void ShowPlatformConnection(string platformId)
    {
        if (metadataServices.ContainsKey(platformId)) { ShowMetadataConnection(platformId); return; }
        if (platformId == XuetangPlatform.Id) { ShowXuetangConnection(); return; }
        if (platformId != MoocPlatform.Id) { ShowRainConnection(platformId); return; }
        if (exiting || dashboard is null || moocSync is null) return;
        if (moocConnection is null)
        {
            moocConnection = new MoocConnectionWindow(moocSync, () => dashboard.ShowDashboard());
            moocConnection.Closed += (_, _) => moocConnection = null;
        }
        else moocSync.ConnectionOpened();
        moocConnection.Show(); moocConnection.WindowState = WindowState.Normal; moocConnection.Activate();
    }

    private void ShowXuetangConnection()
    {
        if (exiting || dashboard is null || xuetangSync is null) return;
        if (xuetangConnection is null)
        {
            xuetangConnection = new XuetangConnectionWindow(xuetangSync, () => dashboard.ShowDashboard());
            xuetangConnection.Closed += (_, _) => xuetangConnection = null;
        }
        else xuetangSync.ConnectionOpened();
        xuetangConnection.Show(); xuetangConnection.WindowState = WindowState.Normal; xuetangConnection.Activate();
    }

    private static string MetadataBrowserName(string platformId) => platformId switch
    { "ucampus" => "UcampusSessionBrowser", "fif" => "FifSessionBrowser", "pta" => "PtaSessionBrowser", _ => "ZhihuishuSessionBrowser" };
    private void ShowMetadataConnection(string platformId)
    {
        if (exiting || dashboard is null || !metadataServices.TryGetValue(platformId, out var service)) return;
        if (!metadataConnections.TryGetValue(platformId, out var window))
        {
            window = new MetadataConnectionWindow(service, () => dashboard.ShowDashboard());
            metadataConnections.Add(platformId, window); window.Closed += (_, _) => metadataConnections.Remove(platformId);
        }
        else service.ConnectionOpened();
        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
    }

    private void ShowConnection(string root)
    {
        if (exiting || store is null || dashboard is null) return;
        sync?.LoginConnectionOpened();
        if (connectionWindow is null)
        {
            connectionWindow = new MainWindow(root, store, () => dashboard.ShowDashboard(), sync);
            connectionWindow.OfficialNavigationCompleted += address => sync?.LoginNavigationCompleted(address);
            connectionWindow.SessionClearing += () => sync?.SessionClearing();
            connectionWindow.SessionClearCompleted += succeeded => sync?.SessionClearCompleted(succeeded);
            connectionWindow.Closed += (_, _) => { sync?.LoginConnectionClosed(); connectionWindow = null; };
        }
        connectionWindow.Show(); connectionWindow.WindowState = WindowState.Normal; connectionWindow.Activate();
    }

    private async void ExitApplication()
    {
        if (exiting) return; exiting = true;
        if (dashboard is not null)
        {
            dashboard.ShowSuccess("正在保存登录状态并退出，请稍候……");
            dashboard.IsEnabled = false;
        }
        connectionWindow?.Close();
        moocConnection?.Close();
        xuetangConnection?.Close();
        foreach (var window in metadataConnections.Values.ToArray()) window.Close();
        foreach (var window in rainConnections.Values.ToArray()) window.Close();
        foreach (var service in rainServices.Values)
        {
            if (!await service.SaveForExitAsync()) dashboard?.ShowError("SESSION001", service.Platform.Name + "登录未能保存，下次可能需要重新登录。");
            service.Dispose();
        }
        rainServices.Clear();
        if (moocSync is not null && !await moocSync.SaveForExitAsync())
            MessageBox.Show("MOOC004：MOOC 登录状态未能保存，下次启动可能需要重新登录。已有任务已保留。",
                "登录状态保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        moocSync?.Dispose(); moocSync = null;
        if (xuetangSync is not null && !await xuetangSync.SaveForExitAsync())
            MessageBox.Show("XT004：学堂在线登录状态未能保存，下次启动可能需要重新登录。已有任务已保留。",
                "登录状态保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        xuetangSync?.Dispose(); xuetangSync = null;
        foreach (var service in metadataServices.Values)
        {
            if (!await service.SaveForExitAsync())
                MessageBox.Show(service.Platform.CodePrefix + "004：" + service.Platform.Name + "登录状态未能保存，下次启动可能需要重新登录。已有任务保留。",
                    "登录状态保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            service.Dispose();
        }
        metadataServices.Clear();
        if (sync is not null && !await sync.SaveForExitAsync())
            MessageBox.Show("SESSION001：登录状态未能保存到本地，下次启动可能需要重新登录。任务已保留；请检查项目目录是否可写。",
                "登录状态保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        sync?.Dispose(); sync = null;
        reminders?.Dispose(); reminders = null;
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
