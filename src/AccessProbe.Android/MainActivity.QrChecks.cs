using Android.Graphics;
using Android.Provider;
using Android.Webkit;
using System.Text.Json;
using Path = System.IO.Path;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    // Explicit emulator-only entry. The WebView has a separate profile and blocks all network.
    private async Task RunQrChecks(string mode)
    {
        var directory = Path.Combine(MobileData.Root(this), "synthetic-qr-test");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidDataException(label);
            checks.Add(label);
        }
        void Report(string outcome) => File.WriteAllText(Path.Combine(directory, "qr-checks.json"), JsonSerializer.Serialize(new
        {
            schema = 1, outcome, mode, checks = checks.Count, results = checks,
            officialNetworkAccessed = false, productionDataAccessed = false,
            realAccountAcceptance = "pending", savedImage = savedLoginQr?.ToString()
        }));
        try
        {
            if (mode == "orphan-restart")
            {
                var address = File.ReadAllText(Path.Combine(directory, "orphan-uri.txt"));
                using var uri = Android.Net.Uri.Parse(address);
                Check(uri is not null && !QrTestImageExists(uri), "own QR is cleaned after process termination and restart");
                Check(RememberedQrImages().Length == 0, "cleanup record removed after successful retry");
                Dashboard(); Report("passed"); return;
            }
            byte[] ReadFixture(int id)
            {
                using var input = Resources!.OpenRawResource(id); using var output = new MemoryStream();
                input.CopyTo(output); return output.ToArray();
            }
            var first = ReadFixture(Resource.Raw.synthetic_login_qr_a);
            var second = ReadFixture(Resource.Raw.synthetic_login_qr_b);
            var firstData = "data:image/png;base64," + Convert.ToBase64String(first);
            var secondData = "data:image/png;base64," + Convert.ToBase64String(second);
            CreateLoginPage("chaoxing"); loginLifetime = new();
            var client = new OfflineQrClient(first, LoginPageStarted);
            web!.SetWebViewClient(client);
            async Task Load(string platform, string html)
            {
                loginPlatform = platform;
                var address = platform switch
                {
                    "ucampus" => "https://sso.unipus.cn/sso/login",
                    "zhihuishu" => "https://login.zhihuishu.com/",
                    "yuketang" => "https://www.yuketang.cn/web",
                    "changjiang" => "https://changjiang.yuketang.cn/web",
                    "xuetang" => "https://www.xuetangx.com/",
                    _ => "https://passport2.chaoxing.com/login"
                };
                web!.LoadDataWithBaseURL(address, "<!doctype html><meta name='viewport' content='width=device-width'><body style='margin:16px;font-family:sans-serif'><h2>隔离扫码测试</h2><p>合成二维码，无真实授权</p>" + html,
                    "text/html", "UTF-8", address);
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    await Task.Delay(100);
                    if (web is not null && web.Url == address && await Evaluate("document.readyState === 'complete'") == "true") return;
                }
                throw new TimeoutException("Synthetic QR page did not load.");
            }
            async Task<string?> Capture(string platform, string id)
            {
                var value = await Evaluate(MobilePasswordlessLogin.ImageScript(platform, id));
                return JsonSerializer.Deserialize<string>(value);
            }
            string Image(string attributes) => "<img " + attributes + " src='" + firstData + "'>";
            await Load("chaoxing", Image("id='quickCode'"));
            if (mode == "inspect") { Report("ready"); return; }
            if (mode == "handoff")
            {
                var loginWeb = web; var revision = qrNavigationRevision;
                await SaveOfficialQr("chaoxing", true);
                Check(savedLoginQr is not null && awaitingOfficialApp, "installed app is launched with current QR saved");
                Report("awaiting-app-return");
                for (var attempt = 0; attempt < 150 && (awaitingOfficialApp || !resumed); attempt++) await Task.Delay(200);
                await Task.Delay(1600);
                Check(resumed && !awaitingOfficialApp && web == loginWeb, "return from app retains the original WebView");
                Check(qrNavigationRevision == revision, "return from app does not reload the official login page");
                Check(status.Text?.Contains("尚未确认登录", StringComparison.Ordinal) == true, "unconfirmed official login remains unconfirmed");
                Check(savedLoginQr is not null && QrTestImageExists(savedLoginQr), "QR remains available when confirmation is incomplete");
                CloseLogin("chaoxing");
                Check(RememberedQrImages().Length == 0, "app handoff QR is cleaned on exit");
                Report("passed"); return;
            }
            if (mode == "orphan")
            {
                await SaveOfficialQr("chaoxing", false);
                Check(savedLoginQr is not null && QrTestImageExists(savedLoginQr), "own QR is present before abrupt process termination");
                File.WriteAllText(Path.Combine(directory, "orphan-uri.txt"), savedLoginQr!.ToString());
                Report("ready-for-force-stop"); return;
            }
            var markup = new[]
            {
                ("chaoxing", Image("id='quickCode'")),
                ("ucampus", "<div id='qrcode'>" + Image("") + "</div>"),
                ("zhihuishu", "<div class='app-qrcode-wrapper'><div class='qrcode-box'>" + Image("") + "</div></div>"),
                ("yuketang", "<div id='qrcode-box'>" + Image("class='logma'") + "</div>"),
                ("changjiang", "<div id='qrcode-box'>" + Image("class='logma'") + "</div>"),
                ("xuetang", "<div class='scaveng-img'>" + Image("") + "</div>")
            };
            foreach (var (platform, html) in markup)
            {
                await Load(platform, html);
                Check(MobilePasswordlessLogin.DecodeImage(await Capture(platform, "fixture")) is not null, platform + " official DOM QR is readable");
            }
            await Load("chaoxing", Image("id='quickCode'"));
            Check(await Capture("chaoxing", "original") is not null, "visible data image captured");
            Check(await Evaluate(MobilePasswordlessLogin.VerifyImageScript("chaoxing", "original")) == "true", "unchanged QR passes final verification");
            await Evaluate("document.getElementById('quickCode').src=" + JsonSerializer.Serialize(secondData));
            await Task.Delay(150);
            Check(await Evaluate(MobilePasswordlessLogin.VerifyImageScript("chaoxing", "original")) == "false", "refreshed image cannot reuse the previous capture");
            await Capture("chaoxing", "newer");
            Check(await Evaluate(MobilePasswordlessLogin.VerifyImageScript("chaoxing", "original")) == "false", "superseded capture identifier rejected");
            await Evaluate("document.getElementById('quickCode').style.display='none'");
            Check(await Capture("chaoxing", "hidden") is null, "hidden QR rejected");
            await Load("chaoxing", "<img id='quickCode' src='https://example.com/untrusted.png'>");
            Check(await Capture("chaoxing", "foreign") is null, "cross-origin QR rejected");
            await Load("chaoxing", "<div>No QR on this page</div>");
            Check(await Capture("chaoxing", "missing") is null, "missing QR returns no image");

            await Load("ucampus", "<div id='qrcode'><canvas width='222' height='222'></canvas></div><script>const c=document.querySelector('canvas');const x=c.getContext('2d');x.fillStyle='white';x.fillRect(0,0,222,222);x.fillStyle='black';x.fillRect(20,20,60,60);</script>");
            Check(await Capture("ucampus", "canvas") is not null, "official canvas QR captured");
            await Evaluate("document.querySelector('canvas').getContext('2d').fillRect(100,100,50,50)");
            Check(await Evaluate(MobilePasswordlessLogin.VerifyImageScript("ucampus", "canvas")) == "false", "redrawn canvas invalidates old capture");

            await Load("chaoxing", "<img id='quickCode' src='/createqr?synthetic=1'>");
            var requests = client.ImageRequests;
            Check(await Capture("chaoxing", "remote") is not null, "same-origin displayed image captured");
            Check(client.ImageRequests == requests, "capture does not issue a second QR request");
            client.Image = second;
            await Evaluate("window.syntheticQrSource=document.getElementById('quickCode').src;document.getElementById('quickCode').src='';");
            await Task.Delay(100);
            await Evaluate("document.getElementById('quickCode').src=window.syntheticQrSource;");
            await Task.Delay(400);
            Check(await Evaluate(MobilePasswordlessLogin.VerifyImageScript("chaoxing", "remote")) == "false", "same-URL image with different pixels invalidates capture");

            await Load("chaoxing", Image("id='quickCode'"));
            await SaveOfficialQr("chaoxing", false);
            Check(savedLoginQr is not null && QrTestImageExists(savedLoginQr), "QR saved through scoped MediaStore");
            using (var input = ContentResolver!.OpenInputStream(savedLoginQr!)!)
            using (var bitmap = BitmapFactory.DecodeStream(input))
                Check(bitmap?.Width == 640 && bitmap.Height == 640, "saved QR has legible size and white margin");
            using (var input = ContentResolver!.OpenInputStream(savedLoginQr!)!)
            using (var output = File.Create(Path.Combine(directory, "saved-qr-a.png"))) input.CopyTo(output);
            using var savedAddress = Android.Net.Uri.Parse(savedLoginQr!.ToString())!;
            CloseLogin("chaoxing");
            Check(!QrTestImageExists(savedAddress), "exiting login removes only the created QR image");
            Check(RememberedQrImages().Length == 0, "successful image cleanup removes its record");
            StoreQrImages([savedAddress.ToString()!]);
            CleanupOrphanedLoginQr();
            Check(RememberedQrImages().Length == 0, "already removed media record is cleared after restart cleanup");

            CreateLoginPage("chaoxing"); web!.SetWebViewClient(client);
            await Load("chaoxing", Image("id='quickCode'").Replace(firstData, secondData, StringComparison.Ordinal));
            await SaveOfficialQr("chaoxing", true);
            Check(savedLoginQr is not null && !awaitingOfficialApp && status.Text?.Contains("未找到学习通", StringComparison.Ordinal) == true,
                "missing app leaves QR available and shows fallback");
            using (var input = ContentResolver!.OpenInputStream(savedLoginQr!)!)
            using (var output = File.Create(Path.Combine(directory, "saved-qr-b.png"))) input.CopyTo(output);
            DeleteSavedLoginQr();
            pendingQrSave = (web!, "chaoxing", qrNavigationRevision, false);
            OnRequestPermissionsResult(QrStoragePermission, [Android.Manifest.Permission.WriteExternalStorage], [Android.Content.PM.Permission.Denied]);
            Check(savedLoginQr is null && pendingQrSave is null && status.Text?.Contains("未允许", StringComparison.Ordinal) == true,
                "denied legacy storage permission never saves an image");
            pendingQrSave = (web!, "chaoxing", qrNavigationRevision - 1, false);
            OnRequestPermissionsResult(QrStoragePermission, [Android.Manifest.Permission.WriteExternalStorage], [Android.Content.PM.Permission.Granted]);
            Check(savedLoginQr is null && !preparingQr, "stale permission callback cannot save on a new page");
            var canceled = SaveOfficialQr("chaoxing", false); qrNavigationRevision++;
            await canceled;
            Check(savedLoginQr is null, "page change cancels an in-progress save");
            var closed = SaveOfficialQr("chaoxing", false); CloseLogin("chaoxing");
            await closed;
            Check(savedLoginQr is null && RememberedQrImages().Length == 0, "closing login cancels an in-progress save without gallery residue");
            Report("passed");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "qr-checks.json"), JsonSerializer.Serialize(new
            { outcome = "failed", mode, checks = checks.Count, results = checks, errorType = error.GetType().Name, message = error.Message,
                officialNetworkAccessed = false, productionDataAccessed = false }));
        }
    }

    private bool QrTestImageExists(Android.Net.Uri uri)
    {
        using var cursor = ContentResolver!.Query(uri, ["_id"], null, null, null);
        return cursor?.MoveToFirst() == true;
    }

    private sealed class OfflineQrClient(byte[] image, Action started) : WebViewClient
    {
        public byte[] Image { get; set; } = image;
        public int ImageRequests { get; private set; }
        public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon) { started(); base.OnPageStarted(view, url, favicon); }
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => true;
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url?.Path == "/createqr")
            {
                ImageRequests++;
                return new("image/png", null, 200, "OK", new Dictionary<string, string> { ["Cache-Control"] = "no-store" }, new MemoryStream(Image));
            }
            return new("text/plain", "UTF-8", new MemoryStream([]));
        }
    }
}
