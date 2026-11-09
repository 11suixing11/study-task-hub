using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class XuetangConnectionWindow : Window
{
    private readonly XuetangSyncService service;
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly Button login = new Wpf.Ui.Controls.Button { Content = "打开官方登录", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Padding = new Thickness(14, 7, 14, 7), IsEnabled = false };
    private bool initialized, initializing, closing, clearing, refreshingLogin, syncMessageVisible, documentLoaded, pageReady, checkingPage;
    private string? pageFailureMessage;
    private ulong latestNavigation;
    private readonly DispatcherTimer loadTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer pageTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CancellationTokenSource windowLifetime = new();

    public XuetangConnectionWindow(XuetangSyncService service, Action showDashboard)
    {
        this.service = service;
        Title = "连接学堂在线 · 官方登录"; Width = 1080; Height = 820; MinWidth = 780; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AccessProbe;component/FluentTheme.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        var root = new DockPanel { Margin = new Thickness(18) }; var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "连接学堂在线", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "请点击下方官网右上角的“登录”，在官方弹窗完成登录。已有有效 Cookie 会自动复用；登录后返回课程列表，保存课程选择后采集任务。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 12)
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; buttons.Children.Add(login);
        login.Click += async (_, _) => await NavigateLoginAsync();
        var dashboard = new Wpf.Ui.Controls.Button { Content = "回到任务清单", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        dashboard.Click += (_, _) => showDashboard(); buttons.Children.Add(dashboard);
        var clear = new Wpf.Ui.Controls.Button { Content = "重新连接", Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        clear.Click += async (_, _) =>
        {
            if (!initialized || clearing || refreshingLogin) return;
            clearing = true; clear.IsEnabled = login.IsEnabled = false;
            try
            {
                browser.CoreWebView2.Stop(); browser.CoreWebView2.Navigate("about:blank");
                await service.ClearSessionAsync(); if (!closing) { await NavigateLoginAsync(); service.ConnectionOpened(); }
            }
            catch { if (!closing) status.Text = "XT004：重新连接未完成，请稍后重试；已有任务保留。"; }
            finally { clearing = false; if (!closing) clear.IsEnabled = login.IsEnabled = true; }
        };
        buttons.Children.Add(clear); header.Children.Add(buttons);
        status.Text = "正在准备官方登录…"; status.Margin = new Thickness(0, 0, 0, 12); header.Children.Add(status);
        header.Children.Add(new TextBlock { Text = XuetangPlatform.CoverageText, TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(header); root.Children.Add(browser); Content = root;
        service.StatusChanged += UpdateStatus; Loaded += Initialize;
        loadTimer.Tick += (_, _) =>
        {
            loadTimer.Stop(); pageTimer.Stop();
            if (closing || pageReady || pageFailureMessage is not null) return;
            service.LoginDiagnostics.Record(XuetangLoginStage.MainNavigation,
                documentLoaded ? XuetangLoginResult.BlankPage : XuetangLoginResult.LoadingTimeout);
            ShowPageFailure(documentLoaded ? "XT002：官方页面没有显示内容，请点击“打开官方登录”刷新页面后重试。"
                : "XT002：官方登录页面加载超时，请点击“打开官方登录”刷新页面后重试。");
        };
        pageTimer.Tick += async (_, _) => await InspectPageAsync(latestNavigation);
        Closed += (_, _) =>
        {
            closing = true; loadTimer.Stop(); pageTimer.Stop(); windowLifetime.Cancel();
            service.StatusChanged -= UpdateStatus; service.ConnectionClosed(); browser.Dispose(); windowLifetime.Dispose();
        };
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
            core.WebResourceResponseReceived += (_, response) =>
            {
                if (closing || refreshingLogin) return;
                var kind = XuetangPlatform.LoginResourceKind(response.Request.Uri);
                if (kind == "none") return;
                var code = response.Response.StatusCode;
                var contentType = response.Response.Headers.Contains("Content-Type") ? response.Response.Headers.GetHeader("Content-Type") : "";
                var validType = kind == "script" ? contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase)
                    : contentType.Contains("text/css", StringComparison.OrdinalIgnoreCase);
                if (code >= 400 || code is >= 200 and < 300 && !validType)
                {
                    service.LoginDiagnostics.Record(XuetangLoginStage.Resource,
                        kind == "script" ? XuetangLoginResult.ScriptUnavailable : XuetangLoginResult.StyleUnavailable, response.Request.Uri, code);
                    ShowPageFailure(kind == "script" ? "XT002：官方登录脚本未能加载，请点击“打开官方登录”刷新页面后重试。"
                        : "XT002：官方登录样式未能加载，请点击“打开官方登录”刷新页面后重试。");
                }
            };
            core.ServerCertificateErrorDetected += (_, certificate) =>
            {
                service.LoginDiagnostics.Record(XuetangLoginStage.Certificate, XuetangLoginResult.NetworkFailed, certificate.RequestUri);
                ShowPageFailure("XT002：官方页面证书校验失败，请检查网络后重试。");
            };
            core.ProcessFailed += (_, _) =>
            {
                service.LoginDiagnostics.Record(XuetangLoginStage.Renderer, XuetangLoginResult.Failed);
                ShowPageFailure("XT002：登录窗口运行异常，请关闭连接窗口后重新打开。");
            };
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                if (Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
                    request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "HTTPS Required", "Cache-Control: no-store\r\n");
            };
            core.NavigationStarting += (_, navigation) =>
            {
                if (!XuetangPlatform.IsLoginNavigation(navigation.Uri))
                {
                    navigation.Cancel = true; service.LoginDiagnostics.Record(XuetangLoginStage.MainNavigation, XuetangLoginResult.Blocked, navigation.Uri);
                    ShowPageFailure("XT003：此登录跳转尚未适配，请使用官网其他登录方式。");
                }
                else
                {
                    latestNavigation = navigation.NavigationId; loadTimer.Stop(); pageTimer.Stop(); documentLoaded = pageReady = false;
                    if (navigation.Uri == "about:blank") return;
                    loadTimer.Start(); service.LoginDiagnostics.Record(XuetangLoginStage.MainNavigation, XuetangLoginResult.Started, navigation.Uri);
                    if (!service.IsRunning && !syncMessageVisible && pageFailureMessage is null)
                    { status.Text = "正在加载官方登录页面…"; status.Foreground = Brushes.DimGray; }
                }
            };
            core.FrameNavigationStarting += (_, navigation) =>
            {
                var allowed = XuetangPlatform.IsLoginNavigation(navigation.Uri)
                    || XuetangPlatform.IsOfficialNavigation(core.Source) && core.Source != "about:blank"
                        && XuetangPlatform.IsLoginFrameNavigation(navigation.Uri);
                if (!allowed)
                {
                    navigation.Cancel = true; service.LoginDiagnostics.Record(XuetangLoginStage.FrameNavigation, XuetangLoginResult.Blocked, navigation.Uri);
                    ShowPageFailure("XT003：登录页面的验证窗口跳转尚未适配，请使用官网其他登录方式。");
                }
            };
            core.NewWindowRequested += (_, request) =>
            {
                request.Handled = true;
                var address = request.Uri;
                if (XuetangPlatform.IsLoginNavigation(address))
                    Dispatcher.BeginInvoke(new Action(() => { if (!closing && !clearing) core.Navigate(address); }));
                else
                {
                    service.LoginDiagnostics.Record(XuetangLoginStage.Popup, XuetangLoginResult.Blocked, address);
                    ShowPageFailure("XT003：当前登录跳转尚未适配，请使用官网其他登录方式。");
                }
            };
            core.DOMContentLoaded += async (_, navigation) =>
            {
                if (closing || navigation.NavigationId != latestNavigation || core.Source == "about:blank") return;
                service.LoginNavigationCompleted(core.Source); pageTimer.Start(); await InspectPageAsync(navigation.NavigationId);
            };
            core.NavigationCompleted += async (_, navigation) =>
            {
                if (closing || navigation.NavigationId != latestNavigation || core.Source == "about:blank") return;
                if (navigation.IsSuccess && navigation.HttpStatusCode < 400)
                { documentLoaded = true; service.LoginNavigationCompleted(core.Source); pageTimer.Start(); await InspectPageAsync(navigation.NavigationId); }
                else if (navigation.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted))
                {
                    loadTimer.Stop(); pageTimer.Stop(); service.LoginDiagnostics.Record(XuetangLoginStage.MainNavigation, XuetangLoginResult.NetworkFailed, core.Source, navigation.HttpStatusCode);
                    ShowPageFailure("XT002：官网加载失败（" + (navigation.HttpStatusCode >= 400 ? "HTTP " + navigation.HttpStatusCode : navigation.WebErrorStatus.ToString()) + "），请检查网络后重试。");
                }
            };
            initialized = true; await NavigateLoginAsync(); if (!closing) service.ConnectionOpened();
        }
        catch
        {
            service.LoginDiagnostics.Record(XuetangLoginStage.Preparation, XuetangLoginResult.Failed);
            ShowPageFailure("XT002：登录环境初始化失败，请检查 WebView2 和项目目录权限。");
        }
        finally { initializing = false; }
    }
    private async Task NavigateLoginAsync()
    {
        if (!initialized || closing || refreshingLogin) return;
        refreshingLogin = true; login.IsEnabled = false; syncMessageVisible = false; pageFailureMessage = null;
        loadTimer.Stop(); pageTimer.Stop(); documentLoaded = pageReady = false;
        service.LoginDiagnostics.Record(XuetangLoginStage.Preparation, XuetangLoginResult.Started);
        try
        {
            status.Text = "正在刷新官方页面缓存…"; status.Foreground = Brushes.DimGray;
            var core = browser.CoreWebView2; core.Stop();
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            if (closing) return;
            service.LoginDiagnostics.Record(XuetangLoginStage.CacheRefresh, XuetangLoginResult.Ready);
            core.Navigate(XuetangPlatform.Login.AbsoluteUri);
            status.Text = "正在打开学堂在线官网。请点击官网右上角的“登录”完成登录。";
        }
        catch
        {
            service.LoginDiagnostics.Record(XuetangLoginStage.CacheRefresh, XuetangLoginResult.Failed);
            ShowPageFailure("XT002：官方页面刷新未完成，请关闭连接窗口后重新打开。");
        }
        finally { refreshingLogin = false; if (!closing) login.IsEnabled = true; }
    }

    // Read only whether visible content exists. Never return form values, identity, text or URLs.
    private const string PageContentScript = "(() => [...document.querySelectorAll('input,button,a,h1,h2,p,[role=button],.loginWrap,.login_con')].some(el => { const r = el.getBoundingClientRect(), s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && (el.matches('input,button') || (el.textContent || '').trim().length > 0); }))()";
    private async Task InspectPageAsync(ulong navigationId)
    {
        if (closing || refreshingLogin || checkingPage || pageReady || navigationId != latestNavigation || browser.CoreWebView2.Source == "about:blank") return;
        checkingPage = true;
        try
        {
            var visible = await browser.CoreWebView2.ExecuteScriptAsync(PageContentScript).WaitAsync(TimeSpan.FromSeconds(3), windowLifetime.Token);
            if (closing || navigationId != latestNavigation || visible != "true") return;
            pageReady = true; loadTimer.Stop(); pageTimer.Stop();
            service.LoginDiagnostics.Record(XuetangLoginStage.MainNavigation, XuetangLoginResult.Ready, browser.CoreWebView2.Source);
            if (!service.IsRunning && !syncMessageVisible && pageFailureMessage is null)
            { status.Text = "官网页面已加载，请点击官网右上角的“登录”；登录后回到平台管理保存课程选择。"; status.Foreground = Brushes.DimGray; }
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        finally { checkingPage = false; }
    }

    private void ShowPageFailure(string message)
    { if (!closing) { pageFailureMessage = message; status.Text = message; status.Foreground = Brushes.Firebrick; } }

    private void UpdateStatus(string message, bool running, bool warning)
    {
        if (closing) return;
        syncMessageVisible = true; status.Text = pageFailureMessage ?? message;
        status.Foreground = pageFailureMessage is not null || warning ? Brushes.Firebrick : Brushes.DimGray;
    }
}
