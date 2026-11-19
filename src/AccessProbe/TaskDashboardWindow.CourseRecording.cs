using System.Windows;
using System.Windows.Controls;

namespace AccessProbe;

public partial class TaskDashboardWindow
{
    private int retainedCourseTaskCount;
    private IReadOnlyDictionary<string, int> retainedCourseTasksByPlatform = new Dictionary<string, int>();

    private void ChooseRecordingCourses(object sender, RoutedEventArgs args)
    {
        var platformId = (sender as FrameworkElement)?.Tag as string;
        platformId ??= PlatformFilter.SelectedIndex > 0 ? PlatformCatalog.Sites[PlatformFilter.SelectedIndex - 1].Id : "chaoxing";
        ShowPlatformManager(platformId);
    }

    private void ShowCourseSelection(string platformId)
    {
        ShowPlatformManager(platformId);
    }

    private Task RefreshRecordingCourseCatalogAsync(string platformId) => platformId switch
    {
        "chaoxing" when sync is not null => sync.RefreshCourseCatalogAsync(),
        "mooc" when moocSync is not null => moocSync.RefreshCourseCatalogAsync(),
        "xuetang" when xuetangSync is not null => xuetangSync.RefreshCourseCatalogAsync(),
        _ when metadataServices.TryGetValue(platformId, out var metadataService) => metadataService.RefreshCourseCatalogAsync(),
        _ when rainServices.TryGetValue(platformId, out var service) => service.RefreshCourseCatalogAsync(),
        _ => Task.CompletedTask
    };

    private async Task ApplyRecordingCourseSelectionAsync(string platformId)
    {
        var origin = activeRoute;
        var originRevision = workspaceNavigationRevision;
        var startedAt = DateTimeOffset.UtcNow;
        var flow = connectionFlow is { } currentFlow && currentFlow.PlatformId == platformId
            && origin?.Key == "platform:" + platformId ? currentFlow : null;
        if (flow is not null) flow.PendingSelectionReport = null;
        RefreshTasks();
        ShowSuccess(PlatformCatalog.Name(platformId) + "记录课程已保存，未选课程停止采集和提醒。");
        try
        {
            await SynchronizeRecordingPlatformAsync(platformId);
            if (!permitClose && !workspaceExiting && workspaceNavigationRevision == originRevision
                && ReferenceEquals(activeRoute, origin) && origin?.Key == "platform:" + platformId)
            {
                var report = store.LoadConnectionReport(platformId);
                var currentReport = report is not null && report.CheckedAtUtc >= startedAt && IsCurrentConnectionReport(report);
                var connecting = flow is not null && ReferenceEquals(connectionFlow, flow);
                if (!CourseRecordingRules.SupportsDesktopAutomatic(platformId)
                    || currentReport && CanReturnAfterConnection(report!, requirePtaHome: connecting)
                        && (connecting || report!.TaskListsCovered))
                {
                    ShowTasks(1);
                    if (report is null) ShowSuccess(PlatformCatalog.Name(platformId) + "记录课程已保存。");
                    else ShowConnectionCompletion(report);
                }
                else if (connecting && currentReport && CanReturnAfterConnection(report!, requirePtaHome: false))
                {
                    flow!.PendingSelectionReport = report;
                    flow.SelectionNavigationRevision = originRevision;
                }
            }
        }
        catch { if (!permitClose) ShowError("COURSE001", "课程选择已保存，后续同步将按新选择执行；本轮刷新未完成。"); }
    }

    private Task SynchronizeRecordingPlatformAsync(string platformId) => platformId switch
    {
        "chaoxing" when sync is not null => sync.RecordingCoursesChangedAsync(),
        "mooc" when moocSync is not null => moocSync.RecordingCoursesChangedAsync(),
        "xuetang" when xuetangSync is not null => xuetangSync.RecordingCoursesChangedAsync(),
        _ when metadataServices.TryGetValue(platformId, out var metadataService) => metadataService.RecordingCoursesChangedAsync(),
        _ when rainServices.TryGetValue(platformId, out var service) => service.RecordingCoursesChangedAsync(),
        _ => Task.CompletedTask
    };
}
