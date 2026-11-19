using AccessProbe;
using Android.Views;
using Android.Widget;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private void RunCourseFilterPerformanceChecks(Action<bool, string> check)
    {
        var previousFilter = mobileCourseFilter; var previousBusy = busy;
        try
        {
            busy = false; mobileCourseFilter = CourseListFilter.HideEnded; SetMobileNotificationEditing(false);
            Layout("课程选择性能检查", "使用内存合成目录检查批量选择和旧页面回调。");
            var courses = Enumerable.Range(0, 256).Select(index => new RecordingCourse(
                CourseRecordingRules.MetadataKey("pta", "synthetic-course-performance", index.ToString(), "class"),
                $"合成课程 {index:000}", "合成教学班")
            { Lifecycle = CourseLifecycleRules.FromOfficialState(index % 8 == 0 ? CourseLifecycleState.Ended : CourseLifecycleState.Ongoing) }).ToArray();
            var originalSelection = courses.Where((_, index) => index % 8 == 0).Select(course => course.Key).ToArray();
            var settings = new CourseRecordingSettings { Courses = courses, SelectionConfirmed = true, SelectedCourseKeys = originalSelection };
            BuildRecordingCourseChoices(page, settings);
            IEnumerable<View> Descendants(View view)
            {
                yield return view;
                if (view is ViewGroup group)
                    for (var index = 0; index < group.ChildCount; index++)
                        foreach (var child in Descendants(group.GetChildAt(index)!)) yield return child;
            }
            Button Action(string label) => Descendants(page).OfType<Button>().Single(button => button.Text == label);
            check(courseFilterChoices.Count == courses.Length && courseFilterChoices.Count(choice => choice.Row.Visibility == ViewStates.Visible) == 224
                && !mobileNotificationEditing, "large course catalog hides only verified ended courses without marking initial selections dirty");
            var before = courseFilterRenderCount; Action("全选当前列表").PerformClick();
            check(courseFilterRenderCount == before + 1 && courseFilterChoices.All(choice => choice.Box.Checked) && mobileNotificationEditing,
                "bulk selecting hundreds of visible courses renders once and protects the unsaved selection");
            SetMobileNotificationEditing(false); before = courseFilterRenderCount; Action("全选当前列表").PerformClick();
            check(courseFilterRenderCount == before + 1 && !mobileNotificationEditing,
                "a bulk selection with no actual changes leaves notification editing unmarked");
            before = courseFilterRenderCount; Action("全不选当前列表").PerformClick();
            check(courseFilterRenderCount == before + 1
                && courseFilterChoices.All(choice => choice.Box.Checked == originalSelection.Contains(choice.Course.Key, StringComparer.Ordinal)),
                "bulk clearing renders once and retains every hidden prior selection");
            SetMobileNotificationEditing(false); before = courseFilterRenderCount;
            var single = courseFilterChoices.Single(choice => choice.Course.Key == courses[1].Key); single.Box.Checked = true;
            check(courseFilterRenderCount == before + 1 && mobileNotificationEditing,
                "an individual checkbox change still updates the summary and protects its draft");
            var search = Descendants(page).OfType<EditText>().Single(); search.Text = "合成课程 002";
            before = courseFilterRenderCount; Action("全选当前列表").PerformClick();
            check(courseFilterRenderCount == before + 1 && courseFilterChoices.Single(choice => choice.Course.Key == courses[2].Key).Box.Checked
                && !courseFilterChoices.Single(choice => choice.Course.Key == courses[3].Key).Box.Checked
                && courseFilterSummary!.Text?.Contains("显示 1 / 256", StringComparison.Ordinal) == true,
                "bulk selection uses the current search and lifecycle snapshot and updates only matching choices");
            check(settings.SelectedCourseKeys.SequenceEqual(originalSelection), "draft course selection does not mutate its persisted-settings input");
            var oldButton = Action("全不选当前列表"); var oldChoice = single.Box; var oldSearch = search; before = courseFilterRenderCount;
            Layout("当前页面", "旧页面事件应当失效。"); status.Text = "当前页面保持不变"; SetMobileNotificationEditing(false);
            oldSearch.Text = "旧页面搜索"; oldChoice.Checked = !oldChoice.Checked; oldButton.PerformClick();
            check(courseFilterRenderCount == before && status.Text == "当前页面保持不变" && !mobileNotificationEditing,
                "detached course search checkbox and bulk callbacks cannot change a newer page or its editing flag");
        }
        finally
        {
            busy = previousBusy; mobileCourseFilter = previousFilter; SetMobileNotificationEditing(false);
        }
    }
}
