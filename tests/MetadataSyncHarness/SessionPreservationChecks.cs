using System.IO;
using AccessProbe;

namespace MetadataSyncHarness;

internal static partial class Program
{
    private static async Task VerifyCourseSessionPreservationAsync()
    {
        foreach (var platform in new[] { MetadataPlatform.Ucampus, MetadataPlatform.Zhihuishu })
        {
            var service = services[platform.Id]; var fixture = fixtures[platform.Id];
            var archive = new MetadataSessionStore(platform, Path.Combine(runRoot, "subsystem-check.bin"), store);
            var count = store.LoadTasks().Count;
            fixture.RejectCourseSession = true;
            try
            {
                await service.StartAsync(true);
                var summary = store.LoadMetadataSettings(platform.Id).LastSummary!;
                Check(!summary.LoginRequired && summary.AccountVerified && summary.Problems.Count > 0
                    && store.LoadMetadataSettings(platform.Id).LastSyncOutcome == "partial"
                    && store.LoadPlatformSessionInfo(platform.Id).State == "ready" && archive.Load(DateTimeOffset.UtcNow).Count > 0,
                    "course service expiry preserves verified account and encrypted login");
                Check(store.LoadTasks().Count == count && summary.Problems.Any(problem => problem.Message.Contains("课程服务", StringComparison.Ordinal)),
                    "unavailable course progress remains partial and task history stays intact");
            }
            finally { fixture.RejectCourseSession = false; }
            fixture.RejectCourseOnce = true;
            await service.StartAsync(true);
            Check(store.LoadMetadataSettings(platform.Id).LastSummary is { LoginRequired: false, AccountVerified: true, FullTaskListsCovered: true },
                "profile renewal and one retry recover a transient course service session");
            var manager = browsers[platform.Id].CoreWebView2.CookieManager;
            var auxiliary = manager.CreateCookie(platform.Id == "ucampus" ? "hostUrl" : "acw_tc", "synthetic-auxiliary-renewed",
                platform.Id == "ucampus" ? ".unipus.cn" : "onlineservice-api.zhihuishu.com", "/");
            auxiliary.IsSecure = true; auxiliary.Expires = DateTime.UtcNow.AddDays(1); manager.AddOrUpdateCookie(auxiliary);
            await Task.Delay(80); service.ConnectionClosed();
            await WaitAsync(() => archive.Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Value == "synthetic-auxiliary-renewed"));
            Check(store.LoadPlatformSessionInfo(platform.Id).State == "ready", "closing connection persists current login immediately");
            await service.ClearSessionAsync();
            fixture.SetCookies(browsers[platform.Id]); await Task.Delay(80);
            service.ConnectionClosed();
            await WaitAsync(() => archive.Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Name == fixture.AuthName));
            Check(store.LoadPlatformSessionInfo(platform.Id).State == "saved", "closing freshly completed login preserves unverified credentials before account timer");
            await service.StartAsync(true);
            Check(store.LoadPlatformSessionInfo(platform.Id).State == "ready", "official account check confirms credentials saved on connection close");
        }
    }
}
