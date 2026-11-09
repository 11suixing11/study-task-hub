using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PlatformConnectionTestData;

internal static class ConnectionPopupChecks
{
    public static async Task VerifyAsync(CoreWebView2 core, string host, Action<bool, string> check)
    {
        var entry = "https://" + host + "/__synthetic__/popup-entry";
        var target = "https://" + host + "/__synthetic__/popup-return";
        var rejected = "https://evil.example/__synthetic__/popup-return";
        var returnReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var popupHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectedHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entryRequests = 0;
        var returnRequests = 0;
        void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            if (args.Request.Uri == entry)
            {
                entryRequests++;
                args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 302, "Found",
                    "Location: " + target + "\r\nCache-Control: no-store\r\n");
            }
            else if (args.Request.Uri == target)
            {
                returnRequests++;
                const string html = "<!doctype html><meta charset='utf-8'><p id='synthetic-popup-return'>本地登录弹窗回跳验收</p>";
                args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            }
        }
        void DomReady(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
        { if (core.Source == target) returnReady.TrySetResult(true); }
        void PopupRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            if (args.Uri == entry) popupHandled.TrySetResult(args.Handled);
            if (args.Uri == rejected) rejectedHandled.TrySetResult(args.Handled);
        }
        core.AddWebResourceRequestedFilter(entry, CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.All);
        core.AddWebResourceRequestedFilter(target, CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += ResourceRequested;
        core.DOMContentLoaded += DomReady;
        core.NewWindowRequested += PopupRequested;
        try
        {
            await core.ExecuteScriptAsync("window.open(" + JsonSerializer.Serialize(entry) + ", '_blank')");
            check(await popupHandled.Task.WaitAsync(TimeSpan.FromSeconds(10)), "official login popup stays in the existing window");
            await returnReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(entryRequests == 1 && returnRequests == 1, "official login popup follows the local HTTP return once");
            check(await core.ExecuteScriptAsync("location.href") == JsonSerializer.Serialize(target)
                && await core.ExecuteScriptAsync("!!document.getElementById('synthetic-popup-return')") == "true",
                "official login popup renders its return document");
            await core.ExecuteScriptAsync("window.open(" + JsonSerializer.Serialize(rejected) + ", '_blank')");
            check(await rejectedHandled.Task.WaitAsync(TimeSpan.FromSeconds(10)), "unrelated login popup cannot create another window");
            await Task.Delay(100);
            check(await core.ExecuteScriptAsync("location.href") == JsonSerializer.Serialize(target),
                "unrelated login popup preserves the official return document");
        }
        finally
        {
            core.WebResourceRequested -= ResourceRequested;
            core.DOMContentLoaded -= DomReady;
            core.NewWindowRequested -= PopupRequested;
            core.RemoveWebResourceRequestedFilter(entry, CoreWebView2WebResourceContext.Document);
            core.RemoveWebResourceRequestedFilter(target, CoreWebView2WebResourceContext.Document);
        }
    }
}
