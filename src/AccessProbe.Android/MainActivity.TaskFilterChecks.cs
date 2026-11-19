using AccessProbe;
using Android.Views;
using Android.Widget;
using System.Text.Json;
using View = Android.Views.View;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private async Task RunTaskFilterUiChecks()
    {
        var labels = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); labels.Add(label); }
        IEnumerable<View> Children(View view)
        {
            yield return view;
            if (view is ViewGroup group)
                for (var i = 0; i < group.ChildCount; i++)
                    foreach (var child in Children(group.GetChildAt(i)!)) yield return child;
        }
        Spinner Select(string description) => Children(page).OfType<Spinner>().Single(view => view.ContentDescription == description);
        Button Action(string label) => label == "清除筛选"
            // Both the filter card and empty-state card offer the same reset action.
            ? Children(page).OfType<Button>().First(view => view.Text == label)
            : Children(page).OfType<Button>().Single(view => view.Text == label);
        bool Has(string title) => dashboardAdapter is not null
            && Enumerable.Range(0, dashboardAdapter.Count).Any(index => dashboardAdapter[index].Title == title)
            || Children(page).OfType<TextView>().Any(view => view.Text == title);
        async Task Settled()
        {
            await dashboardLoad; await dashboardRender; await Task.Delay(70);
        }
        var now = DateTimeOffset.UtcNow; var today = now.ToLocalTime().Date;
        var marker = "筛选独立" + Guid.NewGuid().ToString("N")[..8];
        var created = new List<TaskItem>();
        TaskItem Create(string title, string course, string platform, int day, TaskState state = TaskState.Pending)
        {
            var deadline = new DateTimeOffset(DateTime.SpecifyKind(today.AddDays(day).AddHours(23), DateTimeKind.Local));
            var task = store.Create(new(marker + title, platform, marker + course, DeadlineKind.Known, null, deadline, null, null, "", "", null), now);
            if (state != TaskState.Pending) task = store.SetState(task.Id, task.Revision, state, now);
            created.Add(task); return task;
        }
        var completed = Create("已完成", "A", "changjiang", -1, TaskState.Completed);
        var a = Create("今天A", "A", "changjiang", 0);
        var b = Create("今天B", "B", "changjiang", 0);
        var future = Create("未来", "A", "changjiang", 7);
        var other = Create("其他平台", "A", "mooc", 0);
        try
        {
            ResetMobileTaskFilters(); taskMoreFiltersOpen = false; taskQuery = marker; Dashboard(); await Settled();
            Check(Has(a.Title) && Has(b.Title) && Has(completed.Title) && !Children(page).OfType<Spinner>().Any(),
                "mobile collapsed filters preserve task cards and keep advanced controls hidden");
            Action("更多筛选").PerformClick(); await Settled();
            Check(Children(page).OfType<Spinner>().Count() == 5, "mobile more filters exposes independent state time platform course and sort controls");
            Select("任务状态筛选").SetSelection((int)TaskFilterState.Completed); await Settled();
            Select("截止时间筛选").SetSelection((int)TaskFilterTime.Overdue); await Settled();
            Check(Has(completed.Title) && !Has(a.Title) && !Has(b.Title), "mobile completed and overdue filters combine independently");
            Select("任务状态筛选").SetSelection((int)TaskFilterState.All); await Settled();
            Select("截止时间筛选").SetSelection((int)TaskFilterTime.Today); await Settled();
            Check(Has(a.Title) && Has(b.Title) && Has(other.Title) && !Has(future.Title) && !Has(completed.Title),
                "mobile today follows local date while preserving all matching platforms");
            var platformIndex = PlatformCatalog.Sites.ToList().FindIndex(site => site.Id == "changjiang") + 1;
            Select("平台筛选").SetSelection(platformIndex); await Settled();
            Check(Has(a.Title) && Has(b.Title) && !Has(other.Title), "mobile platform composes with time and search");
            var courses = TaskFiltering.Courses(store.LoadTasks().Where(task => CourseRecordingRules.IsVisible(store.LoadCourseRecording(task.PlatformId), task)), "changjiang");
            Select("课程筛选").SetSelection(courses.ToList().FindIndex(course => course.Label.StartsWith(marker + "B", StringComparison.Ordinal))); await Settled();
            Check(Has(b.Title) && !Has(a.Title), "mobile course identity composes with platform time state and search");
            Select("平台筛选").SetSelection(0); await Settled();
            Check(Select("课程筛选").SelectedItemPosition == 0, "mobile switching platform clears unavailable course selection");
            Select("截止时间筛选").SetSelection((int)TaskFilterTime.Custom); await Settled();
            Check(Has("选择开始日期") && Has("选择结束日期") && Has("请选择开始或结束日期，可只填写一端。"),
                "mobile custom date controls request a date and expose clear feedback");
            taskFilterFromDate = DateOnly.FromDateTime(today); taskFilterToDate = DateOnly.FromDateTime(today); Dashboard(); await Settled();
            Check(Has(a.Title) && Has(b.Title) && Has(other.Title) && !Has(future.Title), "mobile same-day custom range includes deadlines throughout final day");
            taskFilterFromDate = DateOnly.FromDateTime(today.AddDays(1)); Dashboard(); await Settled();
            Check(Has("开始日期不能晚于结束日期。") && !Has(a.Title) && !Has(future.Title), "mobile reversed range explains invalid input without showing all tasks");
            Action("清空开始").PerformClick(); await Settled();
            Check(Has(completed.Title) && Has(a.Title) && !Has(future.Title), "mobile end-only range remains usable");
            Action("清除筛选").PerformClick(); await Settled();
            Check(taskFilterState == TaskFilterState.All && taskFilterTime == TaskFilterTime.All && taskFilterPlatform is null
                && taskFilterCourse is null && taskFilterFromDate is null && taskFilterToDate is null && taskQuery.Length == 0,
                "mobile clear resets all independent filters and date controls");
            taskMoreFiltersOpen = false; Dashboard(); await Settled();
            Check(Has("更多筛选") && !Children(page).OfType<Spinner>().Any(), "mobile filters collapse again while keeping main screen available");
            var directory = System.IO.Path.Combine(MobileData.Root(this), "synthetic-improvement-ui-test");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "task-filter-checks.json"), JsonSerializer.Serialize(new
            { schema = 1, outcome = "passed", checks = labels.Count, results = labels, officialNetworkAccessed = false, productionDataAccessed = false }));
        }
        finally
        {
            foreach (var task in created) { var current = store.Get(task.Id); if (current is not null) store.Delete(current.Id, current.Revision); }
            ResetMobileTaskFilters(); taskMoreFiltersOpen = false; Dashboard();
        }
    }
}
