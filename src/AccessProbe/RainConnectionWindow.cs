using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class RainConnectionPage : WorkspacePage
{
    private readonly RainSyncService service;
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly ComboBox hostChoice = new() { MinWidth = 210, Padding = new Thickness(8, 5, 8, 5) };
    private readonly Button login = new Wpf.Ui.Controls.Button { Content = "打开官方登录", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Padding = new Thickness(14, 7, 14, 7), IsEnabled = false };
    private bool initialized;
    private bool initializing;
    private bool closing;
    private bool changingHost;
    private string? pageFailureMessage;
    private readonly ConnectionStatusPanel connection;

    public RainConnectionPage(RainSyncService service, Action showDashboard)
    {
        this.service = service;
        Title = "连接" + service.Platform.Name + " · 官方登录";
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AccessProbe;component/FluentTheme.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "连接" + service.Platform.Name, FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "请在右侧官网完成登录。扫码后确认官方授权，左侧会显示接入结果；确认接入后选择课程，任务将汇入首页。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 12)
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        hostChoice.ItemsSource = service.Platform.Hosts;
        hostChoice.SelectedItem = service.Host;
        hostChoice.Margin = new Thickness(0, 0, 10, 0);
        System.Windows.Automation.AutomationProperties.SetName(hostChoice, "官方站点");
        hostChoice.Visibility = service.Platform.Hosts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        buttons.Children.Add(hostChoice); buttons.Children.Add(login);
        login.Click += (_, _) => NavigateLogin();
        var dashboard = new Wpf.Ui.Controls.Button { Content = "回到任务清单", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        dashboard.Click += (_, _) => showDashboard(); buttons.Children.Add(dashboard);
        var clear = new Wpf.Ui.Controls.Button { Content = "清理登录后重试", Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        clear.Click += async (_, _) =>
        {
            if (!initialized || changingHost) return;
            changingHost = true; clear.IsEnabled = login.IsEnabled = hostChoice.IsEnabled = false;
            try
            {
                browser.CoreWebView2.Stop(); browser.CoreWebView2.Navigate("about:blank");
                await service.ClearSessionAsync();
                if (!closing) { connection!.Reset(); NavigateLogin(); if (IsPageActive) service.ConnectionOpened(); }
            }
            catch { if (!closing) status.Text = "RAIN002：重新连接未完成，请稍后重试。已保存任务保留。"; }
            finally
            {
                changingHost = false;
                if (!closing) clear.IsEnabled = login.IsEnabled = hostChoice.IsEnabled = true;
            }
        };
        buttons.Children.Add(clear); header.Children.Add(buttons);
        connection = new ConnectionStatusPanel(async () =>
        {
            if (!initialized) { connection!.Show(ConnectionFeedback.PageFailed("session_unavailable")); return; }
            await service.RefreshCourseCatalogAsync();
        }, service.RequestCourseSelection, service.Connection);
        header.Children.Add(connection); service.ConnectionChanged += ShowConnection;
        status.Text = "正在准备官方登录…"; status.Margin = new Thickness(0, 0, 0, 12); header.Children.Add(status);
        Content = ConnectionWindowLayout.Create(header, browser);
        hostChoice.SelectionChanged += async (_, _) =>
        {
            if (!initialized || changingHost || hostChoice.SelectedItem is not string host) return;
            changingHost = true; hostChoice.IsEnabled = login.IsEnabled = false;
            try { await service.SelectHostAsync(host); if (!closing) { connection.Reset(); NavigateLogin(); } }
            catch { if (!closing) status.Text = "RAIN002：切换官方站点失败，请重新连接。"; }
            finally { changingHost = false; if (!closing) hostChoice.IsEnabled = login.IsEnabled = true; }
        };
        service.StatusChanged += UpdateStatus;
        Loaded += Initialize;
        Activated += (_, _) => service.ConnectionOpened();
        Deactivated += (_, _) => service.ConnectionClosed();
        Closed += (_, _) => { closing = true; service.StatusChanged -= UpdateStatus; service.ConnectionChanged -= ShowConnection; service.ConnectionClosed(); browser.Dispose(); };
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (initialized || initializing || closing) return;
        initializing = true;
        try
        {
            var environment = await service.EnsureSessionAsync();
            if (closing) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (closing) return;
            var core = browser.CoreWebView2; RainSyncService.ConfigureCore(core);
            core.ServerCertificateErrorDetected += (_, certificate) =>
            {
                certificate.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                ShowPageFailure("RAIN002：官方页面证书校验失败，请检查设备时间和网络后重试。", "certificate_failed");
            };
            core.ProcessFailed += (_, _) => ShowPageFailure("RAIN002：登录页面运行异常，请返回后重新打开连接页。", "renderer_failed");
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.Document,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                if (Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttp)
                    request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "HTTPS Required", "Cache-Control: no-store\r\n");
            };
            core.NavigationStarting += (_, navigation) =>
            {
                if (!RainAddressPolicy.IsNavigation(service.Platform, navigation.Uri))
                {
                    navigation.Cancel = true;
                    ShowPageFailure("RAIN003：官方页面尝试打开其他站点，当前连接暂未适配该跳转。", "unsupported_redirect");
                }
            };
            core.FrameNavigationStarting += (_, navigation) =>
            {
                if (!RainLoginFrameAllowed(navigation.Uri))
                { navigation.Cancel = true; ShowPageFailure("RAIN003：官网登录组件的跳转尚未适配，请使用官网其他登录方式。", "unsupported_redirect"); }
            };
            core.NewWindowRequested += (_, request) =>
            {
                request.Handled = true;
                var address = request.Uri;
                if (RainAddressPolicy.IsNavigation(service.Platform, address))
                    Dispatcher.BeginInvoke(new Action(() => { if (!closing && !changingHost) core.Navigate(address); }));
                else ShowPageFailure("RAIN003：此登录跳转暂未适配，请使用当前官方页面的扫码或账号登录。", "unsupported_redirect");
            };
            core.NavigationCompleted += (_, navigation) =>
            {
                if (closing) return;
                if (navigation.IsSuccess && navigation.HttpStatusCode < 400)
                {
                    service.LoginNavigationCompleted(core.Source);
                    if (pageFailureMessage is null) status.Text = "官方页面已加载。扫码后请确认授权；接入结果会显示在上方。";
                }
                else if (navigation.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    ShowPageFailure("RAIN002：官方页面加载失败（" + navigation.WebErrorStatus + "），请检查网络后重试。",
                        ConnectionPageFailures.Outcome(navigation.WebErrorStatus, navigation.HttpStatusCode));
                }
            };
            initialized = true; login.IsEnabled = true;
            NavigateLogin();
        }
        catch { ShowPageFailure("RAIN002：登录环境初始化失败，请检查 WebView2 和项目目录权限。", "environment_failed"); }
        finally { initializing = false; }
    }

    private static bool RainLoginFrameAllowed(string address) => address == "about:blank"
        || TaskRules.IsSafeLink(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && (uri.IdnHost.Equals("open.weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.Equals("yuketang.cn", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith(".yuketang.cn", StringComparison.OrdinalIgnoreCase));

    private void NavigateLogin()
    {
        if (!initialized || closing) return;
        pageFailureMessage = null; connection.ClearPageFailure(); status.Visibility = Visibility.Visible;
        browser.CoreWebView2.Navigate(service.Platform.LoginUri(service.Host).AbsoluteUri);
        status.Text = "请在官方页面完成登录，程序会自动检查登录状态。";
    }

    private void UpdateStatus(string message, bool running, bool warning)
    {
        if (closing) return;
        connection.SetRunning(running);
        status.Text = pageFailureMessage ?? message; status.Foreground = pageFailureMessage is not null || warning ? Brushes.Firebrick : Brushes.DimGray;
        status.Visibility = pageFailureMessage is null && !running && service.Connection is { } report && message == ConnectionFeedback.From(report).Text ? Visibility.Collapsed : Visibility.Visible;
        if (!running && warning && pageFailureMessage is null && !connection.HasResultForRun) connection.ShowFailure(message);
    }
    private void ShowPageFailure(string message, string outcome)
    {
        if (closing) return;
        if (outcome is "environment_failed" or "renderer_failed") CanReuse = false;
        pageFailureMessage = message; status.Text = message; status.Foreground = Brushes.Firebrick; status.Visibility = Visibility.Visible;
        connection.ShowPageFailure(outcome, message);
    }
    private void ShowConnection(ConnectionReport report) { if (!closing) connection.Show(report); }
}
