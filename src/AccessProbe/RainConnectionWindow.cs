using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class RainConnectionWindow : Window
{
    private readonly RainSyncService service;
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly ComboBox hostChoice = new() { MinWidth = 210, Padding = new Thickness(8, 5, 8, 5) };
    private readonly Button login = new() { Content = "打开官方登录", Padding = new Thickness(14, 7, 14, 7), IsEnabled = false };
    private bool initialized;
    private bool closing;
    private bool changingHost;

    public RainConnectionWindow(RainSyncService service, Action showDashboard)
    {
        this.service = service;
        Title = "连接" + service.Platform.Name + " · 官方登录";
        Width = 1080; Height = 820; MinWidth = 780; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "连接" + service.Platform.Name, FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "请在下方官方页面完成登录。登录后自动读取课程与任务，可回到清单查看；做题和提交通过系统浏览器进入官方页面。",
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
        var dashboard = new Button { Content = "回到任务清单", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        dashboard.Click += (_, _) => showDashboard(); buttons.Children.Add(dashboard);
        var clear = new Button { Content = "重新连接", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        clear.Click += async (_, _) =>
        {
            if (!initialized || changingHost) return;
            changingHost = true; clear.IsEnabled = login.IsEnabled = hostChoice.IsEnabled = false;
            try
            {
                browser.CoreWebView2.Stop(); browser.CoreWebView2.Navigate("about:blank");
                await service.ClearSessionAsync();
                if (!closing) { NavigateLogin(); service.ConnectionOpened(); }
            }
            catch { if (!closing) status.Text = "RAIN002：重新连接未完成，请稍后重试。已保存任务保留。"; }
            finally
            {
                changingHost = false;
                if (!closing) clear.IsEnabled = login.IsEnabled = hostChoice.IsEnabled = true;
            }
        };
        buttons.Children.Add(clear); header.Children.Add(buttons);
        status.Text = "正在准备官方登录…"; status.Margin = new Thickness(0, 0, 0, 12); header.Children.Add(status);
        root.Children.Add(header); root.Children.Add(browser); Content = root;
        hostChoice.SelectionChanged += async (_, _) =>
        {
            if (!initialized || changingHost || hostChoice.SelectedItem is not string host) return;
            changingHost = true; hostChoice.IsEnabled = login.IsEnabled = false;
            try { await service.SelectHostAsync(host); if (!closing) NavigateLogin(); }
            catch { if (!closing) status.Text = "RAIN002：切换官方站点失败，请重新连接。"; }
            finally { changingHost = false; if (!closing) hostChoice.IsEnabled = login.IsEnabled = true; }
        };
        service.StatusChanged += UpdateStatus;
        Loaded += Initialize;
        Closed += (_, _) => { closing = true; service.StatusChanged -= UpdateStatus; service.ConnectionClosed(); browser.Dispose(); };
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (initialized || closing) return;
        try
        {
            var environment = await service.EnsureSessionAsync();
            if (closing) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (closing) return;
            var core = browser.CoreWebView2; RainSyncService.ConfigureCore(core);
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.Document,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) => request.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(), 403, "HTTPS Required", "Cache-Control: no-store\r\n");
            core.NavigationStarting += (_, navigation) =>
            {
                if (!RainAddressPolicy.IsNavigation(service.Platform, navigation.Uri))
                {
                    navigation.Cancel = true;
                    status.Text = "RAIN003：官方页面尝试打开其他站点，当前连接暂未适配该跳转。";
                }
            };
            core.FrameNavigationStarting += (_, navigation) =>
            {
                if (!RainLoginFrameAllowed(navigation.Uri)) navigation.Cancel = true;
            };
            core.NewWindowRequested += (_, request) =>
            {
                request.Handled = true;
                if (RainAddressPolicy.IsNavigation(service.Platform, request.Uri)) core.Navigate(request.Uri);
                else status.Text = "RAIN003：此登录跳转暂未适配，请使用当前官方页面的扫码或账号登录。";
            };
            core.NavigationCompleted += (_, navigation) =>
            {
                if (closing) return;
                if (navigation.IsSuccess)
                {
                    service.LoginNavigationCompleted(core.Source);
                    status.Text = "官方页面已加载。登录完成后自动采集，请回到任务清单查看同步结果。";
                }
                else if (navigation.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    status.Text = "RAIN002：官方页面加载失败（" + navigation.WebErrorStatus + "），请检查网络后重试。";
            };
            initialized = true; login.IsEnabled = true;
            NavigateLogin(); service.ConnectionOpened();
        }
        catch { if (!closing) status.Text = "RAIN002：登录环境初始化失败，请检查 WebView2 和项目目录权限。"; }
    }

    private static bool RainLoginFrameAllowed(string address) => address == "about:blank"
        || TaskRules.IsSafeLink(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && (uri.IdnHost.Equals("open.weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.Equals("yuketang.cn", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith(".yuketang.cn", StringComparison.OrdinalIgnoreCase));

    private void NavigateLogin()
    {
        if (!initialized || closing) return;
        browser.CoreWebView2.Navigate(service.Platform.LoginUri(service.Host).AbsoluteUri);
        status.Text = "请在官方页面完成登录，程序会自动检查登录状态。";
    }

    private void UpdateStatus(string message, bool running, bool warning)
    {
        if (closing) return;
        status.Text = message; status.Foreground = warning ? Brushes.Firebrick : Brushes.DimGray;
    }
}
