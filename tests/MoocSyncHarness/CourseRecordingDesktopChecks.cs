using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AccessProbe;

namespace MoocSyncHarness;

internal static partial class Program
{
    private static async Task VerifyCourseRecordingAsync()
    {
        var completedIds = store.LoadTasks().Where(task => task.Source == TaskSource.Mooc && task.State == TaskState.Completed)
            .Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = MoocPlatform.Name;
        var window = await OpenRecordingAsync();
        var selector = Recording<ComboBox>(window, "RecordingPlatformSelector");
        Check(selector.Items.Count == 1 && selector.Visibility == Visibility.Collapsed && ((PlatformSite)selector.SelectedItem).Id == "mooc",
            "platform manager is fixed to MOOC with no account selector");
        Check(RecordingChoices(window).Length == 2 && RecordingChoices(window).All(choice => choice.Selected)
            && !store.LoadCourseRecording("mooc").RecordAllCourses, "confirmed current terms are checked explicitly");
        Check(Recording<TextBlock>(window, "PlatformSessionStatus").Text.Contains("下次自动复用", StringComparison.Ordinal), "saved login reuse shown in manager");
        Check(window.ResizeMode == ResizeMode.CanResize && Recording<Button>(window, "PlatformSyncButton").IsVisible
            && Recording<CheckBox>(window, "PlatformAutomaticSync").IsVisible && Recording<Expander>(window, "PlatformDetailsExpander").IsVisible,
            "login selection sync automatic stop and details share one resizable manager");
        var firstKey = CourseRecordingRules.MoocKey(fixture.Account, "1001", "2001");
        var otherKey = CourseRecordingRules.MoocKey(fixture.Account, "1001", "2002");
        var summaryBefore = JsonSerializer.Serialize(store.LoadMoocSettings()); var requests = fixture.Requests;
        RecordingClick(window, "RecordingRefreshButton");
        await WaitAsync(() => Recording<Button>(window, "RecordingSaveButton").IsEnabled && fixture.Requests >= requests + 2);
        Check(fixture.Requests == requests + 2 && JsonSerializer.Serialize(store.LoadMoocSettings()) == summaryBefore,
            "catalog refresh reads only catalog without replacing task summary");
        fixture.RejectRpc = true; requests = fixture.Requests;
        RecordingClick(window, "RecordingRefreshButton");
        await WaitAsync(() => Recording<Button>(window, "RecordingSaveButton").IsEnabled && fixture.Requests > requests);
        Check(store.LoadCourseRecording("mooc").LastCatalogRefresh is { Outcome: "login_required" }
            && store.LoadPlatformSessionInfo("mooc").State == "expired"
            && Recording<Button>(window, "RecordingConnectionButton").Content is "重新登录并更新", "expired cookie offers actionable relogin");
        Check(RecordingChoices(window).Length == 2 && RecordingChoices(window).All(choice => choice.Selected)
            && JsonSerializer.Serialize(store.LoadMoocSettings()) == summaryBefore, "login expiry preserves catalog choices task summary and history");
        fixture.RejectRpc = false; Fixtures.SetCookie(browser, fixture.ExpectedToken); requests = fixture.Requests;
        RecordingClick(window, "RecordingRefreshButton");
        await WaitAsync(() => Recording<Button>(window, "RecordingSaveButton").IsEnabled && fixture.Requests >= requests + 2);
        Check(store.LoadPlatformSessionInfo("mooc").State == "ready" && store.LoadCourseRecording("mooc").LastCatalogRefresh is { Outcome: "completed" },
            "current relogin cookie replaces expired record");
        await SetRecordingChoiceAsync(window, otherKey, false);
        var search = Recording<TextBox>(window, "RecordingCourseSearch"); search.Text = "2001";
        Check(Recording<ListBox>(window, "RecordingCourseList").Items.Count == 1, "course search selects correct term");
        RecordingClick(window, "RecordingSelectNoneButton"); search.Text = "";
        Check(RecordingChoices(window).All(choice => !choice.Selected), "bulk deselection affects current search results");
        search.Text = "2001"; RecordingClick(window, "RecordingSelectAllButton"); search.Text = "";
        Check(RecordingChoices(window).Single(choice => choice.Key == firstKey).Selected
            && !RecordingChoices(window).Single(choice => choice.Key == otherKey).Selected, "bulk selection excludes the other course term");
        CaptureWindow(window, "course-selection-desktop.png");
        var expanded = window.Height; RecordingClick(window, "PlatformCollapseButton"); await Task.Delay(80); window.UpdateLayout();
        Check(!Recording<Expander>(window, "PlatformCoursesExpander").IsExpanded && window.Height < expanded
            && Recording<Button>(window, "RecordingConnectionButton").IsVisible && Recording<Button>(window, "PlatformSyncButton").IsVisible,
            "collapse shrinks popup while retaining login and sync operations");
        CaptureWindow(window, "platform-manager-collapsed.png");
        RecordingClick(window, "PlatformCollapseButton"); window.Width = window.MinWidth; window.Height = window.MinHeight;
        await Task.Delay(100); window.UpdateLayout();
        foreach (var name in new[] { "RecordingCourseSearch", "RecordingCourseList", "RecordingSaveButton", "RecordingCancelButton", "RecordingHideHistory" })
        {
            var element = Recording<FrameworkElement>(window, name); var content = (FrameworkElement)window.Content;
            var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1,
                "minimum manager layout keeps " + name + " inside window");
        }
        CaptureWindow(window, "course-selection-desktop-minimum.png");
        var courseListHeight = Recording<ListBox>(window, "RecordingCourseList").ActualHeight;
        tableHeights["minimumCourseList"] = courseListHeight;
        Check(courseListHeight >= 70, "minimum manager retains usable course rows (height=" + courseListHeight + ")");
        window.Close();
        Check(store.LoadCourseRecording("mooc").SelectedCourseKeys.Count == 2, "closing discards unsaved course changes");

        window = await OpenRecordingAsync(); await SetRecordingChoiceAsync(window, otherKey, false);
        await RunAsync(() => RecordingClick(window, "RecordingSaveButton"));
        Click("ShowAllTasksButton"); await WaitAsync(() => Table.Items.Count == 7);
        var settings = store.LoadCourseRecording("mooc"); var summary = store.LoadMoocSettings().LastSummary!;
        Check(settings.SelectionConfirmed && !settings.RecordAllCourses && settings.SelectedCourseKeys.SequenceEqual(new[] { firstKey }),
            "save persists exact course choice");
        Check(window.IsVisible, "save keeps manager open (visibility=" + window.Visibility + ", state=" + window.WindowState + ", owner=" + dashboard.WindowState + ")");
        Check(summary.Courses == 2 && summary.SelectedCourses == 1 && summary.CoursesRead == 1 && summary.Tasks == 4 && summary.Requests == 4,
            "targeted crawl skips all excluded course requests");
        Check(store.LoadTasks().Count == 11 && new TaskStore(store.DatabasePath).LoadRecordedTasks().Count == 7,
            "restart restores selection while preserving excluded history");
        RecordingClick(window, "RecordingSelectNoneButton"); await RunAsync(() => RecordingClick(window, "RecordingSaveButton"));
        Click("ShowAllTasksButton"); await WaitAsync(() => Table.Items.Count == 3);
        Check(store.LoadMoocSettings().LastSummary is { SelectedCourses: 0, Tasks: 0, Requests: 2 } && store.LoadTasks().Count == 11,
            "explicit select-none makes only catalog requests and retains tasks from all platforms");
        RecordingClick(window, "RecordingSelectAllButton"); await RunAsync(() => RecordingClick(window, "RecordingSaveButton"));
        Click("ShowAllTasksButton"); await WaitAsync(() => Table.Items.Count == 11);
        Check(!store.LoadCourseRecording("mooc").RecordAllCourses && store.LoadCourseRecording("mooc").SelectedCourseKeys.Count == 2
            && store.LoadCourseRecording("changjiang").RecordAllCourses, "select-all freezes current choices and leaves other platforms unchanged");
        Check(completedIds.Count == 2 && store.LoadTasks().Where(task => completedIds.Contains(task.Id)).All(task => task.State == TaskState.Completed),
            "hide and restore retain completed states");

        summaryBefore = JsonSerializer.Serialize(store.LoadMoocSettings()); fixture.PauseTerm(); requests = fixture.Requests;
        RecordingClick(window, "PlatformSyncButton"); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RecordingClick(window, "PlatformStopButton"); fixture.Release!.TrySetResult(true);
        await WaitAsync(() => !service.IsRunning && Recording<Button>(window, "RecordingSaveButton").IsEnabled); fixture.ResetPause();
        Check(JsonSerializer.Serialize(store.LoadMoocSettings()) == summaryBefore && store.LoadTasks().Count == 11,
            "platform stop cancels pending task response without altering saved result");
        window.Close();
        fixture.PauseTerm(); requests = fixture.Requests;
        var oldRun = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var catalogRefresh = service.RefreshCourseCatalogAsync(); fixture.Release!.TrySetResult(true);
        await oldRun; await catalogRefresh; fixture.ResetPause();
        Check(fixture.Requests == requests + 5 && JsonSerializer.Serialize(store.LoadMoocSettings()) == summaryBefore && store.LoadTasks().Count == 11,
            "catalog refresh cancels prior task run and preserves last task result");
    }

    private static T Recording<T>(CourseSelectionWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static CourseSelectionWindow.CourseChoice[] RecordingChoices(CourseSelectionWindow window)
        => ((IEnumerable<CourseSelectionWindow.CourseChoice>)Recording<ListBox>(window, "RecordingCourseList").ItemsSource).ToArray();
    private static void RecordingClick(CourseSelectionWindow window, string name)
        => Recording<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task<CourseSelectionWindow> OpenRecordingAsync()
    {
        ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = MoocPlatform.Name;
        Click("ChooseRecordingCoursesButton");
        await WaitAsync(() => dashboard.OwnedWindows.OfType<CourseSelectionWindow>().Any());
        var window = dashboard.OwnedWindows.OfType<CourseSelectionWindow>().Single();
        await WaitAsync(() => Recording<ListBox>(window, "RecordingCourseList").ItemsSource is not null && Recording<Button>(window, "RecordingSaveButton").IsEnabled);
        return window;
    }
    private static async Task SetRecordingChoiceAsync(CourseSelectionWindow window, string key, bool selected)
    {
        var list = Recording<ListBox>(window, "RecordingCourseList"); var choice = RecordingChoices(window).Single(choice => choice.Key == key);
        list.ScrollIntoView(choice); list.UpdateLayout();
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(choice);
        var checkbox = FindVisual<CheckBox>(container) ?? throw new InvalidOperationException("A course row did not render its checkbox.");
        checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty, selected);
        await WaitAsync(() => choice.Selected == selected);
    }
}
