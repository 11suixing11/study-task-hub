using System.Windows;
using System.Windows.Controls;

namespace AccessProbe;

public partial class TaskDashboardWindow
{
    private CourseSelectionWindow? courseSelection;
    private int retainedCourseTaskCount;
    private IReadOnlyDictionary<string, int> retainedCourseTasksByPlatform = new Dictionary<string, int>();

    private void ChooseRecordingCourses(object sender, RoutedEventArgs args)
    {
        var platformId = (sender as FrameworkElement)?.Tag as string;
        platformId ??= PlatformFilter.SelectedIndex > 0 ? PlatformCatalog.Sites[PlatformFilter.SelectedIndex - 1].Id : "chaoxing";
        ShowCourseSelection(platformId);
    }

    private void ShowCourseSelection(string platformId)
    {
        if (courseSelection is not null) { courseSelection.Activate(); return; }
        courseSelection = new(store, platformId, RefreshRecordingCourseCatalogAsync, ApplyRecordingCourseSelectionAsync,
            id => { if (id == "chaoxing") openConnection(); else PlatformConnectionRequested?.Invoke(id); }) { Owner = this };
        courseSelection.Closed += (_, _) => courseSelection = null;
        courseSelection.Show();
    }

    private Task RefreshRecordingCourseCatalogAsync(string platformId) => platformId switch
    {
        "chaoxing" when sync is not null => sync.RefreshCourseCatalogAsync(),
        "mooc" when moocSync is not null => moocSync.RefreshCourseCatalogAsync(),
        _ when rainServices.TryGetValue(platformId, out var service) => service.RefreshCourseCatalogAsync(),
        _ => Task.CompletedTask
    };

    private async Task ApplyRecordingCourseSelectionAsync(string platformId)
    {
        RefreshTasks();
        ShowSuccess(PlatformCatalog.Name(platformId) + "记录课程已保存，未选课程停止采集和提醒。");
        try
        {
            if (platformId == "chaoxing" && sync is not null) await sync.RecordingCoursesChangedAsync();
            else if (platformId == "mooc" && moocSync is not null) await moocSync.RecordingCoursesChangedAsync();
            else if (rainServices.TryGetValue(platformId, out var service)) await service.RecordingCoursesChangedAsync();
        }
        catch { if (!permitClose) ShowError("COURSE001", "课程选择已保存，后续同步将按新选择执行；本轮刷新未完成。"); }
    }
}
