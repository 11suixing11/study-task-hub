using Android.Views;
using Android.Widget;
using AccessProbe;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private CourseListFilter mobileCourseFilter = CourseListFilter.HideEnded;
    private Spinner? courseLifecyclePicker;
    private TextView? courseFilterSummary;
    private IReadOnlyList<MobileCourseChoice> courseFilterChoices = [];
    private int courseFilterRenderCount;

    private sealed record MobileCourseChoice(RecordingCourse Course, CheckBox Box, LinearLayout Row);

    private List<(string Key, CheckBox Box)> BuildRecordingCourseChoices(LinearLayout body, CourseRecordingSettings recording)
    {
        var originPage = page; var originRevision = viewRevision; var selectingVisible = false;
        bool IsCurrentPage() => alive && ReferenceEquals(page, originPage) && viewRevision == originRevision;
        var search = new EditText(this) { Hint = "搜索课程名称、班级或开课期", TextSize = 14, ContentDescription = "搜索课程" };
        search.SetSingleLine(true); search.SetTextColor(Ink); search.SetHintTextColor(Muted);
        search.SetMinimumHeight(Dp(48)); body.AddView(search, new LinearLayout.LayoutParams(-1, -2));
        var picker = new Spinner(this) { ContentDescription = "课程结束筛选" };
        var adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem,
            new[] { "隐藏已结束课程", "显示全部课程", "仅看已结束课程" });
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        picker.Adapter = adapter; picker.SetSelection((int)mobileCourseFilter);
        body.AddView(picker, new LinearLayout.LayoutParams(-1, Dp(48))); courseLifecyclePicker = picker;
        var summary = Text(body, "", 12, false, Muted); summary.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        courseFilterSummary = summary;
        Text(body, "筛选保留原有选择和历史任务；结束状态待核对的课程继续显示。", 12, false, Muted);
        var choices = new List<MobileCourseChoice>(); var selections = new List<(string Key, CheckBox Box)>();
        var rows = Column();
        static bool Matches(MobileCourseChoice choice, string query, CourseListFilter filter, DateTimeOffset now)
        {
            return CourseLifecycleRules.MatchesFilter(choice.Course, filter, now)
                && (query.Length == 0 || choice.Course.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || choice.Course.Detail.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        void RenderSnapshot(string query, CourseListFilter filter, DateTimeOffset now)
        {
            if (!IsCurrentPage()) return;
            courseFilterRenderCount++; var visible = 0; var ended = 0; var selected = 0;
            foreach (var choice in choices)
            {
                var matches = Matches(choice, query, filter, now); choice.Row.Visibility = matches ? ViewStates.Visible : ViewStates.Gone;
                if (matches) visible++;
                if (CourseLifecycleRules.IsEnded(choice.Course, now)) ended++;
                if (choice.Box.Checked) selected++;
            }
            summary.Text = $"显示 {visible} / {choices.Count} 门 · 已选 {selected} 门"
                + (filter == CourseListFilter.HideEnded && ended > 0 ? $" · 已自动隐藏 {ended} 门已结束课程" : ended > 0 ? $" · 已结束 {ended} 门" : "")
                + (visible == 0 && choices.Count > 0 ? "\n没有匹配的课程，可切换为显示全部课程或清除搜索。" : "");
        }
        void Render()
        {
            if (!IsCurrentPage() || selectingVisible) return;
            RenderSnapshot((search.Text ?? "").Trim(), mobileCourseFilter, DateTimeOffset.UtcNow);
        }
        void SelectVisible(bool selected)
        {
            if (!IsCurrentPage()) return;
            if (busy) { status.Text = "请等待当前同步结束。"; return; }
            var now = DateTimeOffset.UtcNow; var query = (search.Text ?? "").Trim(); var filter = mobileCourseFilter; var changed = false;
            selectingVisible = true;
            try
            {
                foreach (var choice in choices)
                {
                    if (!IsCurrentPage()) return;
                    if (Matches(choice, query, filter, now) && choice.Box.Checked != selected)
                    {
                        choice.Box.Checked = selected; changed = true;
                    }
                }
            }
            finally { selectingVisible = false; }
            if (!IsCurrentPage()) return;
            if (changed) SetMobileNotificationEditing(true);
            RenderSnapshot(query, filter, now); status.Text = "课程选择尚未保存，点击下方保存按钮生效。";
        }
        Pair(body, "全选当前列表", () => SelectVisible(true), "全不选当前列表", () => SelectVisible(false));
        body.AddView(rows);
        var courseSnapshotNow = DateTimeOffset.UtcNow;
        foreach (var course in recording.Courses)
        {
            var row = Column(); rows.AddView(row);
            var choice = new CheckBox(this)
            {
                Text = course.Name + (course.Available || course.Detail == "手动录入" ? "" : "（当前目录未找到）"),
                Checked = CourseRecordingRules.IsSelected(recording, course.Key), TextSize = 14,
                ContentDescription = "记录课程：" + course.Name
            };
            choice.SetTextColor(Ink); choice.SetMinimumHeight(Dp(48)); row.AddView(choice);
            var detail = course.Detail + (course.Detail.Length > 0 ? " · " : "") + CourseLifecycleRules.Explanation(course, courseSnapshotNow);
            Text(row, detail, 12, false, Muted);
            choices.Add(new(course, choice, row)); selections.Add((course.Key, choice));
            choice.CheckedChange += (_, _) =>
            {
                if (selectingVisible || !IsCurrentPage()) return;
                SetMobileNotificationEditing(true); Render();
            };
        }
        courseFilterChoices = choices;
        search.TextChanged += (_, _) => Render();
        picker.ItemSelected += (_, args) => { if (!IsCurrentPage()) return; mobileCourseFilter = (CourseListFilter)args.Position; Render(); };
        Render(); return selections;
    }
}
