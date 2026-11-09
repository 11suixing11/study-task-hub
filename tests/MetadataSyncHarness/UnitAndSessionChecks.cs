using System.IO;
using System.Windows.Controls;
using AccessProbe;
using Data = PlatformMetadataTestData.Fixtures;

namespace MetadataSyncHarness;

internal static partial class Program
{
    private static async Task VerifyOfficialSessionRenewalAsync()
    {
        foreach (var platform in new[] { MetadataPlatform.Ucampus, MetadataPlatform.Zhihuishu })
        {
            var fixture = fixtures[platform.Id]; var service = services[platform.Id]; var manager = browsers[platform.Id].CoreWebView2.CookieManager;
            var ticket = manager.CreateCookie(platform.Id == "ucampus" ? "TGC" : "CASTGC", "synthetic-cas-ticket",
                platform.Id == "ucampus" ? "sso.unipus.cn" : ".zhihuishu.com", platform.Id == "ucampus" ? "/sso" : "/");
            ticket.IsSecure = ticket.IsHttpOnly = true; manager.AddOrUpdateCookie(ticket); await Task.Delay(70);
            fixture.RenewAccountSession = true; fixture.AccountSessionRenewed = false;
            try
            {
                await service.StartAsync(true);
                var summary = store.LoadMetadataSettings(platform.Id).LastSummary!;
                File.WriteAllText(Path.Combine(runRoot, platform.Id + "-renewal-check.json"), System.Text.Json.JsonSerializer.Serialize(new
                    { fixture.AccountSessionRenewed, summary.AccountVerified, summary.LoginRequired, summary.FullTaskListsCovered,
                        state = store.LoadPlatformSessionInfo(platform.Id).State, paths = fixture.RenewalPaths,
                        problems = summary.Problems.Select(problem => new { problem.Code, problem.Message }),
                        recovery = service.SessionRecoveryOutcome, persistence = service.SessionPersistenceOutcome }));
                var archive = new MetadataSessionStore(platform, Path.Combine(runRoot, "renewal-check.bin"), store);
                Check(fixture.AccountSessionRenewed && summary.AccountVerified && !summary.LoginRequired && summary.FullTaskListsCovered
                    && store.LoadPlatformSessionInfo(platform.Id).State == "ready", platform.Id + " official SSO renews account session without reconnecting");
                Check(archive.Load(DateTimeOffset.UtcNow).Any(cookie => cookie.Value == "synthetic-account-renewed")
                    && (await manager.GetCookiesAsync("")).Any(cookie => cookie.Value == "synthetic-account-renewed"),
                    "official renewal writes the exact new cookie into WebView and encrypted repository");
                Check(fixture.RenewalPaths.Count > 0 && summary.Requests <= MetadataSynchronizer.MaximumRequests
                    && browsers[platform.Id].CoreWebView2.Source == "about:blank", "renewal counts requests and preserves background blank browser");
            }
            finally { fixture.RenewAccountSession = false; }
        }
    }
    private static async Task VerifyLearningUnitsDesktopAsync()
    {
        var fixture = fixtures["ucampus"]; var service = services["ucampus"]; fixture.IncludeLearningUnits = true;
        await service.StartAsync(true);
        var summary = store.LoadMetadataSettings("ucampus").LastSummary!;
        Check(summary is { LearningUnits: 3, LearningUnitsWithDeadline: 3, RequiredLearningUnitsIncomplete: 2, LearningUnitListsCovered: true },
            "desktop service imports limited learning units with complete required-progress coverage");
        ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = "U 校园";
        await WaitAsync(() => Table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Count(row => MetadataTaskNavigation.ParseUcampusUnit(row.Task.Link) is not null) == 3);
        var rows = Table.Items.Cast<TaskDashboardWindow.TaskDisplayRow>().Where(row => MetadataTaskNavigation.ParseUcampusUnit(row.Task.Link) is not null).ToArray();
        Check(MetadataTaskNavigation.ParseUcampusUnit(rows[0].Task.Link)!.UnitId == "u2" && rows[2].Task.State == TaskState.Completed,
            "homepage displays urgent incomplete unit before later and completed units");
        Check(rows.Any(row => row.Task.ProgressPercent == 50 && row.Task.PlatformStatusText.Contains("1/2", StringComparison.Ordinal)),
            "homepage retains required progress count and percentage");
        string? opened = null;
        var navigation = new TaskNavigationService(store, service.ResolveTaskAddressAsync, address => opened = address);
        var outcome = await navigation.OpenAsync(rows[0].Task.Id, default);
        Check(outcome.Destination == "learning-unit" && opened is not null && opened.StartsWith("https://ucontent.unipus.cn/_pc_default/pc.html?", StringComparison.Ordinal)
            && !opened.Contains("openId", StringComparison.Ordinal), "desktop unit click checks account and opens exact official unfinished checkpoint");
        var count = store.LoadTasks().Count; fixture.UnknownLearningCatalog = true;
        try { await service.StartAsync(true); } finally { fixture.UnknownLearningCatalog = false; }
        Check(store.LoadMetadataSettings("ucampus") is { LastSyncOutcome: "partial", LastSummary.LearningUnitListsCovered: false, LastSummary.FullTaskListsCovered: true }
            && store.LoadTasks().Count == count, "unknown learning structure keeps history and existing assignment collection partial");
        Click("ShowAllTasksButton"); await service.StartAsync(true);
    }
}
