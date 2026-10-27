using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace AccessProbe;

public partial class MainWindow
{
    private readonly HttpsUpgradeChain mainHttpsUpgrade = new();
    private readonly Dictionary<CoreWebView2Frame, FrameNavigationState> frameNavigationStates = [];
    private int httpsUpgradeCount;
    private string lastHttpsUpgradeSurface = "none";

    private void MainNavigationStarting(CoreWebView2 core, CoreWebView2NavigationStartingEventArgs navigation)
    {
        activeNavigationId = navigation.NavigationId;
        blockedNavigationId = null;
        InvalidateCollection();
        mainHttpsUpgrade.NavigationStarting(navigation.Uri, navigation.IsRedirected);
        if (clearing && navigation.Uri == "about:blank") return;
        if (!clearing && ProbePolicy.IsAllowedNavigation(navigation.Uri))
        {
            navigationOutcome = "loading";
            StatusText.Text = "正在加载官方页面……";
            return;
        }

        navigation.Cancel = true;
        blockedNavigationId = navigation.NavigationId;
        if (!clearing && ProbePolicy.TryGetHttpsNavigation(navigation.Uri, out var upgraded))
        {
            if (mainHttpsUpgrade.TrySchedule(upgraded))
            {
                navigationOutcome = "upgrading_to_https";
                RecordHttpsUpgrade("主页面", "main_page");
                StatusText.Text = "官方链接使用 HTTP，正在改用 HTTPS 打开……";
                var navigationId = navigation.NavigationId;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (closing || clearing || activeNavigationId != navigationId) return;
                    try { core.Navigate(upgraded); }
                    catch
                    {
                        mainHttpsUpgrade.Reset();
                        navigationOutcome = "https_upgrade_failed";
                        StatusText.Text = "HTTPS 页面打开失败，请返回课程页重试。";
                    }
                }));
                return;
            }
            blockedCategory = "https_downgrade_loop";
        }
        else blockedCategory = clearing ? "clearing" : ProbePolicy.NavigationCategory(navigation.Uri);
        navigationOutcome = "blocked_by_client";
        StatusText.Text = blockedCategory == "https_downgrade_loop"
            ? "官方页面反复跳回 HTTP，已停止本次跳转。请返回课程页重试；HTTPS 页面尚未验证。"
            : $"客户端阻止了页面跳转：{blockedCategory}。请保留下方拦截类别以便定位。";
        DiagnosticText.Text = $"最近拦截：主页面 / {blockedCategory}（不记录跳转地址）";
    }

    private void FrameNavigationStarting(CoreWebView2Frame frame, CoreWebView2NavigationStartingEventArgs navigation)
    {
        InvalidateCollection();
        if (!frameNavigationStates.TryGetValue(frame, out var state)) return;
        state.NavigationId = navigation.NavigationId;
        state.Upgrade.NavigationStarting(navigation.Uri, navigation.IsRedirected);
        if (clearing || ProbePolicy.IsAllowedNavigation(navigation.Uri)) return;
        navigation.Cancel = true;
        if (!ProbePolicy.TryGetHttpsNavigation(navigation.Uri, out var upgraded)) return;
        if (!state.Upgrade.TrySchedule(upgraded))
        {
            blockedCategory = "https_downgrade_loop";
            DiagnosticText.Text = "最近拦截：内嵌页面 / https_downgrade_loop；页面反复跳回 HTTP。";
            return;
        }
        RecordHttpsUpgrade("内嵌页面", "frame");
        var navigationId = navigation.NavigationId;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () =>
        {
            if (closing || clearing || !frameNavigationStates.TryGetValue(frame, out var current) || current.NavigationId != navigationId) return;
            try
            {
                if (frame.IsDestroyed() != 0) return;
                await frame.ExecuteScriptAsync("location.replace(" + JsonSerializer.Serialize(upgraded) + ");")
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                if (!closing && !clearing && current.NavigationId == navigationId)
                {
                    current.Upgrade.Reset();
                    DiagnosticText.Text = "内嵌页面改用 HTTPS 未完成。请返回课程页重试；未记录页面地址。";
                }
            }
        }));
    }

    private void NewWindowRequested(CoreWebView2 core, CoreWebView2NewWindowRequestedEventArgs request)
    {
        request.Handled = true;
        if (closing) return;
        if (!clearing && ProbePolicy.IsAllowedNavigation(request.Uri))
        {
            try { core.Navigate(request.Uri); }
            catch { StatusText.Text = "页面打开失败，请返回课程页重试。"; }
            return;
        }
        if (!clearing && ProbePolicy.TryGetHttpsNavigation(request.Uri, out var upgraded))
        {
            mainHttpsUpgrade.Reset();
            if (mainHttpsUpgrade.TrySchedule(upgraded))
            {
                RecordHttpsUpgrade("新窗口", "new_window");
                try { core.Navigate(upgraded); }
                catch
                {
                    mainHttpsUpgrade.Reset();
                    StatusText.Text = "HTTPS 页面打开失败，请返回课程页重试。";
                }
                return;
            }
        }
        blockedCategory = clearing ? "clearing" : ProbePolicy.NavigationCategory(request.Uri);
        DiagnosticText.Text = $"最近拦截：新窗口 / {blockedCategory}；未记录完整地址。";
    }

    private void RecordHttpsUpgrade(string surfaceText, string surface)
    {
        httpsUpgradeCount++;
        lastHttpsUpgradeSurface = surface;
        DiagnosticText.Text = $"官方链接已改用 HTTPS：{surfaceText}（不记录路径或地址参数）";
    }

    private sealed class FrameNavigationState
    {
        public ulong NavigationId { get; set; }
        public HttpsUpgradeChain Upgrade { get; } = new();
    }
}
