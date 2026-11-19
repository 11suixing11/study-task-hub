using Android.Views;
using Android.Widget;
using AccessProbe;
using System.Text.Json;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private async Task RunCourseFilterUiChecks()
    {
        var directory = System.IO.Path.Combine(MobileData.Root(this), "synthetic-course-filter-test");
        Directory.CreateDirectory(directory); var checks = new List<string>();
        void Check(bool condition, string label) { if (!condition) throw new InvalidDataException(label); checks.Add(label); }
        void Report(string outcome, string? error = null) => File.WriteAllText(System.IO.Path.Combine(directory, "ui-checks.json"), JsonSerializer.Serialize(new
        {
            schema = 1, outcome, checks = checks.Count, results = checks, error,
            officialNetworkAccessed = false, productionDataAccessed = false, realAccountAcceptance = "pending"
        }));
        IEnumerable<View> Descendants(View root)
        {
            yield return root;
            if (root is ViewGroup group)
                for (var index = 0; index < group.ChildCount; index++)
                    foreach (var view in Descendants(group.GetChildAt(index)!)) yield return view;
        }
        Button Action(string label) => Descendants(page).OfType<Button>().Single(button => button.Text == label);
        async Task SetFilter(CourseListFilter filter)
        {
            courseLifecyclePicker!.SetSelection((int)filter);
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (mobileCourseFilter == filter) { await Task.Delay(80); return; }
                await Task.Delay(40);
            }
            throw new TimeoutException("Course filter selection was not applied.");
        }
        try
        {
            var protector = new AndroidDataProtector("LearningTaskAggregator.SyntheticCourseFilter.v1");
            store = new TaskStore(System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N"), "tasks.db"), protector);
            vault = new(store, protector); sync = new(store, vault, _ => new OfflineUiHandler()); store.SaveSettings(new(false, false));
            var now = DateTimeOffset.UtcNow;
            var ended = CourseRecordingRules.Metadata("uai", "9001", new("101", "合成已结束课程", "class", "官网课程结束日期") { EndUtc = now.AddDays(-1) });
            var unknown = CourseRecordingRules.Metadata("uai", "9001", new("102", "合成未知结束课程", "class", "结束日期未提供"));
            var upcoming = CourseRecordingRules.Metadata("uai", "9001", new("103", "合成未来结束课程", "class", "官网课程结束日期") { EndUtc = now.AddDays(30) });
            var catalog = store.ObserveRecordingCourses("uai", [ended, unknown, upcoming], true, now);
            store.SaveCourseRecording("uai", false, [ended.Course.Key], true, catalog.Revision, now);
            mobileCourseFilter = CourseListFilter.HideEnded; Platform("uai"); await mobilePageLoad; await Task.Delay(120);
            Check(courseFilterChoices.Count == 3 && courseFilterChoices.Count(choice => choice.Row.Visibility == ViewStates.Visible) == 2, "default filter hides only verified ended course");
            Check(courseFilterChoices.Single(choice => choice.Course.Key == unknown.Course.Key).Row.Visibility == ViewStates.Visible, "unknown ended status stays visible");
            Check(courseFilterSummary!.Text?.Contains("隐藏 1 门", StringComparison.Ordinal) == true, "hidden ended count is explained");
            Check(courseFilterChoices.Single(choice => choice.Course.Key == ended.Course.Key).Box.Checked, "hidden prior selected course remains checked");
            var before = JsonSerializer.Serialize(store.LoadCourseRecording("uai"));
            Action("全选当前列表").PerformClick();
            Check(courseFilterChoices.All(choice => choice.Box.Checked), "select visible keeps hidden prior selection");
            Check(JsonSerializer.Serialize(store.LoadCourseRecording("uai")) == before, "bulk selection remains an unsaved draft");
            Action("保存手动课程范围").PerformClick();
            await mobileCourseSave;
            Check(store.LoadCourseRecording("uai").SelectedCourseKeys.Count == 3 && store.LoadCourseRecording("uai").SelectedCourseKeys.Contains(ended.Course.Key), "save retains hidden course and visible choices");
            await SetFilter(CourseListFilter.All);
            Check(courseFilterChoices.All(choice => choice.Row.Visibility == ViewStates.Visible), "show all restores ended course");
            await SetFilter(CourseListFilter.EndedOnly);
            Check(courseFilterChoices.Count(choice => choice.Row.Visibility == ViewStates.Visible) == 1
                && courseFilterChoices.Single(choice => choice.Row.Visibility == ViewStates.Visible).Course.Key == ended.Course.Key, "ended-only selects trustworthy ended course");
            await SetFilter(CourseListFilter.HideEnded);
            Action("全不选当前列表").PerformClick();
            Check(courseFilterChoices.Single(choice => choice.Course.Key == ended.Course.Key).Box.Checked
                && courseFilterChoices.Where(choice => choice.Course.Key != ended.Course.Key).All(choice => !choice.Box.Checked), "clear visible cannot deselect hidden course");
            Action("保存手动课程范围").PerformClick();
            await mobileCourseSave;
            Check(store.LoadCourseRecording("uai").SelectedCourseKeys.SequenceEqual(new[] { ended.Course.Key }), "save after clearing visible still preserves hidden selection");
            Check(store.LoadTasks().Count == 0 && vault.Load("uai") is null, "course filtering performs no account or task writes");
            var search = Descendants(page).OfType<EditText>().Single(); search.Text = "不存在";
            Check(courseFilterChoices.All(choice => choice.Row.Visibility == ViewStates.Gone)
                && courseFilterSummary!.Text?.Contains("没有匹配", StringComparison.Ordinal) == true, "course search combines with lifecycle filter and explains empty match");
            search.Text = "";
            Check(courseFilterChoices.Count(choice => choice.Row.Visibility == ViewStates.Visible) == 2, "clearing course search retains ended filter");
            var settings = store.LoadCourseRecording("uai");
            store.ObserveRecordingCourses("uai", [ended with { Course = ended.Course with { Lifecycle = new() } }, unknown, upcoming], true, now.AddMinutes(1));
            Platform("uai"); await mobilePageLoad; await Task.Delay(100);
            Check(courseFilterChoices.All(choice => choice.Row.Visibility == ViewStates.Visible) && store.LoadCourseRecording("uai").SelectedCourseKeys.SequenceEqual(settings.SelectedCourseKeys),
                "fresh unknown lifecycle restores course without losing selection");
            Report("passed");
        }
        catch (Exception error) { Report("failed", error.GetType().Name + ": " + error.Message); }
    }
}
