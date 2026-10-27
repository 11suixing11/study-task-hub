using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class MoocConnectionWindow : Window
{
    private readonly MoocSyncService service;
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly Button login = new() { Content = "打开官方登录", Padding = new Thickness(14, 7, 14, 7), IsEnabled = false };
    private bool initialized, closing, clearing, syncMessageVisible;
    private ulong latestNavigation;
    private readonly DispatcherTimer loadTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    public MoocConnectionWindow(MoocSyncService service, Action showDashboard)
    {
        this.service = service;
        Title = "连接中国大学 MOOC · 官方登录"; Width = 1080; Height = 820; MinWidth = 780; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        var root = new DockPanel { Margin = new Thickness(18) }; var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "连接中国大学 MOOC", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "请在下方官网使用手机号、邮箱或微信完成登录。登录后自动采集课程与任务到统一清单；点击任务可在系统浏览器进入相应做题或提交页面。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 12)
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; buttons.Children.Add(login);
        login.Click += (_, _) => NavigateLogin();
        var dashboard = new Button { Content = "回到任务清单", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        dashboard.Click += (_, _) => showDashboard(); buttons.Children.Add(dashboard);
        var clear = new Button { Content = "重新连接", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        clear.Click += async (_, _) =>
        {
            if (!initialized || clearing) return;
            clearing = true; clear.IsEnabled = login.IsEnabled = false;
            try
            {
                browser.CoreWebView2.Stop(); browser.CoreWebView2.Navigate("about:blank");
                await service.ClearSessionAsync(); if (!closing) { NavigateLogin(); service.ConnectionOpened(); }
            }
            catch { if (!closing) status.Text = "MOOC004：重新连接未完成，请稍后重试；已有任务保留。"; }
            finally { clearing = false; if (!closing) clear.IsEnabled = login.IsEnabled = true; }
        };
        buttons.Children.Add(clear); header.Children.Add(buttons);
        status.Text = "正在准备官方登录…"; status.Margin = new Thickness(0, 0, 0, 12); header.Children.Add(status);
        root.Children.Add(header); root.Children.Add(browser); Content = root;
        service.StatusChanged += UpdateStatus; Loaded += Initialize;
        loadTimer.Tick += (_, _) =>
        {
            loadTimer.Stop();
            if (!closing && !service.IsRunning && !syncMessageVisible)
            { status.Text = "官方登录页面仍在加载，可点击“打开官方登录”重新加载；已有任务保留。"; status.Foreground = Brushes.Firebrick; }
        };
        Closed += (_, _) => { closing = true; loadTimer.Stop(); service.StatusChanged -= UpdateStatus; service.ConnectionClosed(); browser.Dispose(); };
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (initialized || closing) return;
        try
        {
            var environment = await service.EnsureSessionAsync(); if (closing) return;
            await browser.EnsureCoreWebView2Async(environment); if (closing) return;
            var core = browser.CoreWebView2; RainSyncService.ConfigureCore(core);
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) => request.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(), 403, "HTTPS Required", "Cache-Control: no-store\r\n");
            core.NavigationStarting += (_, navigation) =>
            {
                if (!MoocPlatform.IsLoginNavigation(navigation.Uri))
                { navigation.Cancel = true; status.Text = "MOOC003：此官方登录跳转尚未适配，请使用当前官网的手机号、邮箱或微信登录。"; }
                else
                {
                    latestNavigation = navigation.NavigationId; loadTimer.Stop(); loadTimer.Start();
                    if (!service.IsRunning && !syncMessageVisible) { status.Text = "正在加载官方登录页面…"; status.Foreground = Brushes.DimGray; }
                }
            };
            core.FrameNavigationStarting += (_, navigation) =>
            {
                if (!MoocPlatform.IsLoginNavigation(navigation.Uri))
                { navigation.Cancel = true; status.Text = "MOOC003：登录页面的验证窗口跳转尚未适配，请使用官网其他登录方式。"; status.Foreground = Brushes.Firebrick; }
            };
            core.NewWindowRequested += (_, request) =>
            {
                request.Handled = true;
                if (MoocPlatform.IsLoginNavigation(request.Uri)) core.Navigate(request.Uri);
                else status.Text = "MOOC003：当前登录方式的跳转尚未适配，请使用官网手机号、邮箱或微信登录。";
            };
            void PageReady(ulong navigationId)
            {
                if (closing || navigationId != latestNavigation) return;
                loadTimer.Stop(); service.LoginNavigationCompleted(core.Source);
                if (!service.IsRunning && !syncMessageVisible)
                { status.Text = "官方登录页面已就绪，请完成登录；任务会自动采集到统一清单。"; status.Foreground = Brushes.DimGray; }
            }
            core.DOMContentLoaded += (_, navigation) => PageReady(navigation.NavigationId);
            core.NavigationCompleted += (_, navigation) =>
            {
                if (closing || navigation.NavigationId != latestNavigation) return;
                if (navigation.IsSuccess)
                    PageReady(navigation.NavigationId);
                else if (navigation.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted))
                { loadTimer.Stop(); status.Text = "MOOC002：官网加载失败（" + navigation.WebErrorStatus + "），请检查网络后重试。"; status.Foreground = Brushes.Firebrick; }
            };
            initialized = true; login.IsEnabled = true; NavigateLogin(); service.ConnectionOpened();
        }
        catch { if (!closing) status.Text = "MOOC002：登录环境初始化失败，请检查 WebView2 和项目目录权限。"; }
    }
    private void NavigateLogin()
    {
        if (!initialized || closing) return;
        syncMessageVisible = false;
        browser.CoreWebView2.Navigate(MoocPlatform.Login.AbsoluteUri); status.Text = "正在打开官方登录页，请使用手机号、邮箱或微信完成登录。";
    }
    private void UpdateStatus(string message, bool running, bool warning)
    { if (!closing) { syncMessageVisible = true; status.Text = message; status.Foreground = warning ? Brushes.Firebrick : Brushes.DimGray; } }
}
