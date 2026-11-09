using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AccessProbe;
using Microsoft.Web.WebView2.Wpf;

namespace MetadataSyncHarness;

internal static partial class Program
{
    private static async Task VerifyZhihuishuCacheRetryAsync(MetadataConnectionWindow connection, WebView2 browser,
        MetadataSyncService service, Fixtures fixture)
    {
        lastCheck = "local failed login JavaScript response";
        var status = (TextBlock)typeof(MetadataConnectionWindow).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        await WaitAsync(() => service.LoginDiagnostics.Snapshot().Events.Any(item => item.Result == MetadataLoginResult.ScriptUnavailable));
        Check(status.Text.Contains("官方登录脚本未能加载", StringComparison.Ordinal)
            && service.LoginDiagnostics.Snapshot().Events.Any(item => item is { Stage: MetadataLoginStage.Resource, Result: MetadataLoginResult.ScriptUnavailable, Status: 200 }),
            "200 HTML in place of login script displays actionable failure");
        Check(fixture.BrowserDocumentRoutes.All(route => !route.Contains("synthetic-login.js", StringComparison.Ordinal)),
            "failed script is verified as resource rather than document navigation");
        await browser.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('synthetic-cache-retained','synthetic-kept');");
        var beforeCookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync("");
        var cookieValues = beforeCookies.ToDictionary(cookie => cookie.Name + "|" + cookie.Domain + "|" + cookie.Path, cookie => cookie.Value);
        Check(cookieValues.Count > 0, "cache retry starts with an existing synthetic login session");
        var failedText = status.Text;
        await service.StartAsync(true);
        Check(status.Text == failedText && service.LoginDiagnostics.Describe().Contains("登录脚本未能加载", StringComparison.Ordinal),
            "background session result cannot erase current login script failure");
        var scriptsBefore = fixture.LoginScriptRequests; var refreshesBefore = service.LoginDiagnostics.Snapshot().Events
            .Count(item => item is { Stage: MetadataLoginStage.CacheRefresh, Result: MetadataLoginResult.Ready });
        var navigationReadyBefore = service.LoginDiagnostics.Snapshot().Events
            .Count(item => item is { Stage: MetadataLoginStage.MainNavigation, Result: MetadataLoginResult.Ready });
        fixture.FailLoginScript = false;
        Visuals<Button>(connection).Single(button => (string?)button.Content == "打开官方登录").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        lastCheck = "replacement login script and successful retry navigation";
        await WaitAsync(() => fixture.LoginScriptRequests > scriptsBefore && fixture.HealthyLoginScriptRequests > 0
            && service.LoginDiagnostics.Snapshot().Events.Count(item => item is { Stage: MetadataLoginStage.MainNavigation, Result: MetadataLoginResult.Ready }) > navigationReadyBefore
            && service.LoginDiagnostics.Snapshot().PageFailure is null);
        Check(await browser.CoreWebView2.ExecuteScriptAsync("document.body.getAttribute('data-synthetic-script') === 'ready'") == "true",
            "cache retry actually executes healthy replacement script");
        Check(await browser.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('synthetic-cache-retained') === 'synthetic-kept'") == "true",
            "disk cache refresh preserves login origin localStorage");
        var afterCookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync("");
        Check(afterCookies.Count == beforeCookies.Count && afterCookies.All(cookie => cookieValues.TryGetValue(cookie.Name + "|" + cookie.Domain + "|" + cookie.Path, out var value)
            && value == cookie.Value), "disk cache retry preserves every synthetic cookie");
        Check(service.LoginDiagnostics.Snapshot().Events.Count(item => item is { Stage: MetadataLoginStage.CacheRefresh, Result: MetadataLoginResult.Ready }) > refreshesBefore
            && !service.LoginDiagnostics.Describe().Contains("登录脚本未能加载", StringComparison.Ordinal), "successful retry starts a fresh visible diagnostic attempt");
        var reportText = File.ReadAllText(service.LoginDiagnostics.Path);
        Check(!reportText.Contains("synthetic-cache-retained", StringComparison.Ordinal) && !reportText.Contains("synthetic-kept", StringComparison.Ordinal)
            && !reportText.Contains("synthetic-login.js", StringComparison.Ordinal) && !reportText.Contains("synthetic-session", StringComparison.Ordinal),
            "login cache regression diagnostics contain classifications without storage cookies or resource URL");
    }
}
