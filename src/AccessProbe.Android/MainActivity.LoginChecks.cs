using AccessProbe;
using Android.Webkit;
using System.Net;
using System.Text;
using System.Text.Json;
using Path = System.IO.Path;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    // Explicit emulator-only entry with a separate browser profile and an isolated task store.
    private async Task RunLoginNavigationChecks()
    {
        var directory = Path.Combine(MobileData.Root(this), "synthetic-login-test");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        var navigationRequests = new List<string>();
        var output = Path.Combine(directory, "login-checks.json");
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidDataException(label);
            checks.Add(label);
        }
        void Report(string outcome, string? error = null) => File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            schema = 1, outcome, checks = checks.Count, results = checks, error,
            officialNetworkAccessed = false, productionDataAccessed = false, credentialValuesLogged = false,
            realAccountAcceptance = "pending"
        }));
        try
        {
            var protector = new AndroidDataProtector("LearningTaskAggregator.SyntheticLogin.v1");
            store = new TaskStore(Path.Combine(directory, "tasks.db"), protector); vault = new(store, protector);
            var handlers = new List<SyntheticLoginHandler>();
            sync = new(store, vault, platform =>
            {
                if (platform != "chaoxing") throw new InvalidDataException("Unexpected synthetic platform.");
                var handler = new SyntheticLoginHandler(); handlers.Add(handler); return handler;
            });
            store.SaveSettings(new(false, false));
            CreateLoginPage("chaoxing"); loginLifetime = new();
            var client = new OfflineLoginClient(LoginPageStarted, message => status.Text = message, navigationRequests);
            loginClient = client; web!.SetWebViewClient(client);
            var manager = Android.Webkit.CookieManager.Instance!;
            async Task Cookie(string origin, string header)
            {
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                manager.SetCookie(origin, header, new CookieWriteResult(completed));
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            async Task WaitFor(Func<bool> condition)
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (condition()) return;
                    await Task.Delay(100);
                }
                throw new TimeoutException("Synthetic navigation did not finish.");
            }
            async Task LoadCallback()
            {
                client.ResetNavigation();
                const string address = "https://passport2.chaoxing.com/login";
                web!.LoadDataWithBaseURL(address,
                    "<!doctype html><meta name='viewport' content='width=device-width'><h2 id='callback-ready'>隔离扫码返回测试</h2><p>合成官网确认，无真实账号。</p>",
                    "text/html", "UTF-8", address);
                await WaitFor(() => web?.Url == address);
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    if (await Evaluate("document.readyState==='complete' && document.getElementById('callback-ready')!==null") == "true") return;
                    await Task.Delay(100);
                }
                throw new TimeoutException("Synthetic callback did not load.");
            }
            async Task WaitForCoursePage(string address)
            {
                await WaitFor(() => web?.Url == address);
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (await Evaluate("location.href===" + JsonSerializer.Serialize(address)
                        + " && document.readyState==='complete' && document.getElementById('course-ready')!==null") == "true") return;
                    await Task.Delay(100);
                }
                throw new TimeoutException("Synthetic course page did not load.");
            }
            await Cookie("https://chaoxing.com/", "_uid=900001; Domain=.chaoxing.com; Path=/; Secure; HttpOnly");
            await Cookie("https://mooc2-ans.chaoxing.com/mooc2-ans/", "SyntheticCourseSession=SYNTHETIC-COURSE; Path=/mooc2-ans; Secure; HttpOnly");
            await Cookie("https://example.invalid/", "SyntheticForeignSession=SYNTHETIC-FOREIGN; Path=/; Secure");
            manager.Flush();
            var cookies = MobilePlatforms.ReadBrowserCookies("chaoxing");
            var catalogUri = new Uri(ChaoxingSyncParser.Catalog);
            Check(cookies.Any(cookie => cookie.Name == "SyntheticCourseSession" && cookie.AppliesTo(catalogUri)), "course host and scoped HttpOnly cookie captured");
            Check(cookies.All(cookie => cookie.Domain != "example.invalid" && cookie.Name != "SyntheticForeignSession"), "foreign cookie excluded from platform session");
            Check(cookies.Select(cookie => cookie.Key).Distinct().Count() == cookies.Length, "cookie aliases remain unique");
            Check(MobilePlatforms.FrameAllowed("xuetang", "https://turing.captcha.qcloud.com/template/drag_ele.html")
                && !MobilePlatforms.Allowed("xuetang", "https://turing.captcha.qcloud.com/template/drag_ele.html"), "official verifier stays limited to login frames");

            const string http = "http://i.mooc.chaoxing.com/space/index?enc=SYNTHETIC%2BVALUE%26NEXT#after-scan";
            const string https = "https://i.mooc.chaoxing.com/space/index?enc=SYNTHETIC%2BVALUE%26NEXT#after-scan";
            await LoadCallback();
            await Evaluate("location.href=" + JsonSerializer.Serialize(http));
            await WaitForCoursePage(https);
            Check(web!.Url == https, "scan confirmation HTTP redirect reaches the equivalent HTTPS page");
            Check(client.Requests.All(address => !address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)), "no cleartext request reaches the reader");

            var requestsBefore = client.Requests.Count(address => address.StartsWith("https://i.mooc.chaoxing.com/space/index", StringComparison.Ordinal));
            await Evaluate("location.href=" + JsonSerializer.Serialize(http));
            await WaitFor(() => status.Text?.Contains("反复跳回", StringComparison.Ordinal) == true);
            Check(client.Requests.Count(address => address.StartsWith("https://i.mooc.chaoxing.com/space/index", StringComparison.Ordinal)) == requestsBefore, "repeated official downgrade is stopped without looping");
            await LoadCallback();
            await Evaluate("location.href=" + JsonSerializer.Serialize(http));
            await WaitForCoursePage(https);
            Check(web!.Url == https, "manual retry resets the redirect limit");

            await LoadCallback();
            await Evaluate("window.open(" + JsonSerializer.Serialize(http) + ", '_blank')");
            await WaitForCoursePage(https);
            Check(web!.Url == https, "official popup callback also upgrades to HTTPS");
            await Evaluate("location.href='https://chaoxing.com.example.invalid/finish'");
            await WaitFor(() => status.Text?.Contains("尚未适配", StringComparison.Ordinal) == true);
            Check(web!.Url == https && client.Requests.All(address => !address.Contains("example.invalid", StringComparison.Ordinal)), "unrelated navigation remains blocked");

            await CaptureLogin("chaoxing");
            Check(web is null && loginPlatform is null, "successful course validation returns from login to platform page");
            Check(store.LoadPlatformSessionInfo("chaoxing").State == "ready" && vault.Load("chaoxing") is not null, "validated official session is encrypted and confirmed");
            var recording = store.LoadCourseRecording("chaoxing");
            Check(recording.Courses.Count == 1 && !recording.SelectionConfirmed, "course catalog is shown before task collection");
            Check(handlers.Count == 1 && handlers[0].CatalogRequests == 1 && handlers[0].CourseCookieAccepted, "captured course cookie reaches the existing course reader");
            Check(store.LoadTasks().Count == 0, "login alone does not import unselected tasks");
            Check(status.Text?.Contains("登录已保存", StringComparison.Ordinal) == true, "saved login status is visible");
            Report("passed");
        }
        catch (Exception error) { Report("failed", error.GetType().Name + ": " + error.Message); }
    }

    private sealed class CookieWriteResult(TaskCompletionSource<bool> completed) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value)
        {
            if (value?.ToString() == "true") completed.TrySetResult(true);
            else completed.TrySetException(new InvalidDataException("Synthetic browser Cookie update failed."));
        }
    }

    private sealed class OfflineLoginClient(Action started, Action<string> message, List<string> requests) : OfficialLoginClient("chaoxing", message, started)
    {
        public List<string> Requests => requests;
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            var address = request?.Url?.ToString() ?? "";
            lock (Requests) Requests.Add(address);
            const string html = "<!doctype html><meta name='viewport' content='width=device-width'><h2 id='course-ready'>隔离扫码返回测试</h2><p>合成课程主页，无真实网络。</p>";
            return new("text/html", "UTF-8", new MemoryStream(Encoding.UTF8.GetBytes(html)));
        }
    }

    private sealed class SyntheticLoginHandler : HttpMessageHandler
    {
        public int CatalogRequests { get; private set; }
        public bool CourseCookieAccepted { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = request.RequestUri!;
            var cookies = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : "";
            if (address.AbsoluteUri == ChaoxingSyncParser.Portal && cookies.Contains("_uid=900001", StringComparison.Ordinal))
                return Task.FromResult(Page("<input id='fid' value='700001'>"));
            if (address.Host == "mooc2-ans.chaoxing.com" && address.AbsolutePath == "/mooc2-ans/visit/courselistdata")
            {
                CatalogRequests++;
                CourseCookieAccepted = cookies.Contains("SyntheticCourseSession=SYNTHETIC-COURSE", StringComparison.Ordinal);
                return Task.FromResult(CourseCookieAccepted
                    ? Page("<div class='course-list'><a href='https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid=710001&amp;clazzid=720001&amp;cpi=730001'><span class='course-name'>扫码验证课程</span></a></div>")
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
            throw new InvalidDataException("Unexpected synthetic course request.");
        }
        private static HttpResponseMessage Page(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    }
}
