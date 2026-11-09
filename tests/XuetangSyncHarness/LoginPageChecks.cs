using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AccessProbe;
using Microsoft.Web.WebView2.Wpf;

namespace XuetangSyncHarness;

internal static partial class Program
{
    private static async Task VerifyLoginPageAsync(XuetangConnectionWindow window, WebView2 loginBrowser)
    {
        var status = (TextBlock)typeof(XuetangConnectionWindow).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        lastCheck = "login application script HTML fallback";
        await WaitAsync(() => service.LoginDiagnostics.Snapshot().PageFailure?.Result == XuetangLoginResult.ScriptUnavailable);
        Check(status.Text.Contains("官方登录脚本未能加载", StringComparison.Ordinal)
            && service.LoginDiagnostics.Snapshot().PageFailure?.Status == 200, "HTML returned for login script displays actionable failure");
        await loginBrowser.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('synthetic-cache-retained','synthetic-kept')");
        var cookies = (await loginBrowser.CoreWebView2.CookieManager.GetCookiesAsync("")).ToDictionary(
            cookie => cookie.Name + "|" + cookie.Domain + "|" + cookie.Path, cookie => cookie.Value);
        var storedTasks = store.LoadTasks().Select(task => (task.Id, task.SourceKey, task.Title, task.CourseName, task.State)).ToArray();
        var failureText = status.Text; await service.StartAsync(true);
        Check(status.Text == failureText, "background login polling cannot overwrite current page failure");

        fixture.FailLoginScript = false;
        await RetryLoginPageAsync(window, () => service.LoginDiagnostics.Snapshot().PageFailure is null
            && fixture.HealthyLoginScriptRequests > 0 && service.LoginDiagnostics.Snapshot().Events.LastOrDefault()?.Result == XuetangLoginResult.Ready);
        Check(await loginBrowser.CoreWebView2.ExecuteScriptAsync("document.documentElement.getAttribute('data-synthetic-script') === 'ready'") == "true",
            "cache retry loads and executes healthy replacement login script");
        Check(await loginBrowser.CoreWebView2.ExecuteScriptAsync("location.pathname === '/' && !!document.getElementById('synthetic-open-login')") == "true",
            "desktop login navigates official home with visible login control");
        await loginBrowser.CoreWebView2.ExecuteScriptAsync("document.getElementById('synthetic-open-login').click()");
        Check(await loginBrowser.CoreWebView2.ExecuteScriptAsync("document.getElementById('synthetic-login-form').getBoundingClientRect().height > 0") == "true",
            "local official login control reveals its form");
        Check(await loginBrowser.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('synthetic-cache-retained') === 'synthetic-kept'") == "true",
            "login page cache refresh retains origin storage");
        var after = await loginBrowser.CoreWebView2.CookieManager.GetCookiesAsync("");
        Check(cookies.Count > 0 && after.Count == cookies.Count && after.All(cookie => cookies.GetValueOrDefault(cookie.Name + "|" + cookie.Domain + "|" + cookie.Path) == cookie.Value),
            "login page refresh retains every existing synthetic cookie");

        fixture.FailLoginStyle = true;
        await RetryLoginPageAsync(window, () => service.LoginDiagnostics.Snapshot().PageFailure?.Result == XuetangLoginResult.StyleUnavailable);
        Check(status.Text.Contains("官方登录样式未能加载", StringComparison.Ordinal), "failed login stylesheet explains incomplete layout");
        failureText = status.Text; await service.StartAsync(true); Check(status.Text == failureText, "background status preserves stylesheet failure");
        fixture.FailLoginStyle = false;
        await RetryLoginPageAsync(window, () => service.LoginDiagnostics.Snapshot().PageFailure is null
            && service.LoginDiagnostics.Snapshot().Events.LastOrDefault()?.Result == XuetangLoginResult.Ready);

        var timer = (DispatcherTimer)typeof(XuetangConnectionWindow).GetField("loadTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var interval = timer.Interval; timer.Interval = TimeSpan.FromMilliseconds(750); fixture.EmptyLoginDocument = true;
        await RetryLoginPageAsync(window, () => service.LoginDiagnostics.Snapshot().PageFailure?.Result == XuetangLoginResult.BlankPage);
        Check(status.Text.Contains("官方页面没有显示内容", StringComparison.Ordinal), "empty loaded application shell is not reported ready");
        failureText = status.Text; await service.StartAsync(true); Check(status.Text == failureText, "login polling cannot hide an empty application shell");
        fixture.EmptyLoginDocument = false; timer.Interval = interval;
        await RetryLoginPageAsync(window, () => service.LoginDiagnostics.Snapshot().PageFailure is null
            && service.LoginDiagnostics.Snapshot().Events.LastOrDefault()?.Result == XuetangLoginResult.Ready);

        var verifiedFrames = new[] {
            "https://newassets.hcaptcha.com/captcha/v1/syntheticversion/static/hcaptcha.html#frame=challenge",
            "https://turing.captcha.qcloud.com/template/drag_ele.html?synthetic=1"
        };
        foreach (var address in verifiedFrames)
        {
            var expectedOrigin = new Uri(address).GetLeftPart(UriPartial.Authority);
            await loginBrowser.CoreWebView2.ExecuteScriptAsync("(() => { document.body.removeAttribute('data-synthetic-captcha');"
                + "const expected=" + JsonSerializer.Serialize(expectedOrigin) + "; const listener=e=>{if(e.origin===expected && e.data==='synthetic-captcha-ready')"
                + "{document.body.setAttribute('data-synthetic-captcha',expected);window.removeEventListener('message',listener);}};window.addEventListener('message',listener);"
                + "const frame=document.createElement('iframe');frame.src=" + JsonSerializer.Serialize(address) + ";document.body.appendChild(frame); })()");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (await loginBrowser.CoreWebView2.ExecuteScriptAsync("document.body.getAttribute('data-synthetic-captcha') === " + JsonSerializer.Serialize(expectedOrigin)) != "true")
            { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("local verifier iframe document execution"); await Task.Delay(25); }
            Check(service.LoginDiagnostics.Snapshot().PageFailure is null, "confirmed verifier iframe executes under official login document");
        }
        loginBrowser.CoreWebView2.Navigate(verifiedFrames[0]); await Task.Delay(100);
        Check(loginBrowser.CoreWebView2.Source == XuetangPlatform.Login.AbsoluteUri
            && service.LoginDiagnostics.Snapshot().PageFailure is { Stage: XuetangLoginStage.MainNavigation, Result: XuetangLoginResult.Blocked },
            "verifier cannot replace the main official page");
        await RetryLoginPageAsync(window, () => service.LoginDiagnostics.Snapshot().PageFailure is null
            && service.LoginDiagnostics.Snapshot().Events.LastOrDefault()?.Result == XuetangLoginResult.Ready);
        await loginBrowser.CoreWebView2.ExecuteScriptAsync("const frame=document.createElement('iframe');frame.src='https://evil.example/__synthetic__/captcha';document.body.appendChild(frame)");
        await WaitAsync(() => service.LoginDiagnostics.Snapshot().PageFailure?.Stage == XuetangLoginStage.FrameNavigation);
        Check(status.Text.Contains("验证窗口跳转尚未适配", StringComparison.Ordinal), "unrelated verification frame remains blocked and explained");
        await RetryLoginPageAsync(window, () => service.LoginDiagnostics.Snapshot().PageFailure is null
            && service.LoginDiagnostics.Snapshot().Events.LastOrDefault()?.Result == XuetangLoginResult.Ready);
        var report = File.ReadAllText(service.LoginDiagnostics.Path);
        Check(!report.Contains("synthetic-kept", StringComparison.Ordinal) && !report.Contains("synthetic-login", StringComparison.Ordinal)
            && !report.Contains("https://", StringComparison.Ordinal), "desktop login diagnostic persists classifications without credentials or resource URLs");
        Check(store.LoadTasks().Select(task => (task.Id, task.SourceKey, task.Title, task.CourseName, task.State)).SequenceEqual(storedTasks),
            "login rendering checks and explicit sync retain task identities and manual fields");
    }

    private static async Task RetryLoginPageAsync(XuetangConnectionWindow window, Func<bool> ready)
    {
        var documentsBefore = fixture.LoginDocumentRequests;
        var attemptsBefore = service.LoginDiagnostics.Snapshot().Attempts;
        FindVisuals<Button>(window).Single(button => (string?)button.Content == "打开官方登录").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => fixture.LoginDocumentRequests > documentsBefore && service.LoginDiagnostics.Snapshot().Attempts > attemptsBefore && ready());
    }
}
