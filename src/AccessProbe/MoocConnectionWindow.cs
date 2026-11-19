using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class MoocConnectionPage : WorkspacePage
{
    private readonly MoocSyncService service;
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly Button login = new Wpf.Ui.Controls.Button { Content = "打开官方登录", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Padding = new Thickness(14, 7, 14, 7), IsEnabled = false };
    private bool initialized, initializing, closing, clearing, syncMessageVisible;
    private ulong latestNavigation;
    private string? pageFailureMessage;
    private readonly DispatcherTimer loadTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly ConnectionStatusPanel connection;

    public MoocConnectionPage(MoocSyncService service, Action showDashboard)
    {
        this.service = service;
        Title = "连接中国大学 MOOC · 官方登录";
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AccessProbe;component/FluentTheme.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "连接中国大学 MOOC", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "请在右侧官网使用手机号、邮箱或微信登录。微信扫码后确认授权，等待左侧接入结果，再选择课程同步到首页。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 12)
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; buttons.Children.Add(login);
        login.Click += (_, _) => NavigateLogin();
        var dashboard = new Wpf.Ui.Controls.Button { Content = "回到任务清单", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        dashboard.Click += (_, _) => showDashboard(); buttons.Children.Add(dashboard);
        var clear = new Wpf.Ui.Controls.Button { Content = "清理登录后重试", Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        clear.Click += async (_, _) =>
        {
            if (!initialized || clearing) return;
            clearing = true; clear.IsEnabled = login.IsEnabled = false;
            try
            {
                browser.CoreWebView2.Stop(); browser.CoreWebView2.Navigate("about:blank");
                await service.ClearSessionAsync(); if (!closing) { connection!.Reset(); NavigateLogin(); if (IsPageActive) service.ConnectionOpened(); }
            }
            catch { if (!closing) status.Text = "MOOC004：重新连接未完成，请稍后重试；已有任务保留。"; }
            finally { clearing = false; if (!closing) clear.IsEnabled = login.IsEnabled = true; }
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
        service.StatusChanged += UpdateStatus; Loaded += Initialize;
        Activated += (_, _) => service.ConnectionOpened();
        Deactivated += (_, _) => { service.ConnectionClosed(); loadTimer.Stop(); };
        loadTimer.Tick += (_, _) =>
        {
            loadTimer.Stop();
            if (!closing && !service.IsRunning && !syncMessageVisible)
                ShowPageFailure("官方登录页面仍在加载，可点击“打开官方登录”重新加载；已有任务保留。", "timeout");
        };
        Closed += (_, _) => { closing = true; loadTimer.Stop(); service.StatusChanged -= UpdateStatus; service.ConnectionChanged -= ShowConnection; service.ConnectionClosed(); browser.Dispose(); };
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (initialized || initializing || closing) return;
        initializing = true;
        try
        {
            var environment = await service.EnsureSessionAsync(); if (closing) return;
            await browser.EnsureCoreWebView2Async(environment); if (closing) return;
            var core = browser.CoreWebView2; RainSyncService.ConfigureCore(core);
            core.ServerCertificateErrorDetected += (_, certificate) =>
            {
                certificate.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                ShowPageFailure("MOOC002：官方页面证书校验失败，请检查设备时间和网络后重试。", "certificate_failed");
            };
            core.ProcessFailed += (_, _) => ShowPageFailure("MOOC002：登录页面运行异常，请返回后重新打开连接页。", "renderer_failed");
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                if (Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttp)
                    request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "HTTPS Required", "Cache-Control: no-store\r\n");
            };
            core.NavigationStarting += (_, navigation) =>
            {
                if (!MoocPlatform.IsLoginNavigation(navigation.Uri))
                { navigation.Cancel = true; ShowPageFailure("MOOC003：此官方登录跳转尚未适配，请使用当前官网的手机号、邮箱或微信登录。", "unsupported_redirect"); }
                else
                {
                    latestNavigation = navigation.NavigationId; loadTimer.Stop(); if (IsPageActive) loadTimer.Start();
                    if (!service.IsRunning && !syncMessageVisible && pageFailureMessage is null) { status.Text = "正在加载官方登录页面…"; status.Foreground = Brushes.DimGray; }
                }
            };
            core.FrameNavigationStarting += (_, navigation) =>
            {
                if (!MoocPlatform.IsLoginNavigation(navigation.Uri))
                { navigation.Cancel = true; ShowPageFailure("MOOC003：登录页面的验证窗口跳转尚未适配，请使用官网其他登录方式。", "unsupported_redirect"); }
            };
            core.NewWindowRequested += (_, request) =>
            {
                request.Handled = true;
                var address = request.Uri;
                if (MoocPlatform.IsLoginNavigation(address))
                    Dispatcher.BeginInvoke(new Action(() => { if (!closing && !clearing) core.Navigate(address); }));
                else ShowPageFailure("MOOC003：当前登录方式的跳转尚未适配，请使用官网手机号、邮箱或微信登录。", "unsupported_redirect");
            };
            void PageReady(ulong navigationId)
            {
                if (closing || navigationId != latestNavigation) return;
                loadTimer.Stop(); service.LoginNavigationCompleted(core.Source);
                if (!service.IsRunning && !syncMessageVisible && pageFailureMessage is null)
                { status.Text = "官方登录页面已就绪，请完成登录；随后返回课程列表，保存选择后采集任务。"; status.Foreground = Brushes.DimGray; }
            }
            core.DOMContentLoaded += (_, navigation) => PageReady(navigation.NavigationId);
            core.NavigationCompleted += (_, navigation) =>
            {
                if (closing || navigation.NavigationId != latestNavigation) return;
                if (navigation.IsSuccess && navigation.HttpStatusCode < 400)
                    PageReady(navigation.NavigationId);
                else if (navigation.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted))
                {
                    loadTimer.Stop(); ShowPageFailure("MOOC002：官网加载失败（" + navigation.WebErrorStatus + "），请检查网络后重试。",
                        ConnectionPageFailures.Outcome(navigation.WebErrorStatus, navigation.HttpStatusCode));
                }
            };
            initialized = true; login.IsEnabled = true; NavigateLogin();
        }
        catch { ShowPageFailure("MOOC002：登录环境初始化失败，请检查 WebView2 和项目目录权限。", "environment_failed"); }
        finally { initializing = false; }
    }
    private void NavigateLogin()
    {
        if (!initialized || closing) return;
        syncMessageVisible = false; pageFailureMessage = null; connection.ClearPageFailure(); status.Visibility = Visibility.Visible;
        browser.CoreWebView2.Navigate(MoocPlatform.Login.AbsoluteUri); status.Text = "正在打开官方登录页，请使用手机号、邮箱或微信完成登录。";
    }
    private void UpdateStatus(string message, bool running, bool warning)
    {
        if (closing) return;
        connection.SetRunning(running); syncMessageVisible = true; status.Text = pageFailureMessage ?? message;
        status.Foreground = pageFailureMessage is not null || warning ? Brushes.Firebrick : Brushes.DimGray;
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
