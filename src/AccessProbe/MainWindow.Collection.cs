using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Web.WebView2.Core;

namespace AccessProbe;

public partial class MainWindow
{
    private CancellationTokenSource? collectionCancellation;
    private long collectionRevision;
    private DateTimeOffset nextCollectionAt;
    private string lastCollectionOutcome = "not_attempted";
    private int collectedTaskCount;
    private int collectedNoticeCount;
    private int collectedPageCount;
    private string collectionTerminationReason = "not_attempted";
    private bool collectedCursorPresent;
    private NoticeHttpDiagnostic? lastNoticeHttpDiagnostic;
    private BrowserNoticeDiagnostic lastBrowserNoticeDiagnostic = new("not_attempted", false, false, null, null, null);
    private Window? resultsWindow;

    private void InvalidateCollection(bool clearBrowserProbe = true, bool clearNoticeProbe = true)
    {
        collectionRevision++;
        collectionCancellation?.Cancel();
        resultsWindow?.Close();
        resultsWindow = null;
        ResetWorkReadDiagnostics();
        if (clearNoticeProbe)
        {
            lastCollectionOutcome = "not_attempted";
            collectedTaskCount = 0;
            collectedNoticeCount = 0;
            collectedPageCount = 0;
            collectionTerminationReason = "not_attempted";
            collectedCursorPresent = false;
            lastNoticeHttpDiagnostic = null;
        }
        if (clearBrowserProbe) lastBrowserNoticeDiagnostic = new("not_attempted", false, false, null, null, null);
    }

    private async void VerifyBrowserNotice(object sender, RoutedEventArgs args)
    {
        if (!initialized || clearing || collectionCancellation != null) return;
        if (DateTimeOffset.UtcNow < nextCollectionAt)
        {
            StatusText.Text = "请等待 60 秒冷却结束后重试，避免频繁请求。";
            return;
        }
        var currentAddress = Browser.CoreWebView2.Source;
        if (currentAddress == "about:blank" || !ProbePolicy.IsAllowedNavigation(currentAddress))
        {
            StatusText.Text = "请先在内嵌浏览器打开官方 HTTPS 页面，再进行浏览器上下文验证。";
            return;
        }
        if (MessageBox.Show("将从当前官方页面上下文向固定学习通通知接口发送一次只读 POST，使用 WebView 自身的会话规则；不会读取响应正文，也不会跟随重定向。浏览器可能因跨域策略拒绝页面读取，即使网络请求已发出。仅记录请求/响应钩子是否观察到、状态码和结果类别。继续？",
            "授权浏览器上下文验证", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        InvalidateCollection(false, false);
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        collectionCancellation = cancellation;
        lastBrowserNoticeDiagnostic = new("not_attempted", false, false, null, null, null);
        CollectButton.IsEnabled = BrowserProbeButton.IsEnabled = ReadWorkButton.IsEnabled = false;
        nextCollectionAt = DateTimeOffset.UtcNow.AddSeconds(60);
        StatusText.Text = "正在使用内嵌浏览器上下文验证通知接口；不读取正文。页面导航或清理会取消本次诊断。";
        try
        {
            var result = await ProbeNoticeInBrowserAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closing || clearing) return;
            lastBrowserNoticeDiagnostic = result;
            StatusText.Text = result.Outcome switch
            {
                "fetch_rejected_endpoint_status_observed" => $"页面 fetch 被拒绝；请求钩子={(result.RequestObserved ? "已观察" : "未观察到")}，窗口内观察到接口 HTTP {result.EndpointStatusObservedDuringWindow}。该响应不能保证对应本次 fetch，未读取正文。",
                "fetch_rejected_response_without_status" => $"页面 fetch 被拒绝；请求钩子={(result.RequestObserved ? "已观察" : "未观察到")}，观察到接口响应事件但没有状态码。",
                "fetch_rejected_after_request_observed" => "页面 fetch 被拒绝；已观察到固定接口进入 WebView 请求钩子，但没有观察到接口响应。请求钩子不证明服务器已收到请求。",
                "fetch_rejected_without_request_observed" => "页面 fetch 被拒绝，且未观察到固定接口进入 WebView 请求钩子；可能是请求未启动、策略拦截或观察过滤未命中，无法区分。",
                "response_received" => $"WebView 页面 fetch 收到 HTTP {result.FetchStatusCode}（响应类型 {result.FetchResponseType ?? "未知"}）；请求钩子={(result.RequestObserved ? "已观察" : "未观察到")}，响应事件={(result.ResponseObserved ? "已观察" : "未观察到")}。未读取正文。",
                "opaque_response" => $"WebView 已发出跨域 POST，页面只得到 opaque 响应（不暴露状态码）；请求钩子={(result.RequestObserved ? "已观察" : "未观察到")}，响应事件={(result.ResponseObserved ? "已观察" : "未观察到")}。请再执行“读取通知（试验）”获取独立 HTTP 状态。",
                "redirect_status_hidden" => $"页面 fetch 遇到重定向，但浏览器隐藏了状态码；请求钩子={(result.RequestObserved ? "已观察" : "未观察到")}，响应事件={(result.ResponseObserved ? "已观察" : "未观察到")}，未跟随跳转。",
                "fetch_response_without_http_event" => $"页面 fetch 返回状态 {result.FetchStatusCode}（响应类型 {result.FetchResponseType ?? "未知"}），但未观察到接口 HTTP 响应事件；请求钩子={(result.RequestObserved ? "已观察" : "未观察到")}。结果不完整。",
                _ => "浏览器上下文诊断未能完成；未读取响应正文，也未更新作业状态。"
            };
        }
        catch (TimeoutException)
        {
            if (!closing && !clearing) StatusText.Text = "浏览器上下文验证已超时；未采用结果。";
            lastBrowserNoticeDiagnostic = new("cancelled_or_timeout", false, false, null, null, null);
        }
        catch (OperationCanceledException)
        {
            if (!closing && !clearing) StatusText.Text = "浏览器上下文验证已因超时或页面变化取消；未采用结果。";
            lastBrowserNoticeDiagnostic = new("cancelled_or_timeout", false, false, null, null, null);
        }
        catch
        {
            if (!closing && !clearing) StatusText.Text = "浏览器上下文验证失败；未记录异常正文、地址或会话信息。";
            lastBrowserNoticeDiagnostic = new("probe_error", false, false, null, null, null);
        }
        finally
        {
            collectionCancellation = null;
            cancellation.Dispose();
            if (!closing && !clearing) CollectButton.IsEnabled = BrowserProbeButton.IsEnabled = ReadWorkButton.IsEnabled = initialized;
        }
    }

    private async Task<BrowserNoticeDiagnostic> ProbeNoticeInBrowserAsync(CancellationToken cancellation)
    {
        var core = Browser.CoreWebView2;
        var source = core.Source;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) || !ProbePolicy.IsAllowedNavigation(source))
            return new("page_not_ready", false, false, null, null, null);

        var sourceOrigin = sourceUri.GetLeftPart(UriPartial.Authority);
        var nonce = Guid.NewGuid().ToString("N");
        var fetchResult = new TaskCompletionSource<BrowserFetchMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseObserved = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2WebMessageReceivedEventArgs> messageHandler = (_, message) =>
        {
            if (!Uri.TryCreate(message.Source, UriKind.Absolute, out var messageSource)
                || !messageSource.GetLeftPart(UriPartial.Authority).Equals(sourceOrigin, StringComparison.OrdinalIgnoreCase)) return;
            if (NoticeReader.TryParseBrowserFetchMessage(message.WebMessageAsJson, nonce, out var parsedMessage)
                && parsedMessage is not null)
                fetchResult.TrySetResult(parsedMessage);
        };
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> responseHandler = (_, response) =>
        {
            if (!NoticeReader.IsFixedNoticeRequest(response.Request.Method, response.Request.Uri)) return;
            responseObserved.TrySetResult(response.Response is { StatusCode: > 0 } responseView
                ? responseView.StatusCode : null);
        };
        EventHandler<CoreWebView2WebResourceRequestedEventArgs> requestHandler = (_, request) =>
        {
            if (NoticeReader.IsFixedNoticeRequest(request.Request.Method, request.Request.Uri))
                requestObserved.TrySetResult(true);
        };

        core.WebMessageReceived += messageHandler;
        core.WebResourceResponseReceived += responseHandler;
        core.WebResourceRequested += requestHandler;
        var requestFilterAdded = false;
        try
        {
            core.AddWebResourceRequestedFilter(NoticeReader.Endpoint,
                CoreWebView2WebResourceContext.Fetch,
                CoreWebView2WebResourceRequestSourceKinds.All);
            requestFilterAdded = true;
            var endpoint = JsonSerializer.Serialize(NoticeReader.Endpoint);
            var nonceJson = JsonSerializer.Serialize(nonce);
            var fieldsJson = JsonSerializer.Serialize(NoticeReader.CreateFormFields());
            var script = "(() => { const endpoint = " + endpoint + "; const nonce = " + nonceJson
                + "; const fields = " + fieldsJson + ";"
                + "; const post = value => window.chrome.webview.postMessage(value);"
                + " fetch(endpoint, { method: 'POST', mode: 'no-cors', credentials: 'include', redirect: 'manual', cache: 'no-store', body: new URLSearchParams(fields) })"
                + " .then(response => post({ nonce, kind: 'response', status: response.status, responseType: response.type }))"
                + " .catch(() => post({ nonce, kind: 'fetch_rejected' })); return 'started'; })()";
            await core.ExecuteScriptAsync(script).WaitAsync(cancellation);
            var fetch = await fetchResult.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            if (!responseObserved.Task.IsCompleted)
                await Task.WhenAny(responseObserved.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellation));
            var requestSeen = requestObserved.Task.IsCompletedSuccessfully && requestObserved.Task.Result;
            var responseSeen = responseObserved.Task.IsCompletedSuccessfully;
            int? httpStatus = responseSeen ? responseObserved.Task.Result : null;
            return NoticeReader.ClassifyBrowserProbe(requestSeen, responseSeen, httpStatus,
                fetch.StatusCode, fetch.ResponseType, fetch.Rejected);
        }
        finally
        {
            core.WebMessageReceived -= messageHandler;
            core.WebResourceResponseReceived -= responseHandler;
            core.WebResourceRequested -= requestHandler;
            if (requestFilterAdded)
                core.RemoveWebResourceRequestedFilter(NoticeReader.Endpoint,
                    CoreWebView2WebResourceContext.Fetch,
                    CoreWebView2WebResourceRequestSourceKinds.All);
        }
    }

    private async void CollectNotices(object sender, RoutedEventArgs args)
    {
        if (!initialized || clearing || collectionCancellation != null) return;
        if (DateTimeOffset.UtcNow < nextCollectionAt)
        {
            StatusText.Text = "请等待 60 秒冷却结束后重试，避免频繁请求。";
            return;
        }
        if (MessageBox.Show($"将使用本窗口的登录会话，向学习通通知接口串行发送最多 {NoticeReader.MaximumPages} 次只读 POST 请求，以游标读取近期通知。不会重试、提交或保存正文、Cookie、游标。通知不是完整作业清单，提交状态仍未知。继续？",
            "授权本次只读试验", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        InvalidateCollection(clearBrowserProbe: false);
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        collectionCancellation = cancellation;
        CollectButton.IsEnabled = BrowserProbeButton.IsEnabled = ReadWorkButton.IsEnabled = false;
        nextCollectionAt = DateTimeOffset.UtcNow.AddSeconds(60);
        StatusText.Text = $"正在读取近期通知（最多 {NoticeReader.MaximumPages} 页）；页面导航或清理登录数据将取消本次结果。";
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() };
            var endpoint = new Uri(NoticeReader.Endpoint);
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(NoticeReader.Endpoint);
            cancellation.Token.ThrowIfCancellationRequested();
            foreach (var cookie in cookies)
            {
                var httpCookie = new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                { Secure = cookie.IsSecure, HttpOnly = cookie.IsHttpOnly };
                handler.CookieContainer.Add(endpoint, httpCookie);
            }
            cookies.Clear();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var result = await NoticeReader.FetchRecentAsync(client, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closing || clearing) return;
            lastCollectionOutcome = result.Outcome;
            collectedTaskCount = result.Tasks.Count;
            collectedNoticeCount = result.Total;
            collectedPageCount = result.PageCount;
            collectionTerminationReason = result.TerminationReason;
            collectedCursorPresent = result.NextCursorPresent;
            lastNoticeHttpDiagnostic = result.HttpDiagnostic;
            StatusText.Text = result.Outcome switch
            {
                "parsed_partial" => $"读取 {result.PageCount} 页，共 {result.Total} 条通知，得到 {result.Tasks.Count} 条作业通知；{result.Skipped} 条结构未识别，{result.InvalidTimes} 个时间异常。停止：{FormatPaginationTermination(result.TerminationReason)}。仅近期通知窗口，不能确认作业总数。",
                "redirect_received" => $"接口返回 HTTP {result.HttpDiagnostic?.StatusCode} 重定向；目标类别：{result.HttpDiagnostic?.RedirectCategory}。未跟随跳转，不能据此判断登录或作业状态。",
                "unauthorized" => "通知接口返回 HTTP 401：本次请求未获认证。网页登录成功不等于此接口会话有效。",
                "forbidden" => "通知接口返回 HTTP 403：访问被拒绝。不能仅凭此响应判断是权限、会话还是风控原因；未自动重试。",
                "api_rejected" => "通知接口返回 JSON 失败标记：本次会话或接口权限未获确认；未把它当成空作业列表。",
                "html_not_task_data" => "接口返回网页而非任务 JSON，可能是登录或验证页；请核对官方登录。不能判断没有作业。",
                "rate_limited" => "平台限制了请求频率。本次不自动重试，请稍后再试。",
                "response_too_large" => "响应超过 2 MiB 安全上限，已停止；不能判断作业数量。",
                "http_error" => "接口响应失败，未更新任何作业状态。请稍后重试。",
                _ => "返回结构与已知通知格式不符；未当成空作业列表处理。"
            };
            if (result.Outcome == "parsed_partial") ShowNoticeResults(result);
        }
        catch (OperationCanceledException)
        {
            if (!closing && !clearing) StatusText.Text = "采集已因超时或页面变化取消，未采用结果。";
            lastCollectionOutcome = "cancelled_or_timeout";
        }
        catch (HttpRequestException)
        {
            if (!closing && !clearing) StatusText.Text = "读取失败：网络或 TLS 连接异常；未关闭证书校验，未自动重试。";
            lastCollectionOutcome = "network_error";
        }
        catch
        {
            if (!closing && !clearing) StatusText.Text = "本次会话或读取操作失败，未记录敏感异常正文。请重新打开官方页核对登录。";
            lastCollectionOutcome = "session_or_read_error";
        }
        finally
        {
            collectionCancellation = null;
            cancellation.Dispose();
            if (!closing && !clearing) CollectButton.IsEnabled = BrowserProbeButton.IsEnabled = ReadWorkButton.IsEnabled = initialized;
        }
    }

    private static string FormatPaginationTermination(string reason) => reason switch
    {
        "empty_page" => "空页",
        "last_page" => "接口标记末页",
        "cursor_missing" => "响应未提供后续游标",
        "cursor_repeated" => "游标重复，已停止以防循环",
        "notification_limit" => "达到通知数量上限",
        "page_limit" => "达到页数上限",
        _ => "读取未完成"
    };

    private void ShowNoticeResults(NoticeResult result)
    {
        var panel = new DockPanel { Margin = new Thickness(16) };
        var summary = new TextBlock
        {
            Text = $"本次读取 {result.PageCount} 页、{result.Total} 条通知，保留 {result.Tasks.Count} 条作业通知。仅近期通知窗口，不是完整待办；可能有缺项或过时项。截止时间来自通知，提交状态未知。结果仅保留在内存，导航或退出后清除。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);
        var table = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
            ClipboardCopyMode = DataGridClipboardCopyMode.None, ItemsSource = result.Tasks
        };
        foreach (var (header, property) in new[] { ("作业通知", "Title"), ("课程", "Course"), ("通知截止时间", "DeadlineText"), ("平台提交状态", "SubmissionState") })
            table.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        panel.Children.Add(table);
        resultsWindow = new Window { Owner = this, Title = "通知采集结果 · 覆盖不完整", Width = 1000, Height = 480, MinWidth = 700, MinHeight = 320, Content = panel };
        resultsWindow.Show();
    }
}
