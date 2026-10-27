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
        Check(selector.Items.Count == 8 && ((PlatformSite)selector.SelectedItem).Id == "mooc", "course selection covers all platforms and follows the task filter");
        Check(Recording<CheckBox>(window, "RecordingAllCourses").IsChecked == true
            && Recording<CheckBox>(window, "RecordingHideHistory").IsChecked == true, "course selection defaults to all courses and retained hidden history");
        var choices = RecordingChoices(window);
        Check(choices.Length == 2 && choices.Select(choice => choice.Name).Distinct().Count() == 1
            && choices.Select(choice => choice.Key).Distinct().Count() == 2 && choices.All(choice => choice.Selected),
            "same-name course terms appear as separate checked rows");
        var firstKey = CourseRecordingRules.MoocKey(fixture.Account, "1001", "2001");
        var otherKey = CourseRecordingRules.MoocKey(fixture.Account, "1001", "2002");
        var summaryBefore = JsonSerializer.Serialize(store.LoadMoocSettings()); var requests = fixture.Requests;
        RecordingClick(window, "RecordingRefreshButton");
        await WaitAsync(() => Recording<Button>(window, "RecordingSaveButton").IsEnabled && fixture.Requests >= requests + 2);
        Check(fixture.Requests == requests + 2 && JsonSerializer.Serialize(store.LoadMoocSettings()) == summaryBefore,
            "refreshing the course catalog makes only catalog requests and preserves the task-sync summary");
        Check(RecordingChoices(window).Length == 2 && Recording<TextBlock>(window, "RecordingResult").Text.Contains("已刷新", StringComparison.Ordinal),
            "catalog refresh renders rows and its success message");
        await SetRecordingChoiceAsync(window, otherKey, false);
        Check(!RecordingChoices(window).Single(choice => choice.Key == otherKey).Selected
            && Recording<CheckBox>(window, "RecordingAllCourses").IsChecked == false,
            "unchecking an actual course checkbox updates its binding and exits all-courses mode");
        await SelectRecordingPlatformAsync(window, "chaoxing");
        RecordingClick(window, "RecordingSelectNoneButton");
        Check(RecordingChoices(window).Length == 1 && RecordingChoices(window).All(choice => !choice.Selected), "another platform has an independent unsaved selection");
        await SelectRecordingPlatformAsync(window, "ucampus");
        Check(Recording<TextBlock>(window, "RecordingCatalogStatus").Text.Contains("手动录入", StringComparison.Ordinal)
            && RecordingChoices(window).Length == 0 && Recording<Button>(window, "RecordingConnectionButton").Visibility == Visibility.Collapsed,
            "manual-only platforms clearly identify local courses and empty catalog");
        await SelectRecordingPlatformAsync(window, "mooc");
        Check(RecordingChoices(window).Single(choice => choice.Key == firstKey).Selected
            && !RecordingChoices(window).Single(choice => choice.Key == otherKey).Selected,
            "switching platforms preserves the unsaved MOOC draft");
        var search = Recording<TextBox>(window, "RecordingCourseSearch"); search.Text = "2001";
        Check(Recording<ListBox>(window, "RecordingCourseList").Items.Count == 1, "course search filters by course term");
        RecordingClick(window, "RecordingSelectNoneButton"); search.Text = "";
        Check(RecordingChoices(window).All(choice => !choice.Selected), "bulk selection changes only the search results");
        search.Text = "2001"; RecordingClick(window, "RecordingSelectAllButton"); search.Text = "";
        Check(RecordingChoices(window).Single(choice => choice.Key == firstKey).Selected
            && !RecordingChoices(window).Single(choice => choice.Key == otherKey).Selected,
            "bulk re-selection leaves the other course term excluded");
        CaptureWindow(window, "course-selection-desktop.png");
        window.Width = window.MinWidth; window.Height = window.MinHeight; await Task.Delay(75); window.UpdateLayout();
        foreach (var name in new[] { "RecordingPlatformSelector", "RecordingCourseSearch", "RecordingCourseList", "RecordingSaveButton", "RecordingCancelButton", "RecordingHideHistory" })
        {
            var element = Recording<FrameworkElement>(window, name); var content = (FrameworkElement)window.Content;
            var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1,
                "minimum course-selection layout keeps " + name + " visible");
        }
        Check(Recording<ListBox>(window, "RecordingCourseList").ActualHeight >= 90, "minimum course-selection window retains usable course rows");
        CaptureWindow(window, "course-selection-desktop-minimum.png");
        RecordingClick(window, "RecordingCancelButton"); await WaitAsync(() => !window.IsVisible);
        Check(store.LoadCourseRecording("mooc").RecordAllCourses && store.LoadCourseRecording("chaoxing").RecordAllCourses,
            "cancel discards unsaved drafts for every platform");

        Click("ShowAllTasksButton"); ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = MoocPlatform.Name;
        window = await OpenRecordingAsync(); await SetRecordingChoiceAsync(window, otherKey, false);
        await RunAsync(() => RecordingClick(window, "RecordingSaveButton"));
        Click("ShowAllTasksButton"); await WaitAsync(() => Table.Items.Count == 7);
        var settings = store.LoadCourseRecording("mooc"); var summary = store.LoadMoocSettings().LastSummary!;
        Check(!window.IsVisible && !settings.RecordAllCourses && settings.HideUnselectedTasks
            && settings.SelectedCourseKeys.SequenceEqual(new[] { firstKey }), "save persists exactly the checked course and closes the selector");
        Check(summary.Courses == 2 && summary.SelectedCourses == 1 && summary.CoursesRead == 1 && summary.Tasks == 4 && summary.Requests == 4
            && store.LoadMoocSettings().LastSyncOutcome == "completed", "selected-course sync reads only that term and completes with accurate coverage");
        Check(store.LoadTasks().Count == 11 && Rows().Count(row => row.Task.Source == TaskSource.Mooc) == 4
            && Rows().Select(row => row.Task.PlatformId).ToHashSet().SetEquals(new[] { "chaoxing", "yuketang", "changjiang", "mooc" }),
            "unselected course tasks remain stored while all selected platforms share one list");
        Check(Text("AggregationSummaryText").Contains("中国大学 MOOC 4 条", StringComparison.Ordinal)
            && Text("FilterCountText").Contains("未选课程", StringComparison.Ordinal), "unified source counts and hidden-history explanation follow the course selection");
        Check(new TaskStore(System.IO.Path.Combine(runRoot, "tasks.db")).LoadRecordedTasks().Count == 7,
            "a reopened task store restores the saved selection");

        ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = MoocPlatform.Name;
        window = await OpenRecordingAsync(); RecordingClick(window, "RecordingSelectNoneButton");
        await RunAsync(() => RecordingClick(window, "RecordingSaveButton")); Click("ShowAllTasksButton"); await WaitAsync(() => Table.Items.Count == 3);
        summary = store.LoadMoocSettings().LastSummary!;
        Check(summary.Courses == 2 && summary.SelectedCourses == 0 && summary.CoursesRead == 0 && summary.Tasks == 0 && summary.Requests == 2
            && store.LoadMoocSettings().LastSyncOutcome == "completed", "no selected courses makes no task requests and is a complete sync");
        Check(store.LoadTasks().Count == 11 && Rows().All(row => row.Task.Source != TaskSource.Mooc), "all excluded MOOC history remains stored and other platforms remain visible");
        ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = MoocPlatform.Name;
        Check(((TextBlock)dashboard.FindName("EmptyText")).Text.Contains("课程", StringComparison.Ordinal), "an empty platform filter explains its course selections");

        window = await OpenRecordingAsync(); RecordingClick(window, "RecordingSelectAllButton");
        await SelectRecordingPlatformAsync(window, "changjiang"); RecordingClick(window, "RecordingSelectNoneButton");
        await RunAsync(() => RecordingClick(window, "RecordingSaveButton")); Click("ShowAllTasksButton"); await WaitAsync(() => Table.Items.Count == 10);
        Check(!store.LoadCourseRecording("mooc").RecordAllCourses && store.LoadCourseRecording("mooc").SelectedCourseKeys.Count == 2
            && !store.LoadCourseRecording("changjiang").RecordAllCourses && store.LoadCourseRecording("changjiang").SelectedCourseKeys.Count == 0,
            "one save applies independent drafts for multiple platforms");
        Check(store.LoadTasks().Count == 11 && store.LoadMoocSettings().LastSummary?.Added == 0
            && Rows().Count(row => row.Task.Source == TaskSource.Mooc) == 8 && Rows().All(row => row.Task.PlatformId != "changjiang"),
            "re-selecting course history restores tasks without duplicates and respects another platform's exclusion");

        ((ComboBox)dashboard.FindName("PlatformFilter")).SelectedItem = MoocPlatform.Name;
        window = await OpenRecordingAsync(); Recording<CheckBox>(window, "RecordingAllCourses").IsChecked = true;
        await SelectRecordingPlatformAsync(window, "changjiang"); Recording<CheckBox>(window, "RecordingAllCourses").IsChecked = true;
        await RunAsync(() => RecordingClick(window, "RecordingSaveButton")); Click("ShowAllTasksButton"); await WaitAsync(() => Table.Items.Count == 11);
        Check(store.LoadCourseRecording("mooc").RecordAllCourses && store.LoadCourseRecording("changjiang").RecordAllCourses,
            "all-courses mode restores the original unified list and includes future discoveries");
        var completed = store.LoadTasks().Where(task => completedIds.Contains(task.Id)).ToArray();
        Check(completedIds.Count == 2 && completed.Length == 2 && completed.All(task => task.State == TaskState.Completed),
            "hide and restore preserve the platform's completed task states");

        summaryBefore = JsonSerializer.Serialize(store.LoadMoocSettings()); fixture.PauseTerm(); requests = fixture.Requests;
        var oldRun = service.StartAsync(true); await fixture.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var catalogRefresh = service.RefreshCourseCatalogAsync(); fixture.Release!.TrySetResult(true);
        await oldRun; await catalogRefresh; fixture.ResetPause();
        Check(fixture.Requests == requests + 5 && JsonSerializer.Serialize(store.LoadMoocSettings()) == summaryBefore && store.LoadTasks().Count == 11,
            "catalog refresh stops the previous task run and reads the catalog without replacing the last task result");
    }

    private static T Recording<T>(CourseSelectionWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static CourseSelectionWindow.CourseChoice[] RecordingChoices(CourseSelectionWindow window)
        => ((IEnumerable<CourseSelectionWindow.CourseChoice>)Recording<ListBox>(window, "RecordingCourseList").ItemsSource).ToArray();
    private static void RecordingClick(CourseSelectionWindow window, string name)
        => Recording<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task<CourseSelectionWindow> OpenRecordingAsync()
    {
        Click("ChooseRecordingCoursesButton");
        await WaitAsync(() => dashboard.OwnedWindows.OfType<CourseSelectionWindow>().Any());
        var window = dashboard.OwnedWindows.OfType<CourseSelectionWindow>().Single();
        await WaitAsync(() => Recording<ListBox>(window, "RecordingCourseList").ItemsSource is not null && Recording<Button>(window, "RecordingSaveButton").IsEnabled);
        return window;
    }
    private static async Task SelectRecordingPlatformAsync(CourseSelectionWindow window, string platform)
    {
        var selector = Recording<ComboBox>(window, "RecordingPlatformSelector");
        selector.SelectedItem = PlatformCatalog.Sites.Single(site => site.Id == platform);
        await WaitAsync(() => ((IEnumerable<CourseSelectionWindow.CourseChoice>?)Recording<ListBox>(window, "RecordingCourseList").ItemsSource)?
            .All(choice => choice.Key.StartsWith(platform + ":", StringComparison.Ordinal)) == true
            && Recording<Button>(window, "RecordingSaveButton").IsEnabled
            && (CourseRecordingRules.SupportsAutomatic(platform) || Recording<TextBlock>(window, "RecordingCatalogStatus").Text.Contains("手动录入", StringComparison.Ordinal)));
        await Task.Delay(50);
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
