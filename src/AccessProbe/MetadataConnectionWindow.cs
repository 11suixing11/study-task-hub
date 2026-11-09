using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AccessProbe;

public sealed class MetadataConnectionWindow : Window
{
    private readonly MetadataSyncService service;
    private MetadataPlatform Platform => service.Platform;
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly Button login = new Wpf.Ui.Controls.Button { Content = "打开官方登录", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Padding = new Thickness(14, 7, 14, 7), IsEnabled = false };
    private bool initialized, closing, clearing, syncMessageVisible, refreshingLogin, pageFailure;
    private string? pageFailureMessage;
    private ulong latestNavigation;
    private readonly DispatcherTimer loadTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer tokenTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource windowLifetime = new();
    private readonly HashSet<CoreWebView2Frame> tokenFrames = [];
    private bool checkingTokens;

    public MetadataConnectionWindow(MetadataSyncService service, Action showDashboard)
    {
        this.service = service;
        Title = $"连接{Platform.Name} · 官方登录"; Width = 1080; Height = 820; MinWidth = 780; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AccessProbe;component/FluentTheme.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        var root = new DockPanel { Margin = new Thickness(18) }; var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = $"连接{Platform.Name}", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = Platform.Id switch
            {
                "zhihuishu" => "请在下方官网完成登录。Cookie 自动保存，随后列出翻转课、共享课、智慧课程及 AI 智课；选择并保存课程后采集已支持的作业、考试到统一清单。",
                "pta" => "请在下方 PTA 官网完成登录。Cookie 自动保存，下次优先复用；登录失效时请重新登录。随后列出教学课程，选择并保存后采集题目集到统一清单。",
                _ => "请在下方旧版 U 校园官网完成登录。登录会自动保存，随后返回班课列表；选择并保存课程后采集单元截止日期、必学进度及作业、测试到统一清单。"
            },
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 12)
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; buttons.Children.Add(login);
        login.Click += async (_, _) => await NavigateLoginAsync();
        var dashboard = new Wpf.Ui.Controls.Button { Content = "回到任务清单", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        dashboard.Click += (_, _) => showDashboard(); buttons.Children.Add(dashboard);
        var clear = new Wpf.Ui.Controls.Button { Content = "重新连接", Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        clear.Click += async (_, _) =>
        {
            if (!initialized || clearing) return;
            clearing = true; clear.IsEnabled = login.IsEnabled = false;
            try
            {
                browser.CoreWebView2.Stop(); browser.CoreWebView2.Navigate("about:blank");
                await service.ClearSessionAsync(); if (!closing) { await NavigateLoginAsync(); service.ConnectionOpened(); }
            }
            catch { if (!closing) status.Text = $"{Platform.CodePrefix}004：重新连接未完成，请稍后重试；已有任务保留。"; }
            finally { clearing = false; if (!closing) clear.IsEnabled = login.IsEnabled = true; }
        };
        buttons.Children.Add(clear); header.Children.Add(buttons);
        status.Text = "正在准备官方登录…"; status.Margin = new Thickness(0, 0, 0, 12); header.Children.Add(status);
        header.Children.Add(new TextBlock { Text = Platform.CoverageText, TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(header); root.Children.Add(browser); Content = root;
        service.StatusChanged += UpdateStatus; Loaded += Initialize;
        loadTimer.Tick += (_, _) =>
        {
            loadTimer.Stop();
            service.LoginDiagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.LoadingTimeout,
                navigationId: latestNavigation);
            if (!closing && !service.IsRunning && !syncMessageVisible && !pageFailure)
                ShowPageFailure("官方登录页面仍在加载，可点击“打开官方登录”重新加载；已有任务保留。");
        };
        tokenTimer.Tick += async (_, _) => await CheckTokensAsync();
        Closed += (_, _) =>
        {
            closing = true; loadTimer.Stop(); tokenTimer.Stop(); windowLifetime.Cancel(); tokenFrames.Clear();
            service.StatusChanged -= UpdateStatus; service.ConnectionClosed(); browser.Dispose(); windowLifetime.Dispose();
        };
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (initialized || closing) return;
        try
        {
            var environment = await service.EnsureSessionAsync(); if (closing) return;
            await browser.EnsureCoreWebView2Async(environment); if (closing) return;
            var core = browser.CoreWebView2; RainSyncService.ConfigureCore(core);
            core.WebResourceResponseReceived += (_, response) =>
            {
                if (closing || refreshingLogin || Platform.Id != "zhihuishu" || !Uri.TryCreate(response.Request.Uri, UriKind.Absolute, out var uri)
                    || uri.Scheme != "https" || uri.IdnHost != "login.zhihuishu.com") return;
                var code = response.Response.StatusCode;
                var script = uri.AbsolutePath.StartsWith("/assets/", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith(".js", StringComparison.Ordinal);
                var contentType = response.Response.Headers.Contains("Content-Type") ? response.Response.Headers.GetHeader("Content-Type") : "";
                if (script && (code >= 400 || code == 200 && !contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                    && !contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase)))
                {
                    service.LoginDiagnostics.Record(MetadataLoginStage.Resource, MetadataLoginResult.ScriptUnavailable, uri.AbsoluteUri, code);
                    ShowPageFailure("ZHS002：官方登录脚本未能加载，请点击“打开官方登录”刷新页面缓存后重试。");
                }
                else if (code >= 400)
                    service.LoginDiagnostics.Record(MetadataLoginStage.Resource, MetadataLoginResult.NetworkFailed, uri.AbsoluteUri, code);
            };
            core.ServerCertificateErrorDetected += (_, certificate) => service.LoginDiagnostics.Record(MetadataLoginStage.Certificate,
                MetadataLoginResult.NetworkFailed, certificate.RequestUri);
            core.ProcessFailed += (_, _) =>
            {
                service.LoginDiagnostics.Record(MetadataLoginStage.Renderer, MetadataLoginResult.Failed);
                ShowPageFailure(Platform.CodePrefix + "002：登录窗口运行异常，请关闭连接窗口后重新打开。");
            };
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                if (Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttp)
                    request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "HTTPS Required", "Cache-Control: no-store\r\n");
            };
            core.NavigationStarting += (_, navigation) =>
            {
                if (!Platform.IsLoginNavigation(navigation.Uri))
                { navigation.Cancel = true; service.LoginDiagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Blocked, navigation.Uri);
                    ShowPageFailure($"{Platform.CodePrefix}003：此登录跳转尚未适配，请使用官网其他登录方式。"); }
                else
                {
                    latestNavigation = navigation.NavigationId; loadTimer.Stop(); loadTimer.Start();
                    service.LoginDiagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Started, navigation.Uri, navigationId: navigation.NavigationId);
                    if (!service.IsRunning && !syncMessageVisible && !pageFailure) { status.Text = "正在加载官方登录页面…"; status.Foreground = Brushes.DimGray; }
                }
            };
            core.FrameNavigationStarting += (_, navigation) =>
            {
                if (!Platform.IsLoginFrameNavigation(navigation.Uri))
                { navigation.Cancel = true; service.LoginDiagnostics.Record(MetadataLoginStage.FrameNavigation, MetadataLoginResult.Blocked, navigation.Uri);
                    ShowPageFailure($"{Platform.CodePrefix}003：登录页面的验证窗口跳转尚未适配，请使用官网其他登录方式。"); }
            };
            core.NewWindowRequested += (_, request) =>
            {
                request.Handled = true;
                var address = request.Uri;
                // Navigating inside the popup event can leave a blank intermediate document.
                if (Platform.IsLoginNavigation(address))
                    Dispatcher.BeginInvoke(new Action(() => { if (!closing && !clearing) core.Navigate(address); }));
                else if (Platform.Id == "ucampus" && Platform.IsMetadataDocument(core.Source) && service.LoginDiagnostics.SessionConfirmed)
                    service.LoginDiagnostics.Record(MetadataLoginStage.Popup, MetadataLoginResult.PopupSuppressed, address);
                else { service.LoginDiagnostics.Record(MetadataLoginStage.Popup, MetadataLoginResult.Blocked, address);
                    ShowPageFailure($"{Platform.CodePrefix}003：当前登录跳转尚未适配，请使用官网其他登录方式。"); }
            };
            void PageReady(ulong navigationId)
            {
                if (closing || navigationId != latestNavigation) return;
                loadTimer.Stop(); service.LoginNavigationCompleted(core.Source);
                service.LoginDiagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, core.Source, navigationId: navigationId);
                if (pageFailure && service.LoginDiagnostics.Snapshot().PageFailure is null)
                { pageFailure = false; pageFailureMessage = null; }
                if (!service.IsRunning && !syncMessageVisible && !pageFailure)
                { status.Text = "官方登录页面已就绪，请完成登录；随后选择需要记录的课程。"; status.Foreground = Brushes.DimGray; }
            }
            core.DOMContentLoaded += async (_, navigation) => { PageReady(navigation.NavigationId); await ObserveToken(core.Source, () => core.ExecuteScriptAsync(TokenScript)); };
            if (Platform.Id == "zhihuishu") core.FrameCreated += (_, created) => TrackTokenFrame(created.Frame);
            core.NavigationCompleted += (_, navigation) =>
            {
                if (closing || navigation.NavigationId != latestNavigation) return;
                if (navigation.IsSuccess)
                    PageReady(navigation.NavigationId);
                else if (navigation.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted))
                { loadTimer.Stop(); service.LoginDiagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.NetworkFailed, core.Source);
                    ShowPageFailure($"{Platform.CodePrefix}002：官网加载失败（" + navigation.WebErrorStatus + "），请检查网络后重试。"); }
            };
            initialized = true; await NavigateLoginAsync(); if (closing) return; service.ConnectionOpened();
            if (Platform.Id == "zhihuishu") tokenTimer.Start();
        }
        catch { service.LoginDiagnostics.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Failed);
            if (!closing) status.Text = $"{Platform.CodePrefix}002：登录环境初始化失败，请检查 WebView2 和项目目录权限。"; }
    }
    private const string TokenScript = "(() => { const eligible = location.origin === 'https://onlineweb.zhihuishu.com' && location.pathname === '/onlinestuh5' || location.origin === 'https://onlinestuh5.zhihuishu.com' && location.pathname === '/'; if (!eligible) return {eligible:false}; return {eligible:true,source:location.origin,token:localStorage.getItem('token')}; })()";
    private void TrackTokenFrame(CoreWebView2Frame frame)
    {
        if (closing || tokenFrames.Count >= 16 || !tokenFrames.Add(frame)) return;
        frame.DOMContentLoaded += async (_, _) => await ObserveToken("https://onlinestuh5.zhihuishu.com/", () => frame.ExecuteScriptAsync(TokenScript));
        frame.FrameCreated += (_, created) => TrackTokenFrame(created.Frame);
        frame.Destroyed += (_, _) => tokenFrames.Remove(frame);
    }
    private async Task CheckTokensAsync()
    {
        if (!initialized || closing || clearing || checkingTokens || Platform.Id != "zhihuishu") return;
        checkingTokens = true;
        try
        {
            var core = browser.CoreWebView2;
            if (Platform.IsMetadataDocument(core.Source)) await ObserveToken(core.Source, () => core.ExecuteScriptAsync(TokenScript));
            foreach (var frame in tokenFrames.ToArray())
            {
                if (closing || clearing) break;
                await ObserveToken("https://onlinestuh5.zhihuishu.com/", () => frame.ExecuteScriptAsync(TokenScript));
            }
        }
        catch { }
        finally { checkingTokens = false; }
    }
    private async Task ObserveToken(string address, Func<Task<string>> read)
    {
        if (closing || clearing || Platform.Id != "zhihuishu") return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { await service.ObserveMetadataTokenAsync(address, read, timeout.Token); } catch { }
    }
    private async Task NavigateLoginAsync()
    {
        if (!initialized || closing || refreshingLogin) return;
        refreshingLogin = true; login.IsEnabled = false; syncMessageVisible = pageFailure = false; pageFailureMessage = null;
        service.LoginDiagnostics.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
        try
        {
            var core = browser.CoreWebView2;
            if (Platform.Id == "zhihuishu")
            {
                status.Text = "正在刷新官方登录页面缓存…"; status.Foreground = Brushes.DimGray;
                core.Stop();
                // Published chunks are replaced during official login deployments. Retain cookies and DOM storage.
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
                if (closing) return;
                service.LoginDiagnostics.Record(MetadataLoginStage.CacheRefresh, MetadataLoginResult.Ready);
            }
            if (closing) return;
            core.Navigate(Platform.Login.AbsoluteUri); status.Text = "正在打开官方登录页，请使用官网支持的方式完成登录。";
        }
        catch
        {
            service.LoginDiagnostics.Record(MetadataLoginStage.CacheRefresh, MetadataLoginResult.Failed);
            ShowPageFailure(Platform.CodePrefix + "002：登录页面刷新未完成，请稍后重新打开连接窗口。");
        }
        finally { refreshingLogin = false; if (!closing) login.IsEnabled = true; }
    }
    private void ShowPageFailure(string message)
    {
        if (closing) return;
        pageFailure = true; pageFailureMessage = message; status.Text = message; status.Foreground = Brushes.Firebrick;
    }
    private void UpdateStatus(string message, bool running, bool warning)
    {
        if (closing) return;
        if (pageFailure && Platform.Id == "ucampus" && service.LoginDiagnostics.SessionConfirmed
            && Platform.IsMetadataDocument(browser.CoreWebView2?.Source ?? "")
            && service.LoginDiagnostics.Snapshot().PageFailure is null)
        { pageFailure = false; pageFailureMessage = null; }
        syncMessageVisible = true;
        status.Text = pageFailureMessage ?? message;
        status.Foreground = pageFailure || warning ? Brushes.Firebrick : Brushes.DimGray;
    }
}
