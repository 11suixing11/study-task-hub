using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Globalization;

namespace AccessProbe;

public partial class TaskDashboardWindow
{
    private bool updatingFilterCourses;
    private bool invalidFilterFromDate;
    private bool invalidFilterToDate;
    private bool additionalTaskFiltersOpen;
    private readonly HashSet<DatePicker> datesRevertingInvalidText = [];

    private void ConfigureTaskFilters()
    {
        StateFilter.ItemsSource = new[] { "待办（含待核对）", "全部状态", "未完成", "已完成", "已忽略", "被打回", "已逾期待办", "24 小时内待办", "待核对" };
        StateFilter.SelectedIndex = 1;
        PlatformFilter.ItemsSource = new[] { "全部平台" }.Concat(PlatformCatalog.Sites.Select(site => site.Name)).ToArray();
        PlatformFilter.SelectedIndex = 0;
        SortFilter.ItemsSource = TaskFiltering.SortLabels;
        SortFilter.SelectedIndex = 0;
        TimeFilter.ItemsSource = TaskFiltering.TimeLabels;
        TimeFilter.SelectedIndex = 0;
        CourseFilter.ItemsSource = TaskFiltering.Courses([]);
        CourseFilter.SelectedIndex = 0;
        foreach (var picker in new[] { FilterFromDate, FilterToDate })
        {
            picker.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(FilterDateTextEdited), true);
            picker.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(FilterDateKeyPressed), true);
            picker.CalendarClosed += FilterDateCalendarClosed;
        }
    }

    private void RefreshFilterCourses()
    {
        if (updatingFilterCourses) return;
        var platform = PlatformFilter.SelectedIndex > 0 ? PlatformCatalog.Sites[PlatformFilter.SelectedIndex - 1].Id : null;
        var courses = TaskFiltering.Courses(tasks, platform);
        if (CourseFilter.ItemsSource is IReadOnlyList<TaskCourseFilterOption> previous && previous.SequenceEqual(courses)) return;
        var selectedKey = (CourseFilter.SelectedItem as TaskCourseFilterOption)?.Key;
        updatingFilterCourses = true;
        try
        {
            CourseFilter.ItemsSource = courses;
            CourseFilter.SelectedItem = courses.FirstOrDefault(course => course.Key == selectedKey) ?? courses[0];
        }
        finally { updatingFilterCourses = false; }
    }

    private TaskFilterOptions CurrentTaskFilters()
    {
        var (state, shortcut) = StateFilter.SelectedIndex switch
        {
            0 => (TaskFilterState.Active, TaskFilterTime.All), 2 => (TaskFilterState.Pending, TaskFilterTime.All),
            3 => (TaskFilterState.Completed, TaskFilterTime.All), 4 => (TaskFilterState.Ignored, TaskFilterTime.All),
            5 => (TaskFilterState.Returned, TaskFilterTime.All), 6 => (TaskFilterState.Active, TaskFilterTime.Overdue),
            7 => (TaskFilterState.Active, TaskFilterTime.Next24Hours), 8 => (TaskFilterState.NeedsReview, TaskFilterTime.All),
            _ => (TaskFilterState.All, TaskFilterTime.All)
        };
        var time = (TaskFilterTime)Math.Max(0, TimeFilter.SelectedIndex);
        return new()
        {
            State = state, Time = time == TaskFilterTime.All ? shortcut : time,
            PlatformId = PlatformFilter.SelectedIndex > 0 ? PlatformCatalog.Sites[PlatformFilter.SelectedIndex - 1].Id : null,
            CourseKey = (CourseFilter.SelectedItem as TaskCourseFilterOption)?.Key,
            Search = SearchBox.Text, Sort = (TaskFilterSort)Math.Max(0, SortFilter.SelectedIndex),
            FromDate = FilterFromDate.SelectedDate is { } start ? DateOnly.FromDateTime(start) : null,
            ToDate = FilterToDate.SelectedDate is { } end ? DateOnly.FromDateTime(end) : null
        };
    }

    private void ToggleTaskFilters(object sender, RoutedEventArgs args)
    {
        additionalTaskFiltersOpen = !additionalTaskFiltersOpen;
        ApplyFilters();
    }

    private void NormalizeTaskFilterShortcut(object sender)
    {
        if (sender == TimeFilter && TimeFilter.SelectedIndex > 0 && StateFilter.SelectedIndex is 6 or 7)
        {
            var wasReady = ready; ready = false;
            try { StateFilter.SelectedIndex = 0; }
            finally { ready = wasReady; }
        }
        if (sender == StateFilter && StateFilter.SelectedIndex is 6 or 7 && TimeFilter.SelectedIndex > 0)
        {
            var wasReady = ready; ready = false;
            try { TimeFilter.SelectedIndex = 0; }
            finally { ready = wasReady; }
        }
    }

    private void FilterDateChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender == FilterFromDate) invalidFilterFromDate = false;
        if (sender == FilterToDate) invalidFilterToDate = false;
        if (ready) ApplyFilters();
    }

    private void FilterDateInvalid(object sender, DatePickerDateValidationErrorEventArgs args)
    {
        args.ThrowException = false;
        if (sender == FilterFromDate) invalidFilterFromDate = true;
        if (sender == FilterToDate) invalidFilterToDate = true;
        if (sender is DatePicker picker)
        {
            // WPF immediately restores the previous valid text after rejecting input. Keep that
            // automatic rollback distinct from the user's next edit or calendar confirmation.
            datesRevertingInvalidText.Add(picker);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => datesRevertingInvalidText.Remove(picker));
        }
        if (ready) ApplyFilters();
    }

    private void FilterDateTextEdited(object sender, TextChangedEventArgs args)
    {
        if (sender is DatePicker picker && args.OriginalSource is DatePickerTextBox editor)
            ConfirmCorrectedFilterDate(picker, editor.Text);
    }

    private void FilterDateKeyPressed(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && sender is DatePicker picker) ConfirmCorrectedFilterDate(picker, picker.Text);
    }

    private void FilterDateCalendarClosed(object? sender, RoutedEventArgs args)
    {
        if (sender is DatePicker picker) ConfirmCorrectedFilterDate(picker, picker.Text);
    }

    private void ConfirmCorrectedFilterDate(DatePicker picker, string text)
    {
        if (datesRevertingInvalidText.Contains(picker)
            || !(picker == FilterFromDate ? invalidFilterFromDate : invalidFilterToDate)
            || picker.SelectedDate is not { } selectedDate
            || !DateTime.TryParse(text, picker.Language.GetSpecificCulture(), DateTimeStyles.None, out var parsed)
            || parsed.Date != selectedDate.Date) return;
        if (picker == FilterFromDate) invalidFilterFromDate = false;
        if (picker == FilterToDate) invalidFilterToDate = false;
        if (ready) ApplyFilters();
    }

    private void ResetAdditionalTaskFilters()
    {
        TimeFilter.SelectedIndex = 0;
        CourseFilter.SelectedIndex = 0;
        FilterFromDate.SelectedDate = null;
        FilterToDate.SelectedDate = null;
        FilterFromDate.Text = "";
        FilterToDate.Text = "";
        invalidFilterFromDate = invalidFilterToDate = false;
        datesRevertingInvalidText.Clear();
        additionalTaskFiltersOpen = false;
    }
}
