using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using F = PlatformMetadataTestData.PtaFixtures;

namespace PtaSyncHarness;

internal static class BrowserFixtures
{
    public static void BlockNetwork(WebView2 browser, bool includeCaptcha = false)
    {
        browser.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) return;
            var core = browser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, request) =>
            {
                var html = "<!doctype html><html lang='zh-CN'><meta charset='utf-8'><body>PTA 合成官方登录页面</body></html>";
                if (includeCaptcha && Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var address))
                {
                    if (address.IdnHost == "pintia.cn")
                        html = "<!doctype html><html lang='zh-CN'><meta charset='utf-8'><body>PTA 合成官方登录页面"
                            + "<script>window.addEventListener('message', event => { if (event.origin === 'https://turing.captcha.qcloud.com'"
                            + " && event.data === 'synthetic-pta-captcha-ready') window.syntheticPtaCaptchaReady = true; });</script>"
                            + "<iframe src='https://turing.captcha.qcloud.com/template/drag_ele.html?aid=synthetic-captcha'></iframe></body></html>";
                    else if (address.IdnHost == "turing.captcha.qcloud.com")
                        html = "<!doctype html><html lang='zh-CN'><meta charset='utf-8'><body>合成验证码框架"
                            + "<script>parent.postMessage('synthetic-pta-captcha-ready', 'https://pintia.cn');</script></body></html>";
                }
                request.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            };
        };
    }
    public static void SetCookie(WebView2 browser, string value)
    {
        var manager = browser.CoreWebView2.CookieManager; var cookie = F.Cookie(value);
        var live = manager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        live.IsHttpOnly = true; live.IsSecure = true; live.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        manager.AddOrUpdateCookie(live);
    }
}
