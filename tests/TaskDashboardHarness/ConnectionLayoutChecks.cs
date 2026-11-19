using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TaskDashboardHarness;

internal static class ConnectionLayoutChecks
{
    public static async Task VerifyAsync(TaskDashboardWindow dashboard, string runRoot, Action<bool, string> check)
    {
        var root = Path.Combine(runRoot, "connection-layout"); Directory.CreateDirectory(root);
        var store = new TaskStore(Path.Combine(root, "layout.db")); store.SaveSettings(new(false, false) { AutomaticSyncEnabled = false });
        using var chaoxing = new ChaoxingSyncService(store, root, new WebView2());
        using var rain = new RainSyncService(store, root, RainPlatform.Rain, new WebView2());
        using var changjiang = new RainSyncService(store, root, RainPlatform.Changjiang, new WebView2());
        using var mooc = new MoocSyncService(store, root, new WebView2());
        using var xuetang = new XuetangSyncService(store, root, new WebView2());
        using var ucampus = new MetadataSyncService(MetadataPlatform.Ucampus, store, root, new WebView2());
        using var zhihuishu = new MetadataSyncService(MetadataPlatform.Zhihuishu, store, root, new WebView2());
        using var pta = new MetadataSyncService(MetadataPlatform.Pta, store, root, new WebView2());
        var factories = new (string Id, Func<WorkspacePage> Create)[]
        {
            ("chaoxing", () => new ChaoxingConnectionPage(root, store, () => { }, chaoxing)),
            ("yuketang", () => new RainConnectionPage(rain, () => { })),
            ("changjiang", () => new RainConnectionPage(changjiang, () => { })),
            ("mooc", () => new MoocConnectionPage(mooc, () => { })),
            ("xuetang", () => new XuetangConnectionPage(xuetang, () => { })),
            ("ucampus", () => new MetadataConnectionPage(ucampus, () => { })),
            ("zhihuishu", () => new MetadataConnectionPage(zhihuishu, () => { })),
            ("pta", () => new MetadataConnectionPage(pta, () => { }))
        };
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "synthetic-browser-profile"));
        var metrics = new List<object>();
        foreach (var (id, create) in factories)
        {
            var window = create();
            // Exercise the real window's layout without its account initialization or official navigation.
            var initialize = window.GetType().GetMethod(window is ChaoxingConnectionPage ? "InitializeBrowser" : "Initialize", BindingFlags.Instance | BindingFlags.NonPublic)!;
            window.Loaded -= (RoutedEventHandler)initialize.CreateDelegate(typeof(RoutedEventHandler), window);
            var browser = window is ChaoxingConnectionPage ? (WebView2)window.FindName("Browser")
                : (WebView2)window.GetType().GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            try
            {
                dashboard.Width = 1440; dashboard.Height = 960;
                dashboard.ShowWorkspacePage("layout:" + id, window);
                check(Application.Current.Windows.Count == 1 && Window.GetWindow(window) == dashboard,
                    id + " connection is a page inside the only main window");
                await browser.EnsureCoreWebView2Async(environment);
                var core = browser.CoreWebView2; var requests = 0;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested += (_, request) =>
                {
                    requests++;
                    request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Local Layout Only", "Cache-Control: no-store\r\n");
                };
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, navigation) => loaded.TrySetResult(navigation.IsSuccess);
                core.NavigateToString("<!doctype html><meta charset='utf-8'><style>body{margin:0;font:16px 'Segoe UI','Microsoft YaHei';background:#f5f7fb;color:#24384c}header{padding:20px;background:#fff;border-bottom:1px solid #dde3ed}main{text-align:center;padding:28px 10px}.qr{width:220px;height:220px;margin:20px auto;background:repeating-conic-gradient(#435570 0% 25%,white 0% 50%) 0 0/20px 20px;display:grid;place-items:center;border:12px solid white}.qr span{background:white;padding:10px;font-weight:bold}p{padding:0 10px}</style><header>网页布局验收 · 合成页面</header><main><h2>扫码／授权显示区域</h2><div class='qr'><span>布局占位<br>不能扫码</span></div><p>没有访问官网或真实账号。</p></main>");
                check(await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)), id + " local layout document loads");
                var content = (FrameworkElement)window.Content;
                var panel = Visuals<ConnectionStatusPanel>(content).Single();
                var details = Visuals<Expander>(panel).Single();
                foreach (var scene in new[] { "waiting-default", "confirmed-minimum", "details-minimum" })
                {
                    if (scene != "waiting-default")
                    {
                        dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight;
                        panel.Show(new ConnectionReport(id) { AccountVerified = true, CatalogCovered = true, Courses = 12, CatalogOnly = true });
                    }
                    if (scene == "details-minimum")
                    {
                        panel.Show(new ConnectionReport(id)
                        {
                            AccountVerified = true, CatalogOnly = true, Courses = 12, SelectedCourses = 8,
                            ProblemCodes = Enumerable.Range(1, 16).Select(value => "SYN" + value.ToString("000")).ToArray(),
                            ProblemOutcomes = Enumerable.Repeat("unrecognized_response", 16).ToArray()
                        });
                        panel.ShowPageFailure("certificate_failed", "合成证书失败，保留已确认的登录结果。");
                        details.IsExpanded = true;
                        if (id == "chaoxing") Visuals<Expander>(content).First(item => item != details).IsExpanded = true;
                    }
                    await dashboard.Dispatcher.InvokeAsync(dashboard.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    var browserBounds = browser.TransformToAncestor(content).TransformBounds(new Rect(browser.RenderSize));
                    var cardBounds = panel.TransformToAncestor(content).TransformBounds(new Rect(panel.RenderSize));
                    var scroller = Visuals<ScrollViewer>(content).FirstOrDefault(item => item.Content is StackPanel);
                    metrics.Add(new { platform = id, scene, width = content.ActualWidth, height = content.ActualHeight, browser = browserBounds, card = cardBounds, guideScrollableHeight = scroller?.ScrollableHeight });
                    File.WriteAllText(Path.Combine(root, "layout-metrics.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
                    await CaptureAsync(dashboard, browser, Path.Combine(root, id + "-" + scene + ".png"));
                    check(browserBounds.Width >= 320 && browserBounds.Height >= 400, id + " " + scene + " retains a usable official page and QR area");
                    check(browserBounds.Left >= 0 && browserBounds.Top >= 0 && browserBounds.Right <= content.ActualWidth + 1 && browserBounds.Bottom <= content.ActualHeight + 1,
                        id + " " + scene + " browser remains within the window");
                    if (scene == "confirmed-minimum")
                    {
                        var choose = Visuals<Button>(panel).First(item => Equals(item.Content, "确认课程并同步") && item.IsVisible);
                        var bounds = choose.TransformToAncestor(content).TransformBounds(new Rect(choose.RenderSize));
                        check(choose.IsVisible && bounds.Top >= 0 && bounds.Bottom <= content.ActualHeight,
                            id + " confirmed minimum window keeps its next action visible");
                    }
                    if (scene == "details-minimum")
                    {
                        check(scroller is not null && scroller.ScrollableHeight > 0, id + " long connection details scroll independently of the official browser");
                        scroller!.ScrollToEnd(); window.UpdateLayout();
                        check(scroller.VerticalOffset > 0 && browser.ActualHeight >= 400, id + " scrolling details preserves the official document height");
                        await CaptureAsync(dashboard, browser, Path.Combine(root, id + "-details-scrolled.png"));
                    }
                }
                check(requests == 0 && core.Source == "about:blank", id + " layout acceptance never requests the official website");
            }
            finally { dashboard.ShowDashboard(); window.Close(); }
        }
        File.WriteAllText(Path.Combine(root, "layout-acceptance.json"), JsonSerializer.Serialize(new
        { outcome = "passed", platforms = factories.Length, scenes = metrics.Count, windows = 1, data = "synthetic_only", officialNetworkAccessed = false }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static async Task CaptureAsync(Window window, WebView2 browser, string path)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        using var preview = new MemoryStream();
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
        preview.Position = 0; var page = new BitmapImage(); page.BeginInit(); page.CacheOption = BitmapCacheOption.OnLoad; page.StreamSource = preview; page.EndInit();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(content.RenderSize));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(content.RenderSize));
            drawing.DrawImage(page, browser.TransformToAncestor(content).TransformBounds(new Rect(browser.RenderSize)));
        }
        bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
}
