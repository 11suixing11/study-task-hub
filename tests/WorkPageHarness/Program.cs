using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WorkPageHarness;

// A separate developer executable: production has no fixture mode or allowlist bypass.
internal static class Program
{
    private const string Origin = "https://mooc1.chaoxing.com";
    private const string TaskPath = "/mooc-ans/mooc2/work/task";
    private const string Target = Origin + TaskPath
        + "?courseId=101&classId=202&cpi=303&workId=404&answerId=505&enc=SYNTHETIC%2BONLY%26VALUE";
    private static string runRoot = string.Empty;
    private static int checkCount;
    private static int httpResourceCount;
    private static int httpLocalResponseCount;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--project-root") return 2;
        var projectRoot = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(projectRoot, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        runRoot = Path.Combine(projectRoot, ".local", "work-page-harness", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(Path.Combine(projectRoot, ".local", "work-page-harness", "latest-run.txt"), runRoot);
        Environment.SetEnvironmentVariable("TEMP", runRoot);
        Environment.SetEnvironmentVariable("TMP", runRoot);

        var app = new Application();
        var main = new MainWindow(runRoot) { Title = "学习通 · 离线合成页面验证" };
        var browser = (WebView2)main.FindName("Browser");
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 10), IsEnabled = false };
        var status = new TextBlock { Text = "离线检查准备中", Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
        foreach (var (title, path) in new[]
        {
            ("合成作业列表", "/__synthetic__/list"), ("内嵌作业列表", "/__synthetic__/frame"),
            ("读取上限样本", "/__synthetic__/limit"), ("未知页面", "/__synthetic__/unknown")
        })
        {
            var button = new Button { Content = title, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) => browser.CoreWebView2.Navigate(Origin + path);
            controls.Children.Add(button);
        }
        controls.Children.Add(status);
        DockPanel.SetDock(controls, Dock.Top);
        ((DockPanel)main.Content).Children.Insert(0, controls);

        browser.CoreWebView2InitializationCompleted += (_, result) =>
        {
            if (!result.IsSuccess) return;
            var core = browser.CoreWebView2;
            // Every resource is fulfilled locally, including task URLs. No network request leaves this profile.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                var uri = new Uri(request.Request.Uri);
                if (uri.Scheme == Uri.UriSchemeHttp) httpResourceCount++;
                if (uri.Scheme == Uri.UriSchemeHttps && uri.AbsolutePath == "/__synthetic__/downgrade")
                {
                    request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 302, "Found",
                        "Location: http://mooc1.chaoxing.com/__synthetic__/downgrade\r\nCache-Control: no-store\r\n");
                    return;
                }
                var html = PageFor(uri);
                request.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; frame-src https://mooc1.chaoxing.com\r\n");
            };
            main.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                try
                {
                    await RunBrowserChecksAsync(core);
                    WriteSummary("passed");
                    status.Text = $"{checkCount} 项浏览器检查通过；全部离线";
                    controls.IsEnabled = true;
                    await NavigateAsync(core, Origin + "/__synthetic__/list");
                }
                catch
                {
                    WriteSummary("failed");
                    status.Text = $"浏览器检查失败（第 {checkCount} 项），未记录页面内容";
                }
            }));
        };

        app.Run(main);
        return 0;
    }

    private static void WriteSummary(string outcome) => File.WriteAllText(Path.Combine(runRoot, "browser-checks.json"),
        JsonSerializer.Serialize(new { outcome, checkCount, httpResourceCount, httpLocalResponseCount, data = "synthetic_only", network = "intercepted_offline" }, new JsonSerializerOptions { WriteIndented = true }));

    private static void Check(bool condition)
    {
        checkCount++;
        if (!condition) throw new InvalidOperationException("Synthetic browser check failed.");
    }

    private static async Task NavigateAsync(CoreWebView2 core, string address)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs navigation) => completed.TrySetResult(navigation.IsSuccess);
        core.NavigationCompleted += Finished;
        try
        {
            core.Navigate(address);
            if (!await completed.Task.WaitAsync(TimeSpan.FromSeconds(15))) throw new InvalidOperationException("Synthetic navigation failed.");
        }
        finally { core.NavigationCompleted -= Finished; }
    }

    private static async Task<WorkPageSnapshot> SnapshotAsync(CoreWebView2 core, string path)
    {
        await NavigateAsync(core, Origin + path);
        var json = await core.ExecuteScriptAsync(WorkPageSnapshotReader.Script).WaitAsync(TimeSpan.FromSeconds(10));
        Check(!json.Contains("CONTENT_EXCLUDED", StringComparison.Ordinal));
        return WorkPageSnapshotReader.Parse(json);
    }

    private static async Task RunBrowserChecksAsync(CoreWebView2 core)
    {
        var list = await SnapshotAsync(core, "/__synthetic__/list");
        Check(list.List?.Tasks.Count == 3);
        Check(list.List!.Tasks[0].Title == "示例作业 A & 数据练习" && list.List.Tasks[0].PlatformStatusText == "已完成");
        Check(list.List.Tasks[0].Location?.PageUri.OriginalString == Target);
        Check(list.List.Tasks[1].PlatformStatusText == "未完成" && list.List.Tasks[1].Location?.AnswerId is null);
        Check(list.List.Tasks[2].Location is null && list.List.InvalidLinks == 1);
        Check(list.List.Tasks.All(task => task.DeadlineUtc is null));

        var detail = await SnapshotAsync(core, TaskPath + new Uri(Target).Query);
        Check(detail.Detail?.Title == "示例作业 A & 数据练习");
        Check(detail.Detail?.AnswerWindow is { Start: { Year: null, Month: 11, Day: 2 }, End: { Year: null, Month: 11, Day: 12 }, DeadlineUtc: null });
        var ambiguous = await SnapshotAsync(core, "/__synthetic__/ambiguous");
        Check(ambiguous.Outcome == "ambiguous_detail" && ambiguous.Detail is null);
        var unknown = await SnapshotAsync(core, "/__synthetic__/unknown");
        Check(unknown.Outcome == "not_found" && unknown.List is null);
        var limited = await SnapshotAsync(core, "/__synthetic__/limit");
        Check(limited.RowLimitReached && limited.List?.Tasks.Count == 500);
        var oversized = await SnapshotAsync(core, "/__synthetic__/oversized");
        Check(oversized.Outcome == "response_too_large" && oversized.List is null);
        await NavigateAsync(core, "https://i.chaoxing.com/__synthetic__/list");
        Check(WorkPageSnapshotReader.Parse(await core.ExecuteScriptAsync(WorkPageSnapshotReader.Script)).Outcome == "unsupported_page");

        var frameReady = new TaskCompletionSource<CoreWebView2Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Created(object? sender, CoreWebView2FrameCreatedEventArgs created)
        {
            var frame = created.Frame;
            frame.NavigationCompleted += (_, result) => { if (result.IsSuccess) frameReady.TrySetResult(frame); };
        }
        core.FrameCreated += Created;
        try
        {
            await NavigateAsync(core, Origin + "/__synthetic__/frame");
            var frame = await frameReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(WorkPageSnapshotReader.Parse(await core.ExecuteScriptAsync(WorkPageSnapshotReader.Script)).Outcome == "not_found");
            var json = await frame.ExecuteScriptAsync(WorkPageSnapshotReader.Script);
            Check(!json.Contains("CONTENT_EXCLUDED", StringComparison.Ordinal));
            Check(WorkPageSnapshotReader.Parse(json).List?.Tasks.Count == 3);
        }
        finally { core.FrameCreated -= Created; }
        await RunHttpsNavigationChecksAsync(core);
    }

    private static async Task RunHttpsNavigationChecksAsync(CoreWebView2 core)
    {
        // Exercise the production handlers, without UI input or any real-network response.
        httpResourceCount = 0;
        httpLocalResponseCount = 0;
        void LocalHttpResponse(object? sender, CoreWebView2WebResourceRequestedEventArgs request)
        {
            if (new Uri(request.Request.Uri).Scheme == Uri.UriSchemeHttp && request.Response is { StatusCode: 403 })
                httpLocalResponseCount++;
        }
        core.WebResourceRequested += LocalHttpResponse;
        var beforeUpgrade = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs navigation)
        {
            if (navigation.Uri == "http://mooc1.chaoxing.com/__synthetic__/list?enc=SYNTHETIC%2BONLY")
                beforeUpgrade.TrySetResult(navigation.Cancel);
        }
        var afterUpgrade = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs navigation)
        {
            if (core.Source == Origin + "/__synthetic__/list?enc=SYNTHETIC%2BONLY" && navigation.IsSuccess)
                afterUpgrade.TrySetResult(true);
        }
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate("http://mooc1.chaoxing.com/__synthetic__/list?enc=SYNTHETIC%2BONLY");
            Check(await beforeUpgrade.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(await afterUpgrade.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(WorkPageSnapshotReader.Parse(await core.ExecuteScriptAsync(WorkPageSnapshotReader.Script)).List?.Tasks.Count == 3);
            Check(httpResourceCount == httpLocalResponseCount);
        }
        finally { core.NavigationStarting -= Starting; core.NavigationCompleted -= Completed; }

        var downgraded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigationStarts = 0;
        void LoopStarting(object? sender, CoreWebView2NavigationStartingEventArgs navigation)
        {
            if (new Uri(navigation.Uri).AbsolutePath == "/__synthetic__/downgrade")
            {
                navigationStarts++;
                if (navigation.Uri.StartsWith("http://", StringComparison.Ordinal) && navigation.IsRedirected)
                    downgraded.TrySetResult(navigation.Cancel);
            }
        }
        core.NavigationStarting += LoopStarting;
        try
        {
            await NavigateAsync(core, Origin + "/__synthetic__/list");
            core.Navigate("http://mooc1.chaoxing.com/__synthetic__/downgrade");
            Check(await downgraded.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(navigationStarts == 3);
            Check(httpResourceCount == httpLocalResponseCount);
        }
        finally { core.NavigationStarting -= LoopStarting; }
        await NavigateAsync(core, Origin + "/__synthetic__/list");

        var embedded = new TaskCompletionSource<CoreWebView2Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void FrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs created)
        {
            var frame = created.Frame;
            frame.NavigationCompleted += (_, result) => { if (result.IsSuccess) embedded.TrySetResult(frame); };
        }
        core.FrameCreated += FrameCreated;
        try
        {
            await NavigateAsync(core, Origin + "/__synthetic__/http-frame");
            var frame = await embedded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(WorkPageSnapshotReader.Parse(await frame.ExecuteScriptAsync(WorkPageSnapshotReader.Script)).List?.Tasks.Count == 3);
            Check(httpResourceCount == httpLocalResponseCount);
        }
        finally { core.FrameCreated -= FrameCreated; core.WebResourceRequested -= LocalHttpResponse; }
    }

    private static string PageFor(Uri uri)
    {
        var content = uri.AbsolutePath switch
        {
            "/__synthetic__/list" => ListHtml(),
            "/__synthetic__/frame" => "<h2>课程中的内嵌作业页</h2><iframe style='width:95%;height:380px' src='" + Origin + "/__synthetic__/list'></iframe>",
            "/__synthetic__/http-frame" => "<h2>HTTP 内嵌页升级样本</h2><iframe style='width:95%;height:380px' src='http://mooc1.chaoxing.com/__synthetic__/list'></iframe>",
            "/__synthetic__/limit" => "<ul>" + string.Concat(Enumerable.Range(1, 501).Select(index => Row(Target, $"上限样本 {index}", "已完成"))) + "</ul>",
            "/__synthetic__/oversized" => "<ul>" + Row(Target, new string('x', WorkListParser.MaximumBytes + 1), "已完成") + "</ul>",
            "/__synthetic__/ambiguous" => DetailHtml() + DetailHtml(),
            TaskPath => DetailHtml(),
            _ => "<h2>未知页面</h2><p>CONTENT_EXCLUDED</p>"
        };
        return "<!doctype html><html lang='zh-CN'><meta charset='utf-8'><style>body{font:16px 'Microsoft YaHei';padding:12px}"
            + "li{margin:12px 0;padding:10px;border:1px solid #ddd;list-style:none}p{margin:8px 0}.status{color:#20734a}</style><body>"
            + content + "</body></html>";
    }

    private static string ListHtml() => "<h2>离线合成课程 · 作业</h2><ul>"
        + Row(Target, "示例作业 A & 数据练习", "已完成")
        + Row(Target.Replace("answerId=505&", string.Empty, StringComparison.Ordinal).Replace("workId=404", "workId=405", StringComparison.Ordinal), "示例作业 B", "未完成")
        + Row("https://example.invalid/unknown", "示例作业 C（无可用链接）", "已完成")
        + "<div style='display:none'>" + Row(Target, "隐藏样本", "已完成") + "</div></ul><div>CONTENT_EXCLUDED</div>";

    private static string Row(string address, string title, string status) => $"<li onclick='goTask(this);' data='{WebUtility.HtmlEncode(address)}'>"
        + $"<div class='right-content'><p class='overHidden2 fl'>{WebUtility.HtmlEncode(title)}</p><p class='status fl'>{status}</p>"
        + "<a class='insightBtn' href='https://stat2-ans.chaoxing.com/study-knowledge/ans'>智能分析 CONTENT_EXCLUDED</a></div>"
        + "<div class='time notOver'>剩余48小时</div></li>";

    private static string DetailHtml() => "<div class='detailsHead'><h2 class='mark_title'>示例作业 A &amp; 数据练习</h2>"
        + "<div class='infoHead fl'><span>CONTENT_EXCLUDED</span><div><p class='rightBord'>作答时间:<em>11-02 09:15</em>至<em>11-12 23:59</em></p>"
        + "<p class='rightBord'>提交时间:<em>CONTENT_EXCLUDED</em></p></div></div></div><div class='questions'>CONTENT_EXCLUDED</div>";
}
