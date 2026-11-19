using AccessProbe;
using Android.App;
using Android.Widget;
using View = Android.Views.View;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private TaskFilterState taskFilterState;
    private TaskFilterTime taskFilterTime;
    private string? taskFilterPlatform;
    private string? taskFilterCourse;
    private TaskFilterSort taskFilterSort;
    private DateOnly? taskFilterFromDate;
    private DateOnly? taskFilterToDate;
    private bool taskMoreFiltersOpen;

    private void RefreshMobileTaskFilters()
    {
        if (dashboardList is null || renderDashboardTasks is null) return;
        dashboardSearchDelay?.Cancel();
        RebuildDashboardFilters(); renderDashboardTasks();
    }

    private TaskFilterOptions MobileTaskFilters() => new()
    {
        State = taskFilterState, Time = taskFilterTime, PlatformId = taskFilterPlatform,
        CourseKey = taskFilterCourse, Search = taskQuery, Sort = taskFilterSort,
        FromDate = taskFilterFromDate, ToDate = taskFilterToDate
    };

    private void ResetMobileTaskFilters()
    {
        taskFilter = 0;
        taskQuery = "";
        taskFilterState = TaskFilterState.All;
        taskFilterTime = TaskFilterTime.All;
        taskFilterPlatform = taskFilterCourse = null;
        taskFilterSort = TaskFilterSort.Priority;
        taskFilterFromDate = taskFilterToDate = null;
    }

    private void SelectMobileTaskShortcut(int index)
    {
        taskFilter = index;
        taskFilterState = index switch { 1 or 2 => TaskFilterState.Active, 3 => TaskFilterState.Completed, _ => TaskFilterState.All };
        taskFilterTime = index == 2 ? TaskFilterTime.Overdue : TaskFilterTime.All;
    }

    private int MobileTaskShortcut() => (taskFilterState, taskFilterTime) switch
    {
        (TaskFilterState.All, TaskFilterTime.All) => 0,
        (TaskFilterState.Active, TaskFilterTime.All) => 1,
        (TaskFilterState.Active, TaskFilterTime.Overdue) => 2,
        (TaskFilterState.Completed, TaskFilterTime.All) => 3,
        _ => -1
    };

    private void BuildMobileTaskFilters(IReadOnlyList<TaskItem> tasks, LinearLayout parent)
    {
        var card = Card(parent);
        var toggle = Button(card, taskMoreFiltersOpen ? "收起筛选" : "更多筛选", () => { taskMoreFiltersOpen = !taskMoreFiltersOpen; RefreshMobileTaskFilters(); }, false);
        toggle.ContentDescription = "展开或收起任务组合筛选";
        var allCourses = dashboardCourseOptions;
        var courses = allCourses.Where(course => course.Key.Length == 0 || taskFilterPlatform is null || course.PlatformId == taskFilterPlatform).ToArray();
        if (taskFilterCourse is not null && !courses.Any(course => course.Key == taskFilterCourse)) taskFilterCourse = null;
        var selectedCourse = allCourses.FirstOrDefault(course => course.Key == taskFilterCourse);
        var activeLabels = new List<string>();
        if (taskFilterState != TaskFilterState.All) activeLabels.Add(TaskFiltering.StateLabels[(int)taskFilterState]);
        if (taskFilterTime != TaskFilterTime.All) activeLabels.Add(TaskFiltering.TimeLabels[(int)taskFilterTime]);
        if (taskFilterPlatform is not null) activeLabels.Add(PlatformCatalog.Name(taskFilterPlatform));
        if (selectedCourse is not null && selectedCourse.Key.Length > 0) activeLabels.Add(selectedCourse.Label);
        if (taskFilterSort != TaskFilterSort.Priority) activeLabels.Add(TaskFiltering.SortLabels[(int)taskFilterSort]);
        if (activeLabels.Count > 0) Text(card, string.Join(" · ", activeLabels), 12, false, Muted);
        if (activeLabels.Count > 0 || taskQuery.Length > 0)
            Button(card, "清除筛选", () =>
            {
                ResetMobileTaskFilters();
                var search = dashboardList is null ? null : FindDashboardSearch(page);
                if (search is not null) search.Text = "";
                RefreshMobileTaskFilters();
            }, false);
        if (!taskMoreFiltersOpen) return;

        Text(card, "状态", 12, true, Muted);
        MobileFilterSpinner(card, "任务状态筛选", TaskFiltering.StateLabels, (int)taskFilterState,
            index => { taskFilterState = (TaskFilterState)index; RefreshMobileTaskFilters(); });
        Text(card, "截止时间", 12, true, Muted);
        MobileFilterSpinner(card, "截止时间筛选", TaskFiltering.TimeLabels, (int)taskFilterTime,
            index => { taskFilterTime = (TaskFilterTime)index; RefreshMobileTaskFilters(); });
        if (taskFilterTime == TaskFilterTime.Custom)
        {
            Pair(card, taskFilterFromDate is { } start ? "开始 " + start.ToString("yyyy-MM-dd") : "选择开始日期",
                () => ChooseMobileFilterDate(true), taskFilterToDate is { } end ? "结束 " + end.ToString("yyyy-MM-dd") : "选择结束日期",
                () => ChooseMobileFilterDate(false));
            Pair(card, "清空开始", () => { taskFilterFromDate = null; RefreshMobileTaskFilters(); }, "清空结束", () => { taskFilterToDate = null; RefreshMobileTaskFilters(); });
            Text(card, "按本机日期筛选截止时间，可只填一端；结束日期包含当天全天。", 12, false, Muted);
        }
        else if (taskFilterTime is TaskFilterTime.Next7Days or TaskFilterTime.Next30Days)
            Text(card, "包含今天在内的 7/30 个日历日，已逾期任务请在“已逾期”中查看。", 12, false, Muted);
        else if (taskFilterTime == TaskFilterTime.Unknown)
            Text(card, "官网未提供可用截止时间的任务；不会根据课程名称或作业状态猜测日期。", 12, false, Muted);

        var platforms = new[] { "" }.Concat(PlatformCatalog.Sites.Select(site => site.Id)).ToArray();
        Text(card, "平台", 12, true, Muted);
        MobileFilterSpinner(card, "平台筛选", platforms.Select(id => id.Length == 0 ? "全部平台" : PlatformCatalog.Name(id)).ToArray(),
            Math.Max(0, Array.IndexOf(platforms, taskFilterPlatform ?? "")), index =>
            { taskFilterPlatform = platforms[index].Length == 0 ? null : platforms[index]; taskFilterCourse = null; RefreshMobileTaskFilters(); });
        Text(card, "课程", 12, true, Muted);
        MobileFilterSpinner(card, "课程筛选", courses.Select(course => course.Label).ToArray(),
            Math.Max(0, courses.ToList().FindIndex(course => course.Key == (taskFilterCourse ?? ""))), index =>
            { taskFilterCourse = courses[index].Key.Length == 0 ? null : courses[index].Key; RefreshMobileTaskFilters(); });
        Text(card, "排序", 12, true, Muted);
        MobileFilterSpinner(card, "任务排序", TaskFiltering.SortLabels, (int)taskFilterSort,
            index => { taskFilterSort = (TaskFilterSort)index; RefreshMobileTaskFilters(); });
    }

    private void MobileFilterSpinner(LinearLayout parent, string description, IReadOnlyList<string> labels, int selected, Action<int> changed)
    {
        var spinner = new Spinner(this) { ContentDescription = description };
        var adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, labels.ToArray());
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        spinner.Adapter = adapter;
        spinner.SetMinimumHeight(Dp(48));
        spinner.SetSelection(selected, false);
        var previous = selected;
        var currentPage = page; var currentRevision = viewRevision; var filterRevision = dashboardFilterRevision;
        spinner.ItemSelected += (_, args) =>
        {
            if (!alive || !ReferenceEquals(page, currentPage) || viewRevision != currentRevision
                || dashboardFilterRevision != filterRevision || args.Position == previous) return;
            previous = args.Position; changed(args.Position);
        };
        parent.AddView(spinner, new LinearLayout.LayoutParams(-1, -2));
    }

    private static EditText? FindDashboardSearch(View view)
    {
        if (view is EditText text && text.ContentDescription == "搜索任务") return text;
        if (view is Android.Views.ViewGroup group)
            for (var index = 0; index < group.ChildCount; index++)
                if (FindDashboardSearch(group.GetChildAt(index)!) is { } search) return search;
        return null;
    }

    private void ChooseMobileFilterDate(bool from)
    {
        var initial = (from ? taskFilterFromDate : taskFilterToDate) ?? DateOnly.FromDateTime(DateTime.Today);
        var revision = viewRevision;
        var dialog = new DatePickerDialog(this, (_, args) =>
        {
            if (!alive || viewRevision != revision) return;
            var date = DateOnly.FromDateTime(args.Date);
            if (from) taskFilterFromDate = date; else taskFilterToDate = date;
            RefreshMobileTaskFilters();
        }, initial.Year, initial.Month - 1, initial.Day);
        dialog.Show();
    }
}
