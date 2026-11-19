using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AccessProbe;

namespace TaskDashboardHarness;

internal static class TaskFilterChecks
{
    public static async Task VerifyAsync(TaskDashboardWindow dashboard, TaskStore store, string runRoot, Action<bool, string> check)
    {
        var labels = new List<string>();
        void Check(bool value, string label) { check(value, label); labels.Add(label); }
        T Find<T>(string name) where T : FrameworkElement => (T)dashboard.FindName(name);
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var table = Find<DataGrid>("TaskTable");
        var state = Find<ComboBox>("StateFilter");
        var time = Find<ComboBox>("TimeFilter");
        var platform = Find<ComboBox>("PlatformFilter");
        var course = Find<ComboBox>("CourseFilter");
        var start = Find<DatePicker>("FilterFromDate");
        var end = Find<DatePicker>("FilterToDate");
        var search = Find<System.Windows.Controls.TextBox>("SearchBox");
        var oldWidth = dashboard.Width; var oldHeight = dashboard.Height;
        var now = DateTimeOffset.UtcNow;
        var today = now.ToLocalTime().Date;
        var marker = "FILTER-SYNTHETIC-" + Guid.NewGuid().ToString("N")[..8];
        DateTimeOffset Date(int days, int hour) => new(DateTime.SpecifyKind(today.AddDays(days).AddHours(hour), DateTimeKind.Local));
        var created = new List<TaskItem>();
        TaskItem Create(string title, string courseName, string platformId, int days, TaskState taskState = TaskState.Pending)
        {
            var task = store.Create(new(marker + " " + title, platformId, courseName, DeadlineKind.Known, null, Date(days, 23), null, null, "", "筛选独立验收", null), now);
            if (taskState != TaskState.Pending) task = store.SetState(task.Id, task.Revision, taskState, now);
            created.Add(task); return task;
        }
        Create("A completed", marker + " A", "chaoxing", -1, TaskState.Completed);
        Create("A today", marker + " A", "chaoxing", 0);
        Create("A future", marker + " A", "chaoxing", 7);
        Create("B today", marker + " B", "chaoxing", 0);
        Create("A other platform", marker + " A", "pta", 0);
        async Task Wait(Func<bool> condition)
        {
            for (var i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(20); }
            throw new InvalidOperationException("Task filter UI did not update in time.");
        }
        int MarkerCount() => table.Items.OfType<TaskDashboardWindow.TaskDisplayRow>().Count(row => row.Task.Title.StartsWith(marker, StringComparison.Ordinal));
        try
        {
            dashboard.ShowDashboard(); dashboard.RefreshTasks();
            await Wait(() => MarkerCount() == created.Count);
            search.Text = marker;
            Check(table.Items.Count == 5, "search finds every isolated filter fixture");
            state.SelectedIndex = 3; time.SelectedIndex = (int)TaskFilterTime.Overdue;
            Check(table.Items.Count == 1 && table.Items.OfType<TaskDashboardWindow.TaskDisplayRow>().Single().Task.State == TaskState.Completed,
                "desktop completed status combines with overdue time independently");
            state.SelectedIndex = 1; time.SelectedIndex = (int)TaskFilterTime.Today;
            Check(table.Items.Count == 3, "desktop today includes only deadline dates matching local today");
            state.SelectedIndex = 6;
            Check(time.SelectedIndex == 0 && state.SelectedIndex == 6, "overdue shortcut clears independent time instead of showing contradictory conditions");
            time.SelectedIndex = (int)TaskFilterTime.Today;
            Check(state.SelectedIndex == 0 && table.Items.Count == 3, "independent time converts an overdue shortcut to matching active status");
            state.SelectedIndex = 1;
            platform.SelectedIndex = PlatformCatalog.Sites.ToList().FindIndex(site => site.Id == "chaoxing") + 1;
            Check(table.Items.Count == 2, "desktop platform filter composes with local today and search");
            var options = ((IEnumerable<TaskCourseFilterOption>)course.ItemsSource).ToArray();
            course.SelectedItem = options.Single(option => option.Label.StartsWith(marker + " B", StringComparison.Ordinal));
            Check(table.Items.Count == 1 && table.Items.OfType<TaskDashboardWindow.TaskDisplayRow>().Single().Task.CourseName == marker + " B",
                "desktop course identity composes with state time platform and search");
            var selectedId = ((TaskDashboardWindow.TaskDisplayRow)table.SelectedItem).Task.Id;
            Find<ComboBox>("SortFilter").SelectedIndex = 2;
            Check(((TaskDashboardWindow.TaskDisplayRow)table.SelectedItem).Task.Id == selectedId,
                "changing sort retains selected matching task");
            platform.SelectedIndex = PlatformCatalog.Sites.ToList().FindIndex(site => site.Id == "pta") + 1;
            Check(course.SelectedIndex == 0 && table.Items.Count == 1,
                "changing platform resets an unavailable course instead of retaining impossible selection");
            platform.SelectedIndex = 0;
            time.SelectedIndex = (int)TaskFilterTime.Custom;
            Check(Find<FrameworkElement>("TaskDateRangePanel").Visibility == Visibility.Visible && Find<TextBlock>("TaskFilterError").Visibility == Visibility.Visible,
                "custom date opens inline date controls and explains missing range");
            start.SelectedDate = today; end.SelectedDate = today;
            Check(table.Items.Count == 3 && Find<TextBlock>("TaskFilterError").Visibility == Visibility.Collapsed,
                "same-day custom range includes final day's tasks in main window");
            dashboard.UpdateLayout(); end.ApplyTemplate();
            var dateEditor = (System.Windows.Controls.Primitives.DatePickerTextBox)end.Template.FindName("PART_TextBox", end);
            var invalidDateEvents = 0; var selectedDateChanges = 0;
            end.DateValidationError += (_, _) => invalidDateEvents++;
            end.SelectedDateChanged += (_, _) => selectedDateChanges++;
            void CommitDateText() => dateEditor.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(dateEditor), Environment.TickCount, System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            async Task CommitDateTextAsync()
            {
                CommitDateText();
                await dashboard.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            dateEditor.Text = "not-a-date"; await CommitDateTextAsync();
            Check(invalidDateEvents == 1 && end.SelectedDate == today && table.Items.Count == 0
                && Find<TextBlock>("TaskFilterError").Text.Contains("日期格式"),
                "native DatePicker rejects invalid text and preserves a visible error after automatic rollback");
            dateEditor.Text = today.ToString("d", end.Language.GetSpecificCulture()); await CommitDateTextAsync();
            var dateFailureEvidence = new
            {
                invalidDateEvents, selectedDateChanges, rows = table.Items.Count, text = dateEditor.Text, pickerText = end.Text,
                selectedDate = end.SelectedDate, expectedDate = today, errorVisibility = Find<TextBlock>("TaskFilterError").Visibility.ToString(),
                errorText = Find<TextBlock>("TaskFilterError").Text
            };
            await File.WriteAllTextAsync(Path.Combine(runRoot, "date-correction-diagnostic.json"), JsonSerializer.Serialize(dateFailureEvidence));
            Check(selectedDateChanges == 0 && table.Items.Count == 3 && Find<TextBlock>("TaskFilterError").Visibility == Visibility.Collapsed,
                "correcting date text back to the same date clears error without requiring a selected-date change "
                    + JsonSerializer.Serialize(dateFailureEvidence));
            dateEditor.Text = "still-not-a-date"; await CommitDateTextAsync();
            Check(invalidDateEvents == 2 && table.Items.Count == 0, "subsequent invalid date remains rejected rather than accepting restored old text");
            await CommitDateTextAsync();
            Check(selectedDateChanges == 0 && table.Items.Count == 3, "explicit Enter confirmation accepts the retained valid date after rejection");
            dateEditor.Text = "another-invalid-date"; await CommitDateTextAsync();
            var calendarClosedEvents = 0; end.CalendarClosed += (_, _) => calendarClosedEvents++;
            end.IsDropDownOpen = true; await Task.Delay(100); end.IsDropDownOpen = false; await Task.Delay(100);
            Check(selectedDateChanges == 0 && table.Items.Count == 3 && Find<TextBlock>("TaskFilterError").Visibility == Visibility.Collapsed,
                $"calendar confirmation of the same selected date clears previous input error (closed={calendarClosedEvents}, dateChanges={selectedDateChanges}, rows={table.Items.Count}, text={dateEditor.Text})");
            start.SelectedDate = today.AddDays(1);
            Check(table.Items.Count == 0 && Find<TextBlock>("TaskFilterError").Text.Contains("不能晚于"),
                "reversed custom dates explain invalid input without revealing all tasks");
            start.SelectedDate = null;
            Check(table.Items.Count == 4, "desktop custom date supports an end-only range");
            start.SelectedDate = today.AddDays(1); end.SelectedDate = null;
            Check(table.Items.Count == 1, "desktop custom date supports a start-only range");
            dashboard.Width = 1100; dashboard.Height = 700; dashboard.UpdateLayout(); await Task.Delay(75); dashboard.UpdateLayout();
            var workspace = Find<FrameworkElement>("TaskWorkspacePanel");
            foreach (var name in new[] { "TimeFilter", "CourseFilter", "FilterFromDate", "FilterToDate" })
            {
                var control = Find<FrameworkElement>(name);
                var bounds = control.TransformToAncestor(workspace).TransformBounds(new Rect(control.RenderSize));
                Check(control.ActualWidth >= 90 && bounds.Left >= -1 && bounds.Right <= workspace.ActualWidth + 1,
                    "desktop " + name + " remains usable within minimum-size main window");
            }
            Check(Application.Current.Windows.Count == 1 && Application.Current.Windows[0] == dashboard,
                "all task filter interactions retain exactly one native window");
            Click("ShowAllTasksButton");
            Check(state.SelectedIndex == 1 && time.SelectedIndex == 0 && platform.SelectedIndex == 0 && course.SelectedIndex == 0
                && string.IsNullOrEmpty(search.Text) && start.SelectedDate is null && end.SelectedDate is null,
                "clear filters resets status time platform course search and date endpoints together");
            Check(Find<TextBlock>("FilterCountText").Text.Contains("当前显示") && Find<TextBlock>("ToolbarCountText").Text == table.Items.Count + " 项",
                "desktop match counts agree with filtered task rows");
            await File.WriteAllTextAsync(Path.Combine(runRoot, "task-filter-checks.json"), JsonSerializer.Serialize(new
            { schema = 1, outcome = "passed", checks = labels.Count, results = labels, officialNetworkAccessed = false, productionDataAccessed = false }));
        }
        finally
        {
            dashboard.Width = oldWidth; dashboard.Height = oldHeight;
            foreach (var task in created) { var current = store.Get(task.Id); if (current is not null) store.Delete(current.Id, current.Revision); }
            Click("ShowAllTasksButton"); dashboard.RefreshTasks();
            await Wait(() => !table.Items.OfType<TaskDashboardWindow.TaskDisplayRow>().Any(row => row.Task.Title.StartsWith(marker, StringComparison.Ordinal)));
        }
    }
}
