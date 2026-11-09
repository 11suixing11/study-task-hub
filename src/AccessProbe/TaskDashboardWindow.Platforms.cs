using System.Windows;
using System.Windows.Controls;

namespace AccessProbe;

public partial class TaskDashboardWindow
{
    private readonly Dictionary<string, CourseSelectionWindow> platformWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Message, bool Running, bool Warning)> platformStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> selectionPrompts = new(StringComparer.Ordinal);

    private void OpenPlatformManager(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is string id) ShowPlatformManager(id);
    }

    private void ShowPlatformManager(string id)
    {
        if (permitClose || !CourseRecordingRules.SupportsAutomatic(id)) return;
        if (platformWindows.TryGetValue(id, out var existing))
        {
            existing.WindowState = WindowState.Normal;
            if (existing.FindName("PlatformCoursesExpander") is Expander courses) courses.IsExpanded = true;
            existing.Show(); existing.Activate(); return;
        }
        var window = new CourseSelectionWindow(store, id, RefreshRecordingCourseCatalogAsync, ApplyRecordingCourseSelectionAsync,
            ConnectPlatform, true, SetPlatformAutomatic, CancelPlatformSync) { Owner = this };
        platformWindows[id] = window;
        window.Closed += (_, _) => platformWindows.Remove(id);
        if (platformStatuses.TryGetValue(id, out var status)) window.ShowSyncStatus(status.Message, status.Running, status.Warning);
        window.Show();
    }

    private void ConnectPlatform(string id)
    {
        if (id == "chaoxing") openConnection(); else PlatformConnectionRequested?.Invoke(id);
    }

    private void UpdatePlatformStatus(string id, string message, bool running, bool warning)
    {
        platformStatuses[id] = (message, running, warning);
        if (platformWindows.TryGetValue(id, out var window)) window.ShowSyncStatus(message, running, warning);
        UpdatePlatformCard(id);
    }

    private void UpdatePlatformCard(string id)
    {
        var prefix = id switch { "chaoxing" => "Chaoxing", "yuketang" => "Rain", "changjiang" => "Changjiang",
            "mooc" => "Mooc", "xuetang" => "Xuetang", "ucampus" => "Ucampus", "pta" => "Pta", _ => "Zhihuishu" };
        if (FindName(prefix + "PlatformSummary") is not TextBlock label) return;
        var session = store.LoadPlatformSessionInfo(id);
        var recording = store.LoadCourseRecording(id);
        label.Text = platformStatuses.TryGetValue(id, out var status) && status.Running ? "正在读取…"
            : session.State == "expired" ? "登录过期 · 点击更新"
            : session.State == "ready" && recording.LastCatalogRefresh is { Outcome: "partial" or "error" } ? "已登录 · 课程刷新需核对"
            : !recording.SelectionConfirmed && session.State == "ready" ? recording.CatalogCheckedAtUtc is null ? "已登录 · 请刷新课程" : "已登录 · 请选择课程"
            : !recording.SelectionConfirmed && session.CanReuse ? "登录已保存 · 刷新并选课"
            : !recording.SelectionConfirmed ? "请登录并选择课程"
            : $"已选 {recording.Courses.Count(course => CourseRecordingRules.IsSelected(recording, course.Key))} 门 · {session.Description.Split('·')[0].Trim()}";
        if (FindName(prefix + "PlatformIndicator") is System.Windows.Shapes.Ellipse indicator)
        {
            var color = platformStatuses.TryGetValue(id, out var currentStatus) && currentStatus.Running ? "PrimaryBrush"
                : session.State == "expired" ? "DangerBrush"
                : currentStatus.Warning || recording.LastCatalogRefresh is { Outcome: "partial" or "error" } ? "WarningBrush"
                : session.State == "ready" ? "SuccessBrush"
                : session.CanReuse ? "PrimaryBrush" : "MutedBrush";
            indicator.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, color);
        }
    }

    private void PromptCourseSelection(string id)
    {
        if (permitClose || !IsLoaded) return;
        var checkedAt = store.LoadCourseRecording(id).CatalogCheckedAtUtc;
        if (checkedAt is null || selectionPrompts.TryGetValue(id, out var previous) && previous == checkedAt) return;
        selectionPrompts[id] = checkedAt.Value;
        Dispatcher.BeginInvoke(new Action(() => { if (!permitClose) ShowPlatformManager(id); }));
    }

    private void CancelPlatformSync(string id)
    {
        if (id == "chaoxing") sync?.Cancel();
        else if (id == "mooc") moocSync?.Cancel();
        else if (id == "xuetang") xuetangSync?.Cancel();
        else if (rainServices.TryGetValue(id, out var rain)) rain.Cancel();
        else if (metadataServices.TryGetValue(id, out var metadata)) metadata.Cancel();
    }

    private void SetPlatformAutomatic(string id, bool enabled)
    {
        RunAction(() =>
        {
            var global = store.LoadSettings().AutomaticSyncEnabled;
            if (id == "chaoxing")
            {
                store.SaveSettings(store.LoadSettings() with { ChaoxingAutomaticSyncEnabled = enabled });
                sync?.SettingsChanged(enabled && global);
            }
            else if (id == "mooc")
            {
                store.SaveMoocSettings(store.LoadMoocSettings() with { AutomaticSyncEnabled = enabled });
                moocSync?.SettingsChanged(enabled && global);
            }
            else if (id == "xuetang")
            {
                store.SaveXuetangSettings(store.LoadXuetangSettings() with { AutomaticSyncEnabled = enabled });
                xuetangSync?.SettingsChanged(enabled && global);
            }
            else if (rainServices.TryGetValue(id, out var rain))
            {
                store.SavePlatformSettings(id, store.LoadPlatformSettings(id) with { AutomaticSyncEnabled = enabled });
                rain.SettingsChanged(enabled && global);
            }
            else if (metadataServices.TryGetValue(id, out var metadata))
            {
                store.SaveMetadataSettings(id, store.LoadMetadataSettings(id) with { AutomaticSyncEnabled = enabled });
                metadata.SettingsChanged(enabled && global);
            }
        });
    }
}
