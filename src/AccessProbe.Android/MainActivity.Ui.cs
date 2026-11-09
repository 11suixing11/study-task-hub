using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Content.Res;
using Android.Views;
using Android.Widget;
using AccessProbe;
using Color = Android.Graphics.Color;
using View = Android.Views.View;
using Orientation = Android.Widget.Orientation;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private static readonly Color Accent = Color.Rgb(49, 91, 232);
    private static readonly Color Ink = Color.Rgb(32, 38, 50);
    private static readonly Color Muted = Color.Rgb(103, 113, 128);
    private static readonly Color Canvas = Color.Rgb(245, 246, 248);
    private static readonly Color Line = Color.Rgb(229, 232, 238);
    private string rootPage = "tasks";
    private Action? pageBack;
    private string taskQuery = "";
    private int taskFilter;

    // Explicit emulator-only entry: never opens the normal task store or starts scheduled work.
    private void OpenSyntheticUi()
    {
        var directory = System.IO.Path.Combine(MobileData.Root(this), "synthetic-ui-test");
        System.IO.Directory.CreateDirectory(directory);
        var protector = new AndroidDataProtector("LearningTaskAggregator.SyntheticUi.v1");
        store = new TaskStore(System.IO.Path.Combine(directory, "tasks.db"), protector); vault = new(store, protector);
        sync = new(store, vault, _ => new OfflineUiHandler()); store.SaveSettings(new(false, false));
        if (store.LoadTasks().Count > 0) return;
        var now = DateTimeOffset.UtcNow;
        var samples = new[] { ("数据结构 · 栈与队列", "pta", "数据结构", -6), ("大学英语 · Unit 4", "ucampus", "大学英语", 8), ("高等数学 · 极限练习", "chaoxing", "高等数学", 22), ("人工智能 · 课程测验", "zhihuishu", "人工智能导论", 48), ("线性代数 · 矩阵运算", "mooc", "线性代数", 72) };
        foreach (var (title, platform, course, hours) in samples)
        {
            var task = store.Create(new(title, platform, course, DeadlineKind.Known, null, now.AddHours(hours), null, null, "", "独立界面验收数据", null), now);
            if (platform == "mooc") store.SetState(task.Id, task.Revision, TaskState.Completed, now);
        }
        var first = CourseRecordingRules.MetadataKey("pta", "ui-synthetic", "101", "class");
        var second = CourseRecordingRules.MetadataKey("pta", "ui-synthetic", "102", "class");
        var catalog = store.ObserveRecordingCourses("pta", [new(new(first, "数据结构", "2026 秋季 · 教学课程"), _ => false), new(new(second, "程序设计基础", "2026 秋季 · 教学课程"), _ => false)], true, now);
        store.SaveCourseRecording("pta", false, [first], true, catalog.Revision, now);
    }
    private sealed class OfflineUiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    private int Dp(float value) => (int)(value * Resources!.DisplayMetrics!.Density + .5f);
    private LinearLayout Column() => new(this) { Orientation = Orientation.Vertical };
    private LinearLayout Row(LinearLayout parent)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal }; row.SetGravity(GravityFlags.CenterVertical);
        parent.AddView(row, new LinearLayout.LayoutParams(-1, -2)); return row;
    }
    private GradientDrawable Surface(Color color, int radius = 12, bool border = false)
    {
        var shape = new GradientDrawable(); shape.SetColor(color); shape.SetCornerRadius(Dp(radius));
        if (border) shape.SetStroke(Dp(1), Line); return shape;
    }
    private void Layout(string title, string subtitle, Action? back = null, bool navigation = false, Action? toolbarAction = null, string? toolbarLabel = null)
    {
        viewRevision++; pageBack = back;
        var root = Column(); root.SetBackgroundColor(Canvas); root.FocusableInTouchMode = true;
        var scroll = new ScrollView(this) { FillViewport = true }; page = Column(); page.SetPadding(Dp(20), Dp(16), Dp(20), Dp(24));
        scroll.AddView(page); root.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        if (back is not null)
        {
            var backButton = Button(page, "‹ 返回", back, false);
            backButton.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { BottomMargin = Dp(4) };
            backButton.Background = Surface(Color.Transparent, 8); backButton.SetPadding(0, Dp(8), Dp(16), Dp(8));
        }
        var heading = Row(page); var titleView = Text(heading, title, 28, true);
        titleView.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        if (toolbarAction is not null)
        {
            var action = Button(heading, toolbarLabel ?? "操作", toolbarAction, false);
            action.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(12) };
        }
        Text(page, subtitle, 12, false, Muted);
        status = Text(page, "", 13, false, Accent);
        status.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        if (navigation) Navigation(root);
        SetContentView(root); root.RequestFocus();
        root.SetOnApplyWindowInsetsListener(new SystemInsetsListener()); root.RequestApplyInsets();
    }
    private void Navigation(LinearLayout parent)
    {
        var line = new View(this); line.SetBackgroundColor(Line); parent.AddView(line, new LinearLayout.LayoutParams(-1, Dp(1)));
        var row = Row(parent); row.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6)); row.SetBackgroundColor(Color.White);
        foreach (var (key, label, action) in new (string, string, Action)[] { ("tasks", "任务", Dashboard), ("platforms", "平台", PlatformsPage), ("settings", "设置", SettingsPage) })
        {
            var button = Button(row, label, action, false); button.Selected = rootPage == key;
            button.SetTextColor(button.Selected ? Accent : Muted);
            button.Background = new RippleDrawable(ColorStateList.ValueOf(Color.Rgb(220, 228, 252)), Surface(button.Selected ? Color.Rgb(238, 243, 255) : Color.White, 10), null);
            button.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1) { LeftMargin = Dp(4), RightMargin = Dp(4) };
            button.ContentDescription = label + (button.Selected ? "，当前页面" : "页面");
        }
    }
    private TextView Text(LinearLayout parent, string value, float size = 15, bool bold = false, Color? color = null)
    {
        var text = new TextView(this) { Text = value, TextSize = size }; text.SetTextColor(color ?? Ink);
        if (bold) text.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        text.SetPadding(0, Dp(4), 0, Dp(4)); text.SetLineSpacing(0, 1.08f);
        text.Visibility = string.IsNullOrWhiteSpace(value) ? ViewStates.Gone : ViewStates.Visible;
        text.TextChanged += (_, _) => text.Visibility = string.IsNullOrWhiteSpace(text.Text) ? ViewStates.Gone : ViewStates.Visible;
        parent.AddView(text, new LinearLayout.LayoutParams(-1, -2)); return text;
    }
    private LinearLayout Card(LinearLayout parent)
    {
        var card = Column(); card.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12)); card.Background = Surface(Color.White, 14, true);
        parent.AddView(card, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(10), TopMargin = Dp(6) }); return card;
    }
    private Button Button(LinearLayout parent, string label, Action action, bool primary = true)
    {
        var button = new Button(this) { Text = label, TextSize = 14 }; button.SetAllCaps(false); button.SetTextColor(primary ? Color.White : Accent);
        button.SetMinimumHeight(Dp(48)); button.SetMinHeight(Dp(48)); button.SetMinimumWidth(0); button.SetMinWidth(0);
        button.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        button.Background = new RippleDrawable(ColorStateList.ValueOf(primary ? Color.Rgb(73, 112, 247) : Color.Rgb(220, 228, 252)), Surface(primary ? Accent : Color.Rgb(238, 243, 255), 10), null);
        button.Click += (_, _) => { if (alive) action(); };
        parent.AddView(button, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(4), BottomMargin = Dp(4) }); return button;
    }
    private void Pair(LinearLayout parent, string left, Action first, string right, Action second, bool primary = false)
    {
        var row = Row(parent); var a = Button(row, left, first, primary); var b = Button(row, right, second, false);
        a.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1) { RightMargin = Dp(6), TopMargin = Dp(4), BottomMargin = Dp(4) };
        b.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1) { LeftMargin = Dp(6), TopMargin = Dp(4), BottomMargin = Dp(4) };
    }
    private async Task SyncAll()
    {
        if (busy) { status.Text = "已有同步正在运行。"; return; }
        var revision = viewRevision;
        var connected = MobilePlatforms.All.Where(id => vault.Load(id) is not null).ToArray();
        if (connected.Length == 0) { status.Text = "请先在“平台”页面登录并选择课程。"; return; }
        foreach (var platform in connected)
        {
            if (revision != viewRevision || taskEditorOpen || web is not null) break;
            await RunSync(platform, false, false);
        }
        RefreshSyncView(revision);
    }
    private void Dashboard()
    {
        taskEditorOpen = false; selectedPlatform = null; rootPage = "tasks";
        Layout("学习任务", "聚合平台 · 各平台任务集中查看", navigation: true, toolbarAction: async () => await SyncAll(), toolbarLabel: "同步");
        var tasks = store.LoadTasks().Where(task => CourseRecordingRules.IsVisible(store.LoadCourseRecording(task.PlatformId), task)).ToArray();
        var now = DateTimeOffset.UtcNow; var summary = Card(page); summary.Background = Surface(Accent, 16);
        var main = Row(summary); var count = Text(main, tasks.Count(TaskRules.IsActive).ToString(), 38, true, Color.White);
        count.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { RightMargin = Dp(12) };
        Text(main, "需要处理", 15, true, Color.White);
        var metrics = Row(summary);
        foreach (var (label, total) in new[] { ("已逾期", tasks.Count(task => TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc < now)), ("24 小时内", tasks.Count(task => TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc >= now && task.DeadlineUtc <= now.AddHours(24))), ("已完成", tasks.Count(task => task.State == TaskState.Completed)) })
        {
            var text = Text(metrics, total + "  " + label, 12, false, Color.Rgb(226, 234, 255));
            text.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        }
        var search = new EditText(this) { Text = taskQuery, Hint = "搜索任务、课程或平台", TextSize = 14, ContentDescription = "搜索任务" };
        search.SetSingleLine(true); search.SetTextColor(Ink); search.SetHintTextColor(Muted); search.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
        search.Background = Surface(Color.White, 10, true); search.SetMinimumHeight(Dp(48));
        page.AddView(search, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(2), BottomMargin = Dp(6) });
        var filters = Row(page); var tabs = new List<Button>();
        var listHeading = Row(page); var totalText = Text(listHeading, "任务清单", 17, true); totalText.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        var add = Button(listHeading, "＋ 新增", AddTask, false); add.LayoutParameters = new LinearLayout.LayoutParams(-2, -2); add.Background = Surface(Color.Transparent);
        var list = Column(); page.AddView(list);
        void RenderTasks()
        {
            list.RemoveAllViews();
            for (var i = 0; i < tabs.Count; i++)
            {
                tabs[i].Selected = i == taskFilter; tabs[i].SetTextColor(i == taskFilter ? Accent : Muted);
                tabs[i].Background = Surface(i == taskFilter ? Color.Rgb(226, 234, 255) : Color.Transparent, 8);
            }
            var visible = tasks.Where(task => taskFilter switch { 1 => TaskRules.IsActive(task), 2 => TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc < now, 3 => task.State == TaskState.Completed, _ => true })
                .Where(task => new[] { task.Title, task.CourseName, PlatformCatalog.Name(task.PlatformId), task.Notes }.Any(value => value.Contains(taskQuery.Trim(), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(task => TaskRules.Priority(task, now)).ThenBy(task => task.DeadlineUtc).ToArray();
            totalText.Text = "任务清单 · " + visible.Length;
            if (visible.Length == 0)
            {
                var empty = Card(list); Text(empty, tasks.Length == 0 ? "从一门课程开始" : "没有匹配的任务", 18, true);
                Text(empty, tasks.Length == 0 ? "登录平台，选择要记录的课程，任务会自动汇总到这里。" : "试试其他状态，或修改搜索内容。", 14, false, Muted);
                Button(empty, tasks.Length == 0 ? "前往平台管理" : "清除筛选", () => { if (tasks.Length == 0) PlatformsPage(); else { taskFilter = 0; taskQuery = ""; search.Text = ""; RenderTasks(); } }, false);
            }
            foreach (var task in visible)
            {
                var card = Card(list); card.Clickable = true; card.Focusable = true; card.Click += (_, _) => TaskDetail(task.Id);
                var titleRow = Row(card); var title = Text(titleRow, task.Title, 16, true); title.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
                var arrow = Text(titleRow, "›", 22, false, Muted); arrow.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(12) };
                Text(card, PlatformCatalog.Name(task.PlatformId) + " · " + (task.CourseName.Length == 0 ? "课程待补充" : task.CourseName), 12, false, Muted);
                var metadata = Row(card); var state = Text(metadata, StateText(task.State), 12, true, task.State == TaskState.Completed ? Color.Rgb(45, 124, 91) : Accent);
                state.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { RightMargin = Dp(12) };
                var overdue = TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc < now;
                Text(metadata, (overdue ? "已逾期 · " : "") + Deadline(task), 12, false, overdue ? Color.Rgb(182, 65, 67) : Muted);
            }
        }
        foreach (var label in new[] { "全部", "待办", "逾期", "已完成" })
        {
            var index = tabs.Count; var tab = Button(filters, label, () => { taskFilter = index; RenderTasks(); }, false); tab.TextSize = 13;
            tab.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1) { RightMargin = Dp(4) }; tabs.Add(tab);
        }
        search.TextChanged += (_, _) => { taskQuery = search.Text ?? ""; RenderTasks(); }; RenderTasks();
    }
    private void PlatformsPage()
    {
        taskEditorOpen = false; selectedPlatform = null; rootPage = "platforms";
        Layout("我的平台", "先登录，再选择需要记录的课程。", navigation: true);
        var tasks = store.LoadTasks();
        foreach (var platform in MobilePlatforms.All)
        {
            var card = Card(page); var recording = store.LoadCourseRecording(platform);
            card.Clickable = true; card.Focusable = true; card.Click += (_, _) => Platform(platform);
            var row = Row(card); var name = Text(row, PlatformCatalog.Name(platform), 17, true); name.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
            var arrow = Text(row, "›", 22, false, Muted); arrow.LayoutParameters = new LinearLayout.LayoutParams(-2, -2);
            Text(card, "已选 " + recording.Courses.Count(course => CourseRecordingRules.IsSelected(recording, course.Key)) + " 门 · " + tasks.Count(task => task.PlatformId == platform && CourseRecordingRules.IsVisible(recording, task)) + " 条任务", 12, false, Muted);
            Text(card, store.LoadPlatformSessionInfo(platform).Description, 12, false, Muted);
        }
    }
    private void SettingsPage()
    {
        taskEditorOpen = false; selectedPlatform = null; rootPage = "settings";
        Layout("设置", "同步与提醒，按你的节奏。", navigation: true);
        var settings = Card(page); Text(settings, "自动同步", 18, true);
        var automatic = new Switch(this) { Text = "自动同步已选课程", Checked = store.LoadSettings().AutomaticSyncEnabled, TextSize = 14 };
        automatic.SetPadding(0, Dp(12), 0, Dp(12));
        automatic.CheckedChange += (_, args) => { store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = args.IsChecked }); ScheduleReminders(); }; settings.AddView(automatic);
        Text(settings, "应用在前台时每 30 分钟检查一次，使用已保存的登录状态。", 13, false, Muted);
        var reminders = Card(page); Text(reminders, "任务提醒", 18, true);
        Text(reminders, "提醒依据已确认的截止时间。后台运行由手机系统调度，可能延迟。", 14, false, Muted);
        var account = Card(page); Text(account, "登录与课程", 18, true);
        Text(account, "每个平台保存当前登录。登录过期时，在平台管理中重新登录即可更新。", 14, false, Muted);
        Button(account, "管理平台与课程", PlatformsPage, false);
    }
    private void Platform(string platform)
    {
        taskEditorOpen = false; selectedPlatform = platform; rootPage = "platforms";
        Layout(PlatformCatalog.Name(platform), "登录、选课与同步", PlatformsPage);
        var controls = Card(page); Text(controls, "平台连接", 17, true); Text(controls, store.LoadPlatformSessionInfo(platform).Description, 13, false, Muted);
        Pair(controls, "连接平台", async () => await Login(platform), "刷新课程", async () => await RunSync(platform, true), true);
        Pair(controls, "同步已选课程", async () => await RunSync(platform, false), "停止同步", () => { if (work is null) { status.Text = "当前没有运行中的同步。"; return; } work.Cancel(); status.Text = "正在停止…"; });
        var recording = store.LoadCourseRecording(platform); var courses = Card(page); var body = Column();
        var courseHeading = Row(courses); var title = Text(courseHeading, "课程范围 · " + recording.Courses.Count, 18, true); title.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        var expanded = true; Button? toggle = null;
        toggle = Button(courseHeading, "收起", () => { expanded = !expanded; body.Visibility = expanded ? ViewStates.Visible : ViewStates.Gone; toggle!.Text = expanded ? "收起" : "展开"; }, false);
        toggle.LayoutParameters = new LinearLayout.LayoutParams(-2, -2); toggle.Background = Surface(Color.Transparent); courses.AddView(body);
        Text(body, CourseRecordingRules.CatalogRefreshText(recording), 12, false, Muted);
        var selections = new List<(string Key, CheckBox Box)>();
        foreach (var course in recording.Courses)
        {
            var choice = new CheckBox(this) { Text = course.Name + (course.Available ? "" : "（当前目录未找到）"), Checked = CourseRecordingRules.IsSelected(recording, course.Key), TextSize = 14 };
            choice.SetTextColor(Ink); choice.SetMinimumHeight(Dp(48)); body.AddView(choice); selections.Add((course.Key, choice));
            if (course.Detail.Length > 0) Text(body, course.Detail, 12, false, Muted);
        }
        if (recording.Courses.Count == 0) Text(body, "完成官方登录后，刷新课程列表。", 14, false, Muted);
        Button(body, "保存选择并采集", async () =>
        {
            if (busy) { status.Text = "请等待当前同步结束。"; return; }
            try { store.SaveCourseRecording(platform, false, selections.Where(item => item.Box.Checked).Select(item => item.Key).ToArray(), true, recording.Revision, DateTimeOffset.UtcNow); await RunSync(platform, false); }
            catch (TaskValidationException error) { status.Text = error.Message; }
        });
        Text(page, PlatformCatalog.Sites.First(site => site.Id == platform).Status, 12, false, Muted);
    }
}
