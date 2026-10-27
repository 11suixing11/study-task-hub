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
    private readonly Dictionary<string, RainSyncService> rainServices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RainConnectionWindow> rainConnections = new(StringComparer.Ordinal);
    private TaskStore? store;
    private bool exiting;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        if (args.Args.Length is not (2 or 4) || args.Args[0] != "--project-root"
            || args.Args.Length == 4 && (args.Args[2] != "--connect-platform" || args.Args[3] != MoocPlatform.Id && !RainPlatform.All.Any(platform => platform.Id == args.Args[3])))
        {
            MessageBox.Show("请使用项目内“启动聚合平台.cmd”启动。", "启动参数缺失");
            Shutdown(1); return;
        }
        var root = Path.GetFullPath(args.Args[1]);
        if (!File.Exists(Path.Combine(root, "NuGet.Config"))
            || !File.Exists(Path.Combine(root, "scripts", "build.ps1"))
            || !File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj")))
        {
            MessageBox.Show("无法确认项目目录，已停止以避免写入其他位置。", "目录检查失败");
            Shutdown(1); return;
        }
        var temporary = Path.Combine(root, ".local", "temp");
        Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("TEMP", temporary);
        Environment.SetEnvironmentVariable("TMP", temporary);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            store = new TaskStore(Path.Combine(root, ".local", "data", "tasks.db"));
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
                if (args.Args.Length == 4) ShowPlatformConnection(args.Args[3]);
            }));
        }
        catch (TaskValidationException error)
        {
            MessageBox.Show(error.Code + "：" + error.Message + "\n请查看 README.md，先备份任务库后处理。", "本地数据加载失败");
            ExitApplication();
        }
        catch
        {
            MessageBox.Show("DB001：无法加载本地任务库。请检查 .local/data 目录是否可写，先备份已有 tasks.db。", "本地数据加载失败");
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
        foreach (var window in rainConnections.Values.ToArray()) window.Close();
        foreach (var service in rainServices.Values) service.Dispose(); rainServices.Clear();
        if (moocSync is not null && !await moocSync.SaveForExitAsync())
            MessageBox.Show("MOOC004：MOOC 登录状态未能保存，下次启动可能需要重新登录。已有任务已保留。",
                "登录状态保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        moocSync?.Dispose(); moocSync = null;
        if (sync is not null && !await sync.SaveForExitAsync())
            MessageBox.Show("SESSION001：登录状态未能保存到本地，下次启动可能需要重新登录。任务已保留；请检查项目目录是否可写。",
                "登录状态保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        sync?.Dispose(); sync = null;
        reminders?.Dispose(); reminders = null;
        dashboard?.CloseForExit(); Shutdown();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        sync?.Dispose(); moocSync?.Dispose(); foreach (var service in rainServices.Values) service.Dispose(); reminders?.Dispose(); base.OnExit(args);
    }
}
