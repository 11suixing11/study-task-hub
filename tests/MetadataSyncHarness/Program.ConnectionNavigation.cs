using System.Text.Json;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MetadataSyncHarness;

internal static partial class Program
{
    private static async Task VerifyZhihuishuReturnNavigationAsync(WebView2 browser, Fixtures fixture)
    {
        lastCheck = "SSO frame and popup returns through the actual connection window";
        var core = browser.CoreWebView2;
        var frameTarget = "https://onlinestuh5.zhihuishu.com/?synthetic=sso-frame";
        var frameReturn = "https://cloudapi.polymas.com/oauth/login/checkLogin?url=" + Uri.EscapeDataString(frameTarget);
        var oldFrame = fixture.StudentFrame;
        await core.ExecuteScriptAsync("(() => { const frame = document.createElement('iframe'); frame.src = "
            + JsonSerializer.Serialize(frameReturn) + "; document.body.appendChild(frame); })()");
        await WaitAsync(() => fixture.StudentFrame is not null && fixture.StudentFrame != oldFrame);
        Check(await fixture.StudentFrame!.ExecuteScriptAsync("location.href") == JsonSerializer.Serialize(frameTarget)
            && fixture.BrowserDocumentRoutes.Contains("cloudapi.polymas.com/oauth/login/checkLogin"),
            "official SSO verification frame loads the student return document");

        var rejected = "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect=" + Uri.EscapeDataString("https://evil.example/");
        var source = core.Source;
        var unrelatedDocuments = fixture.BrowserDocumentRoutes.Count(route => route.StartsWith("evil.example/", StringComparison.Ordinal));
        var mainBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void MainStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        { if (args.Uri == rejected) mainBlocked.TrySetResult(args.Cancel); }
        void FrameStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        { if (args.Uri == rejected) frameBlocked.TrySetResult(args.Cancel); }
        core.NavigationStarting += MainStarting; core.FrameNavigationStarting += FrameStarting;
        try
        {
            core.Navigate(rejected);
            Check(await mainBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10)), "SSO main navigation with an unrelated return is cancelled");
            await core.ExecuteScriptAsync("(() => { const frame = document.createElement('iframe'); frame.src = "
                + JsonSerializer.Serialize(rejected) + "; document.body.appendChild(frame); })()");
            Check(await frameBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10)), "SSO frame navigation with an unrelated return is cancelled");
            await Task.Delay(100);
            Check(await core.ExecuteScriptAsync("location.href") == JsonSerializer.Serialize(source),
                "rejected SSO returns preserve the current document");
            Check(fixture.BrowserDocumentRoutes.Count(route => route.StartsWith("evil.example/", StringComparison.Ordinal)) == unrelatedDocuments,
                "rejected SSO returns never load an unrelated return document");
        }
        finally { core.NavigationStarting -= MainStarting; core.FrameNavigationStarting -= FrameStarting; }

        var restoredHome = Fixtures.ZhihuishuStudentHome + "?synthetic=after-cancel";
        var restored = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Restored(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
        { if (core.Source == restoredHome) restored.TrySetResult(true); }
        core.DOMContentLoaded += Restored;
        try
        {
            core.Stop(); core.Navigate(restoredHome);
            Check(await restored.Task.WaitAsync(TimeSpan.FromSeconds(10)), "official student page can reload after a rejected return");
        }
        finally { core.DOMContentLoaded -= Restored; }

        var popupTarget = Fixtures.ZhihuishuStudentHome + "?synthetic=sso-popup";
        var popupReturn = "https://cloudapi.polymas.com/oauth/login/ssoLogin?redirect=" + Uri.EscapeDataString(popupTarget);
        var popupHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectedPopupHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void PopupRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            if (args.Uri == popupReturn) popupHandled.TrySetResult(args.Handled);
            if (args.Uri == rejected) rejectedPopupHandled.TrySetResult(args.Handled);
        }
        core.NewWindowRequested += PopupRequested;
        try
        {
            await core.ExecuteScriptAsync("window.open(" + JsonSerializer.Serialize(popupReturn) + ", '_blank')");
            Check(await popupHandled.Task.WaitAsync(TimeSpan.FromSeconds(10)), "official SSO popup is handled in the existing login window");
            await WaitAsync(() => core.Source == popupTarget);
            Check(await core.ExecuteScriptAsync("location.href") == JsonSerializer.Serialize(popupTarget),
                "SSO popup loads the student return document");
            Check(core.Environment.UserDataFolder == browsers["zhihuishu"].CoreWebView2.Environment.UserDataFolder,
                "SSO popup return preserves the platform session environment");
            var ssoRequests = fixture.BrowserDocumentRoutes.Count(route => route == "cloudapi.polymas.com/oauth/login/ssoLogin");
            await core.ExecuteScriptAsync("window.open(" + JsonSerializer.Serialize(rejected) + ", '_blank')");
            Check(await rejectedPopupHandled.Task.WaitAsync(TimeSpan.FromSeconds(10)), "unrelated SSO popup cannot create another browser window");
            await Task.Delay(100);
            Check(await core.ExecuteScriptAsync("location.href") == JsonSerializer.Serialize(popupTarget)
                && fixture.BrowserDocumentRoutes.Count(route => route == "cloudapi.polymas.com/oauth/login/ssoLogin") == ssoRequests,
                "unrelated SSO popup neither navigates nor fetches a document");
        }
        finally { core.NewWindowRequested -= PopupRequested; }
        Check(browsers["zhihuishu"].CoreWebView2.Source == "about:blank", "login return never navigates the background metadata browser");
    }
}
