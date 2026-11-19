using System.Windows;
using System.Windows.Controls;

namespace AccessProbe;

public partial class TaskDashboardWindow
{
    private readonly Dictionary<string, CourseSelectionPage> platformPages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Message, bool Running, bool Warning)> platformStatuses = new(StringComparer.Ordinal);

    private void OpenPlatformManager(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is string id) ShowPlatformManager(id);
    }

    private void ShowPlatformManager(string id, bool replace = false)
    {
        if (permitClose || workspaceExiting || !PlatformCatalog.Sites.Any(site => site.Id == id)) return;
        if (platformPages.TryGetValue(id, out var existing))
        {
            if (existing.FindName("PlatformCoursesExpander") is Expander courses) courses.IsExpanded = true;
            ShowWorkspacePage("platform:" + id, existing, retained: true, replace: replace); return;
        }
        var page = new CourseSelectionPage(store, id, RefreshRecordingCourseCatalogAsync, ApplyRecordingCourseSelectionAsync,
            ConnectPlatform, true, SetPlatformAutomatic, CancelPlatformSync);
        platformPages[id] = page;
        page.Closed += (_, _) => platformPages.Remove(id);
        if (platformStatuses.TryGetValue(id, out var status)) page.ShowSyncStatus(status.Message, status.Running, status.Warning);
        ShowWorkspacePage("platform:" + id, page, retained: true, replace: replace);
    }

    private void ConnectPlatform(string id)
    {
        if (id == "chaoxing") openConnection(); else PlatformConnectionRequested?.Invoke(id);
    }

    private void UpdatePlatformStatus(string id, string message, bool running, bool warning)
    {
        platformStatuses[id] = (message, running, warning);
        if (platformPages.TryGetValue(id, out var page)) page.ShowSyncStatus(message, running, warning);
        UpdatePlatformCard(id);
        UpdateOnboardingSummary();
        if (tasks.Count == 0 && IsLoaded && !permitClose) ApplyFilters();
    }

    private void UpdatePlatformCard(string id)
    {
        var prefix = id switch { "chaoxing" => "Chaoxing", "yuketang" => "Rain", "changjiang" => "Changjiang",
            "mooc" => "Mooc", "xuetang" => "Xuetang", "ucampus" => "Ucampus", "pta" => "Pta", "zhihuishu" => "Zhihuishu", _ => "" };
        if (prefix.Length == 0) return;
        if (FindName(prefix + "PlatformSummary") is not TextBlock label) return;
        var status = platformStatuses.GetValueOrDefault(id);
        var access = PlatformAccessPresentation.For(id, store.LoadPlatformSessionInfo(id), store.LoadCourseRecording(id),
            store.LoadConnectionReport(id), status.Running, status.Warning);
        platformAccess[id] = access;
        label.Text = access.StatusTitle;
        label.ToolTip = access.Description;
        if (FindName(prefix + "PlatformIndicator") is System.Windows.Shapes.Ellipse indicator)
            indicator.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, AccessStatusBrush(access.State));
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
