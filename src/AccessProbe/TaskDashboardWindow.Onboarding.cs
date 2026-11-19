using System.Windows;
using System.Windows.Controls;

namespace AccessProbe;

public partial class TaskDashboardWindow
{
    private static readonly PlatformSite[] DesktopAccessPlatforms = PlatformCatalog.Sites
        .Where(site => CourseRecordingRules.SupportsDesktopAutomatic(site.Id)).ToArray();
    private readonly Dictionary<string, PlatformAccessPresentation> platformAccess = new(StringComparer.Ordinal);
    private PlatformAccessCenterPage? accessCenter;
    private bool onboardingDismissed;

    private void UpdateOnboardingSummary()
    {
        accessCenter?.Update(platformAccess);
        if (onboardingDismissed || platformAccess.Count == 0) return;

        var attention = DesktopAccessPlatforms.FirstOrDefault(site => platformAccess.GetValueOrDefault(site.Id)?.NeedsAttention == true);
        var running = DesktopAccessPlatforms.FirstOrDefault(site => platformAccess.GetValueOrDefault(site.Id)?.State == PlatformAccessState.Syncing);
        var latest = platformAccess.Values.Where(access => access.State == PlatformAccessState.Synced)
            .MaxBy(access => access.LastSyncedAtUtc);
        var missing = platformAccess.Values.Count(access => access.State == PlatformAccessState.NotConnected);
        if (attention is not null)
        {
            var status = platformAccess[attention.Id];
            ShowOnboardingSummary($"{status.PlatformName} · {status.StatusTitle}", status.Description, "查看需要处理的平台", attention.Id);
        }
        else if (running is not null)
        {
            var status = platformAccess[running.Id];
            ShowOnboardingSummary($"{status.PlatformName} · {status.StatusTitle}", status.Description, "查看进度", running.Id);
        }
        else if (latest is not null)
        {
            var description = $"最近同步 {latest.LastSyncedAtUtc?.ToLocalTime():MM-dd HH:mm}，结果已汇入首页。";
            if (missing > 0) description += $"另有 {missing} 个平台可接入。";
            ShowOnboardingSummary($"{latest.PlatformName} · {latest.StatusTitle}", description, "查看平台");
        }
        else if (missing > 0)
            ShowOnboardingSummary("接入课程平台", "连接后，作业、考试和截止时间会自动汇总到这里。", "开始接入");
        else
            ShowOnboardingSummary("平台已连接", "可查看各平台的课程和最近同步结果。", "查看平台");
    }

    private void ShowOnboardingSummary(string title, string description, string action, string? platformId = null)
    {
        OnboardingTitleText.Text = title;
        OnboardingDescriptionText.Text = description;
        OnboardingActionButton.Content = action;
        OnboardingActionButton.Tag = platformId;
        OnboardingBanner.Visibility = Visibility.Visible;
    }

    private void DismissOnboardingSummary(object sender, RoutedEventArgs args)
    {
        onboardingDismissed = true;
        OnboardingBanner.Visibility = Visibility.Collapsed;
    }

    private void OpenOnboardingAction(object sender, RoutedEventArgs args)
    {
        if (OnboardingActionButton.Tag is string id) OpenAccessPlatform(id);
        else ShowAccessCenter();
    }

    private void OpenAccessCenter(object sender, RoutedEventArgs args) => ShowAccessCenter();

    private void ShowAccessCenter()
    {
        if (permitClose || workspaceExiting) return;
        if (accessCenter is null)
        {
            accessCenter = new(DesktopAccessPlatforms, OpenAccessPlatform);
            accessCenter.Closed += (_, _) => accessCenter = null;
        }
        foreach (var site in DesktopAccessPlatforms) UpdatePlatformCard(site.Id);
        accessCenter.Update(platformAccess);
        ShowWorkspacePage("access-center", accessCenter);
    }

    private void OpenAccessPlatform(string id)
    {
        if (permitClose || workspaceExiting || !platformAccess.TryGetValue(id, out var platform)) return;
        if (platform.State is PlatformAccessState.NotConnected or PlatformAccessState.NeedsRetry) ConnectPlatform(id);
        else ShowPlatformManager(id);
    }

    private string EmptyAccessTitle()
    {
        if (platformAccess.Values.Any(access => access.State == PlatformAccessState.Syncing)) return "正在同步任务";
        if (platformAccess.Values.Any(access => access.NeedsAttention)) return "平台接入需要处理";
        return platformAccess.Values.Any(access => access.State != PlatformAccessState.NotConnected) ? "暂时没有任务" : "还没有接入课程平台";
    }

    private static string EmptyAccessDescription(string title) => title switch
    {
        "正在同步任务" => "读取完成后，任务会出现在这里。",
        "平台接入需要处理" => "在“查看平台”中打开需要处理的平台，继续登录或同步。",
        "还没有接入课程平台" => "通过“查看平台”连接你正在使用的课程平台。",
        _ => "已接入的平台暂时没有记录中的任务。可查看同步结果或新增任务。"
    };

    internal static string AccessStatusBrush(PlatformAccessState state) => state switch
    {
        PlatformAccessState.Syncing or PlatformAccessState.PendingRefresh or PlatformAccessState.CoursesAvailable => "PrimaryBrush",
        PlatformAccessState.NeedsRetry => "WarningBrush",
        PlatformAccessState.Synced => "SuccessBrush",
        _ => "MutedBrush"
    };
}
