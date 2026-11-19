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
    private ListView? dashboardList;
    private MobileTaskListAdapter? dashboardAdapter;
    private IReadOnlyList<TaskItem> dashboardTasks = [];
    private IReadOnlyList<TaskCourseFilterOption> dashboardCourseOptions = [];
    private LinearLayout? dashboardFilterHost;
    private Action? renderDashboardTasks;
    private Action<MobileDashboardSnapshot>? applyDashboardSnapshot;
    private int dashboardRequest;
    private int dashboardRenderRequest;
    private CancellationTokenSource? dashboardSearchDelay;
    private Task dashboardLoad = Task.CompletedTask;
    private Task dashboardRender = Task.CompletedTask;
    private Task mobilePageLoad = Task.CompletedTask;
    private Task mobileCourseSave = Task.CompletedTask;
    private int dashboardFilterRevision;
    private bool syncAllChecking;
    private Action? dashboardDataChanged;
    private TaskStore? mobileObservedStore;
    private CancellationTokenSource? mobileDataRefreshDelay;
    private CancellationTokenSource? dashboardTimeRefresh;
    private int savedDashboardPosition;
    private int savedDashboardTop;

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
        ClearDashboardView(); mobileShownTaskId = null;
        navigationLifetime?.Cancel();
        ResetExitConfirmation();
        viewRevision++; pageBack = back;
        var root = Column(); root.SetBackgroundColor(Canvas); root.FocusableInTouchMode = true;
        var scroll = new ScrollView(this) { FillViewport = true }; page = Column(); page.SetPadding(Dp(20), Dp(16), Dp(20), Dp(24));
        scroll.AddView(page); root.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        if (back is not null)
        {
            var backButton = Button(page, "‹ 返回", HandleSystemBack, false);
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
        SchedulePendingMobileNotificationTask();
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
        var expectedRevision = viewRevision; var originPage = page;
        var button = new Button(this) { Text = label, TextSize = 14 }; button.SetAllCaps(false); button.SetTextColor(primary ? Color.White : Accent);
        button.SetMinimumHeight(Dp(48)); button.SetMinHeight(Dp(48)); button.SetMinimumWidth(0); button.SetMinWidth(0);
        button.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        button.Background = new RippleDrawable(ColorStateList.ValueOf(primary ? Color.Rgb(73, 112, 247) : Color.Rgb(220, 228, 252)), Surface(primary ? Accent : Color.Rgb(238, 243, 255), 10), null);
        button.Click += (_, _) => { if (alive && viewRevision == expectedRevision && ReferenceEquals(page, originPage)) action(); };
        parent.AddView(button, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(4), BottomMargin = Dp(4) }); return button;
    }
    private void Pair(LinearLayout parent, string left, Action first, string right, Action second, bool primary = false)
    {
        var row = Row(parent); var a = Button(row, left, first, primary); var b = Button(row, right, second, false);
        a.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1) { RightMargin = Dp(6), TopMargin = Dp(4), BottomMargin = Dp(4) };
        b.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1) { LeftMargin = Dp(6), TopMargin = Dp(4), BottomMargin = Dp(4) };
    }
    private void ClearDashboardView()
    {
        dashboardRequest++; dashboardRenderRequest++;
        dashboardSearchDelay?.Cancel(); dashboardSearchDelay?.Dispose(); dashboardSearchDelay = null;
        dashboardTimeRefresh?.Cancel(); dashboardTimeRefresh?.Dispose(); dashboardTimeRefresh = null;
        renderDashboardTasks = null; applyDashboardSnapshot = null; dashboardDataChanged = null;
        if (dashboardList is not null)
        {
            savedDashboardPosition = dashboardList.FirstVisiblePosition;
            savedDashboardTop = dashboardList.GetChildAt(0)?.Top ?? 0;
        }
        dashboardList = null; dashboardAdapter = null; dashboardFilterHost = null;
        dashboardTasks = []; dashboardCourseOptions = [];
    }
    private void LayoutDashboard()
    {
        ClearDashboardView(); mobileShownTaskId = null; navigationLifetime?.Cancel(); ResetExitConfirmation();
        viewRevision++; pageBack = null;
        var root = Column(); root.SetBackgroundColor(Canvas); root.FocusableInTouchMode = true;
        page = Column(); page.SetPadding(Dp(20), Dp(16), Dp(20), Dp(12));
        dashboardList = new ListView(this) { Divider = null, DividerHeight = 0, CacheColorHint = Color.Transparent };
        dashboardList.AddHeaderView(page, null, false);
        dashboardAdapter = new MobileTaskListAdapter(this, id => { if (alive && rootPage == "tasks" && pageBack is null) TaskDetail(id); });
        dashboardList.Adapter = dashboardAdapter;
        root.AddView(dashboardList, new LinearLayout.LayoutParams(-1, 0, 1));
        var heading = Row(page); var title = Text(heading, "学习任务", 28, true);
        title.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        var action = Button(heading, "同步", async () => await SyncAll(), false);
        action.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(12) };
        Text(page, "聚合平台 · 各平台任务集中查看", 12, false, Muted);
        status = Text(page, "", 13, false, Accent); status.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        Navigation(root); SetContentView(root); root.RequestFocus();
        root.SetOnApplyWindowInsetsListener(new SystemInsetsListener()); root.RequestApplyInsets();
    }
    private void RebuildDashboardFilters()
    {
        if (dashboardFilterHost is null) return;
        dashboardFilterRevision++;
        dashboardFilterHost.RemoveAllViews();
        BuildMobileTaskFilters(dashboardTasks, dashboardFilterHost);
    }
    private async Task ApplyCurrentDashboardSnapshotAsync(int expectedRevision, int expectedRequest,
        Action<MobileDashboardSnapshot> apply, MobileUiData data)
    {
        while (alive && viewRevision == expectedRevision && dashboardRequest == expectedRequest)
        {
            var snapshot = await data.LoadDashboardAsync();
            if (!alive || viewRevision != expectedRevision || dashboardRequest != expectedRequest) return;
            if (data.IsCurrent(snapshot)) { apply(snapshot); await dashboardRender; return; }
            await Task.Delay(40);
        }
    }
    private void ReloadDashboardSnapshot()
    {
        if (dashboardList is null || applyDashboardSnapshot is null) return;
        var expectedRevision = viewRevision; var expectedRequest = dashboardRequest;
        var apply = applyDashboardSnapshot; var data = GetMobileUiData();
        dashboardLoad = ReloadAsync();
        async Task ReloadAsync()
        {
            try
            {
                await ApplyCurrentDashboardSnapshotAsync(expectedRevision, expectedRequest, apply, data);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (alive && viewRevision == expectedRevision && dashboardRequest == expectedRequest)
                    status.Text = error is TaskValidationException validation ? validation.Message : "任务刷新未完成，请稍后重试。";
            }
        }
    }
    private async Task SyncAll()
    {
        if (busy || syncAllChecking) { status.Text = "已有同步正在运行。"; return; }
        syncAllChecking = true;
        var revision = viewRevision;
        try
        {
            var connected = await Task.Run(() => MobilePlatforms.All.Where(id => vault.LoadForRevalidation(id) is not null).ToArray());
            if (!alive || revision != viewRevision || taskEditorOpen || mobileNotificationEditing || web is not null) return;
            if (connected.Length == 0) { status.Text = "请先在“平台”页面登录并选择课程。"; return; }
            foreach (var platform in connected)
            {
                if (revision != viewRevision || taskEditorOpen || mobileNotificationEditing || web is not null) break;
                await RunSync(platform, false, false);
            }
            RefreshSyncView(revision);
        }
        catch (Exception error)
        {
            if (alive && revision == viewRevision)
                status.Text = error is TaskValidationException validation ? validation.Message : "平台连接暂时无法读取，请稍后重试同步。";
        }
        finally { syncAllChecking = false; }
    }
    private void Dashboard()
    {
        taskEditorOpen = false; SetMobileNotificationEditing(false); selectedPlatform = null; rootPage = "tasks";
        LayoutDashboard();
        var expectedRevision = viewRevision; var expectedRequest = ++dashboardRequest;
        var currentPage = page; var adapter = dashboardAdapter!;
        var restorePosition = savedDashboardPosition; var restoreTop = savedDashboardTop; var positionRestored = false;
        var summary = Card(page); summary.Background = Surface(Accent, 16);
        var main = Row(summary); var count = Text(main, "…", 38, true, Color.White);
        count.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { RightMargin = Dp(12) };
        Text(main, "需要处理", 15, true, Color.White);
        var metrics = Row(summary);
        var metricViews = new List<TextView>();
        foreach (var label in new[] { "已逾期", "24 小时内", "已完成" })
        {
            var text = Text(metrics, "…  " + label, 12, false, Color.Rgb(226, 234, 255));
            text.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
            metricViews.Add(text);
        }
        var connectionText = Text(page, "正在读取本机任务…", 12, false, Muted);
        var search = new EditText(this) { Text = taskQuery, Hint = "搜索任务、课程或平台", TextSize = 14, ContentDescription = "搜索任务" };
        search.SetSingleLine(true); search.SetTextColor(Ink); search.SetHintTextColor(Muted); search.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
        search.Background = Surface(Color.White, 10, true); search.SetMinimumHeight(Dp(48));
        page.AddView(search, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(2), BottomMargin = Dp(6) });
        var filters = Row(page); var tabs = new List<Button>();
        dashboardFilterHost = Column(); page.AddView(dashboardFilterHost);
        var filterCount = Text(page, "", 12, false, Muted);
        var filterError = Text(page, "", 13, false, Color.Rgb(182, 65, 67));
        var listHeading = Row(page); var totalText = Text(listHeading, "任务清单", 17, true); totalText.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        var add = Button(listHeading, "＋ 新增", AddTask, false); add.LayoutParameters = new LinearLayout.LayoutParams(-2, -2); add.Background = Surface(Color.Transparent);
        var empty = Card(page); empty.Visibility = ViewStates.Gone;
        var emptyTitle = Text(empty, "", 18, true); var emptyMessage = Text(empty, "", 14, false, Muted);
        var emptyAction = Button(empty, "清除筛选", () =>
        {
            if (dashboardTasks.Count == 0) PlatformsPage();
            else { ResetMobileTaskFilters(); search.Text = ""; RefreshMobileTaskFilters(); }
        }, false);
        bool CurrentView() => alive && viewRevision == expectedRevision && dashboardRequest == expectedRequest
            && ReferenceEquals(page, currentPage) && ReferenceEquals(dashboardAdapter, adapter);
        async Task RenderTasksAsync()
        {
            var renderRequest = ++dashboardRenderRequest;
            for (var i = 0; i < tabs.Count; i++)
            {
                tabs[i].Selected = i == MobileTaskShortcut(); tabs[i].SetTextColor(tabs[i].Selected ? Accent : Muted);
                tabs[i].Background = Surface(tabs[i].Selected ? Color.Rgb(226, 234, 255) : Color.Transparent, 8);
            }
            var options = MobileTaskFilters();
            var validation = TaskFiltering.Validate(options);
            var tasks = dashboardTasks; var now = DateTimeOffset.UtcNow;
            var visible = await Task.Run(() => TaskFiltering.Apply(tasks, options, now));
            if (!CurrentView() || renderRequest != dashboardRenderRequest) return;
            totalText.Text = "任务清单 · " + visible.Count;
            filterCount.Text = $"当前显示 {visible.Count}/{tasks.Count} 条任务";
            filterError.Text = validation ?? ""; filterError.Visibility = validation is null ? ViewStates.Gone : ViewStates.Visible;
            empty.Visibility = visible.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            emptyTitle.Text = validation is not null ? "请核对筛选日期" : tasks.Count == 0 ? "从一门课程开始" : "没有匹配的任务";
            emptyMessage.Text = validation ?? (tasks.Count == 0 ? "登录平台，选择要记录的课程，任务会自动汇总到这里。" : "调整状态、时间、平台或课程，或清除筛选查看全部任务。");
            emptyAction.Text = tasks.Count == 0 ? "前往平台管理" : "清除筛选";
            adapter.Submit(visible, now);
            if (!positionRestored && dashboardList is { } list)
            {
                positionRestored = true;
                list.Post(() => { if (CurrentView()) list.SetSelectionFromTop(Math.Min(restorePosition, visible.Count), restoreTop); });
            }
        }
        renderDashboardTasks = () => { dashboardRender = RenderTasksAsync(); };
        foreach (var label in new[] { "全部", "待办", "逾期", "已完成" })
        {
            var index = tabs.Count; var tab = Button(filters, label, () => { SelectMobileTaskShortcut(index); RefreshMobileTaskFilters(); }, false); tab.TextSize = 13;
            tab.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1) { RightMargin = Dp(4) }; tabs.Add(tab);
        }
        search.TextChanged += (_, _) =>
        {
            if (!CurrentView()) return;
            taskQuery = search.Text ?? "";
            dashboardSearchDelay?.Cancel(); dashboardSearchDelay?.Dispose();
            dashboardSearchDelay = new(); var token = dashboardSearchDelay.Token;
            _ = DebounceDashboardSearch();
            async Task DebounceDashboardSearch()
            {
                try { await Task.Delay(180, token); if (CurrentView()) renderDashboardTasks?.Invoke(); }
                catch (OperationCanceledException) { }
            }
        };
        var data = GetMobileUiData();
        bool ApplySnapshot(MobileDashboardSnapshot snapshot)
        {
            if (!CurrentView() || !data.IsCurrent(snapshot)) return false;
            dashboardTasks = snapshot.Tasks; dashboardCourseOptions = snapshot.CourseOptions;
            count.Text = snapshot.ActiveCount.ToString();
            metricViews[0].Text = snapshot.OverdueCount + "  已逾期";
            metricViews[1].Text = snapshot.DueWithin24HoursCount + "  24 小时内";
            metricViews[2].Text = snapshot.CompletedCount + "  已完成";
            connectionText.Text = snapshot.ConnectedCount == 0 ? "尚未连接平台，可先手动记录任务。" : "已连接 " + snapshot.ConnectedCount + " 个平台"
                + (snapshot.IncompleteCount > 0 ? "，其中 " + snapshot.IncompleteCount + " 个任务覆盖待核对。" : "；支持的任务范围已同步。");
            RebuildDashboardFilters(); renderDashboardTasks?.Invoke();
            ScheduleDashboardTimeRefresh(snapshot);
            return true;
        }
        applyDashboardSnapshot = snapshot =>
        {
            if (!ApplySnapshot(snapshot) && CurrentView()) dashboardLoad = LoadSnapshotAsync();
        };
        dashboardDataChanged = ReloadDashboardSnapshot;
        AttachMobileStoreUpdates();
        if (data.TryGetCachedDashboard(out var cached))
        {
            if (ApplySnapshot(cached)) dashboardLoad = dashboardRender;
            else dashboardLoad = LoadSnapshotAsync();
        }
        else dashboardLoad = LoadSnapshotAsync();
        async Task LoadSnapshotAsync()
        {
            try
            {
                while (CurrentView())
                {
                    var snapshot = await data.LoadDashboardAsync();
                    if (!CurrentView()) return;
                    if (ApplySnapshot(snapshot)) { await dashboardRender; return; }
                    await Task.Delay(40);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (CurrentView())
                {
                    connectionText.Text = "本机任务未能读取。";
                    status.Text = error is TaskValidationException validation ? validation.Message : "任务读取未完成，请稍后返回任务页重试。";
                }
            }
        }
        SchedulePendingMobileNotificationTask();
    }
    private void AttachMobileStoreUpdates()
    {
        if (ReferenceEquals(mobileObservedStore, store)) return;
        if (mobileObservedStore is not null) mobileObservedStore.Changed -= OnMobileStoreUpdated;
        mobileObservedStore = store;
        mobileObservedStore.Changed += OnMobileStoreUpdated;
    }
    private void OnMobileStoreUpdated()
    {
        RunOnUiThread(() =>
        {
            if (!alive || dashboardDataChanged is null) return;
            mobileDataRefreshDelay?.Cancel(); mobileDataRefreshDelay?.Dispose(); mobileDataRefreshDelay = new();
            var token = mobileDataRefreshDelay.Token;
            _ = RefreshAfterBurst();
            async Task RefreshAfterBurst()
            {
                try { await Task.Delay(180, token); if (alive) dashboardDataChanged?.Invoke(); }
                catch (OperationCanceledException) { }
            }
        });
    }
    private void ScheduleDashboardTimeRefresh(MobileDashboardSnapshot snapshot)
    {
        dashboardTimeRefresh?.Cancel(); dashboardTimeRefresh?.Dispose(); dashboardTimeRefresh = new();
        var token = dashboardTimeRefresh.Token; var expectedRevision = viewRevision;
        _ = RefreshAtBoundary();
        async Task RefreshAtBoundary()
        {
            try
            {
                var delay = snapshot.RefreshAfterUtc - DateTimeOffset.UtcNow;
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(10), token);
                if (alive && resumed && viewRevision == expectedRevision) ReloadDashboardSnapshot();
            }
            catch (OperationCanceledException) { }
        }
    }
    private void PlatformsPage()
    {
        taskEditorOpen = false; SetMobileNotificationEditing(false); selectedPlatform = null; rootPage = "platforms";
        Layout("我的平台", "先登录，再选择需要记录的课程。", navigation: true);
        var expectedRevision = viewRevision; var originPage = page; var data = GetMobileUiData();
        const string loadingMessage = "正在读取平台连接…";
        status.Text = loadingMessage; mobilePageLoad = LoadPlatformsPage();
        async Task LoadPlatformsPage()
        {
            try
            {
                MobilePlatformsSnapshot snapshot;
                do
                {
                    snapshot = await data.LoadPlatformsAsync();
                    if (!alive || viewRevision != expectedRevision || !ReferenceEquals(page, originPage)) return;
                } while (!data.IsCurrent(snapshot));
                if (status.Text == loadingMessage) status.Text = "";
                foreach (var item in snapshot.Platforms)
                {
                    var platform = item.PlatformId;
                    var card = Card(page);
                    card.Clickable = true; card.Focusable = true; card.Click += (_, _) => Platform(platform);
                    var row = Row(card); var name = Text(row, PlatformCatalog.Name(platform), 17, true); name.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
                    var arrow = Text(row, "›", 22, false, Muted); arrow.LayoutParameters = new LinearLayout.LayoutParams(-2, -2);
                    Text(card, "已选 " + item.SelectedCourseCount + " 门 · " + item.VisibleTaskCount + " 条任务", 12, false, Muted);
                    Text(card, item.SessionDescription, 12, false, Muted);
                    if (item.ConnectionReport is { } connection) Text(card, "上次检查 · " + ConnectionFeedback.From(connection).Title, 12, true);
                    Text(card, item.SyncStatus.Description, 12, false, Muted);
                }
            }
            catch (Exception error)
            {
                if (alive && viewRevision == expectedRevision)
                    status.Text = error is TaskValidationException validation ? validation.Message : "平台信息未能读取，请稍后重试。";
            }
        }
    }
    private void SettingsPage()
    {
        taskEditorOpen = false; SetMobileNotificationEditing(false); selectedPlatform = null; rootPage = "settings";
        Layout("设置", "同步与提醒，按你的节奏。", navigation: true);
        var expectedRevision = viewRevision; var originPage = page;
        const string loadingMessage = "正在读取设置…";
        status.Text = loadingMessage; mobilePageLoad = LoadSettingsPage();
        async Task LoadSettingsPage()
        {
            try
            {
                var saved = await GetMobileUiData().LoadSettingsAsync();
                if (!alive || viewRevision != expectedRevision || !ReferenceEquals(page, originPage)) return;
                if (status.Text == loadingMessage) status.Text = "";
                var settings = Card(page); Text(settings, "自动同步", 18, true);
                var automatic = new Switch(this) { Text = "自动同步已选课程", Checked = saved.AutomaticSyncEnabled, TextSize = 14 };
                automatic.SetPadding(0, Dp(12), 0, Dp(12));
                var applyingAutomatic = false; var automaticSaving = false; var acceptedAutomatic = automatic.Checked;
                automatic.CheckedChange += async (_, args) =>
                {
                    if (applyingAutomatic || automaticSaving || !alive || viewRevision != expectedRevision) return;
                    automaticSaving = true; automatic.Enabled = false;
                    try
                    {
                        var requestedValue = args.IsChecked;
                        var updated = await SaveMobileSettingsAsync(savedSettings => savedSettings with { AutomaticSyncEnabled = requestedValue });
                        if (alive && viewRevision == expectedRevision)
                        { acceptedAutomatic = updated.AutomaticSyncEnabled; ScheduleReminders(); }
                    }
                    catch
                    {
                        if (alive && viewRevision == expectedRevision)
                        {
                            applyingAutomatic = true;
                            try { automatic.Checked = acceptedAutomatic; }
                            finally { applyingAutomatic = false; }
                            status.Text = "设置未能保存，请稍后重试。";
                        }
                    }
                    finally { automaticSaving = false; if (alive && viewRevision == expectedRevision) automatic.Enabled = true; }
                };
                settings.AddView(automatic);
                Text(settings, "应用在前台时每 30 分钟检查一次，使用已保存的登录状态。", 13, false, Muted);
                var reminders = Card(page); Text(reminders, "任务提醒", 18, true);
                AddMobileNotificationSettings(reminders, saved);
                var account = Card(page); Text(account, "登录与课程", 18, true);
                Text(account, "每个平台保存当前登录。登录过期时，在平台管理中重新登录即可更新。", 14, false, Muted);
                Button(account, "管理平台与课程", PlatformsPage, false);
            }
            catch (Exception error)
            {
                if (alive && viewRevision == expectedRevision)
                    status.Text = error is TaskValidationException validation ? validation.Message : "设置未能读取，请稍后重试。";
            }
        }
    }
    private void Platform(string platform)
    {
        taskEditorOpen = false; SetMobileNotificationEditing(false); selectedPlatform = platform; rootPage = "platforms";
        Layout(PlatformCatalog.Name(platform), "登录、选课与同步", PlatformsPage);
        var expectedRevision = viewRevision; var originPage = page; var data = GetMobileUiData();
        const string loadingMessage = "正在读取课程范围…";
        status.Text = loadingMessage; mobilePageLoad = LoadPlatformPage();
        async Task LoadPlatformPage()
        {
            try
            {
                MobilePlatformSnapshot snapshot;
                do
                {
                    snapshot = await data.LoadPlatformAsync(platform);
                    if (!alive || viewRevision != expectedRevision || !ReferenceEquals(page, originPage)) return;
                } while (!data.IsCurrent(snapshot));
                if (status.Text == loadingMessage) status.Text = "";
                BuildPlatformPage(snapshot);
            }
            catch (Exception error)
            {
                if (alive && viewRevision == expectedRevision)
                    status.Text = error is TaskValidationException validation ? validation.Message : "课程范围未能读取，请稍后重试。";
            }
        }
    }
    private void BuildPlatformPage(MobilePlatformSnapshot snapshot)
    {
        var platform = snapshot.PlatformId;
        var controls = Card(page); Text(controls, "平台连接", 17, true); Text(controls, snapshot.SessionDescription, 13, false, Muted);
        var connection = snapshot.ConnectionReport;
        if (connection is not null)
        {
            var feedback = ConnectionFeedback.ForPlatformManagement(connection, platform == "uai" ? "校验官网概要" : "刷新课程");
            Text(controls, "上次检查 · " + feedback.Title, 15, true);
            Text(controls, feedback.Explanation + "\n" + feedback.NextStep, 13, false, Muted);
            Text(controls, "检查于 " + connection.CheckedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss")
                + (connection.ProblemCodes.Count > 0 ? " · " + string.Join("、", connection.ProblemCodes) : ""), 12, false, Muted);
        }
        Pair(controls, "连接平台", async () => await Login(platform), platform == "uai" ? "校验官网概要" : "刷新课程", async () => await RunSync(platform, true), true);
        Pair(controls, platform == "uai" ? "读取首页概要" : "同步已选课程", async () => await RunSync(platform, false), "停止同步", () => { if (work is null) { status.Text = "当前没有运行中的同步。"; return; } work.Cancel(); status.Text = "正在停止…"; });
        if (platform == "uai") Text(controls, UaiPortal.Describe(snapshot.UaiSummary), 13, false, Muted);
        var recording = snapshot.Recording; var courses = Card(page); var body = Column();
        var courseHeading = Row(courses); var title = Text(courseHeading, "课程范围 · " + recording.Courses.Count, 18, true); title.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        var expanded = true; Button? toggle = null;
        toggle = Button(courseHeading, "收起", () => { expanded = !expanded; body.Visibility = expanded ? ViewStates.Visible : ViewStates.Gone; toggle!.Text = expanded ? "收起" : "展开"; }, false);
        toggle.LayoutParameters = new LinearLayout.LayoutParams(-2, -2); toggle.Background = Surface(Color.Transparent); courses.AddView(body);
        Text(body, CourseRecordingRules.CatalogRefreshText(recording), 12, false, Muted);
        Text(body, "手动新增任务默认记录；可在这里取消勾选。手动课程与官网同名班课分别管理。", 12, false, Muted);
        var selections = BuildRecordingCourseChoices(body, recording);
        if (recording.Courses.Count == 0) Text(body, platform == "uai" ? "官网班课归属仍待核对，可先手动新增任务。"
            : connection is { AccountVerified: true, LoginRequired: false, CatalogCovered: true, Courses: 0 }
                ? "官网已确认当前没有课程。可点击“刷新课程”复核；首页暂时没有该平台任务。"
                : "尚未取得完整课程列表。请点击“刷新课程”；官网要求登录时，先打开“连接平台”完成登录。", 14, false, Muted);
        async Task SaveSelectionAsync()
        {
            if (busy) { status.Text = "请等待当前同步结束。"; return; }
            var expectedRevision = viewRevision; var originPage = page;
            var keys = selections.Where(item => item.Box.Checked).Select(item => item.Key).ToArray();
            busy = true;
            try
            {
                var savedRecording = await Task.Run(() => store.SaveCourseRecording(platform, false, keys, true, recording.Revision, DateTimeOffset.UtcNow));
                if (!alive || viewRevision != expectedRevision || !ReferenceEquals(page, originPage)) return;
                recording = savedRecording;
                SetMobileNotificationEditing(false); busy = false;
                if (platform == "uai") { ScheduleReminders(); status.Text = "手动课程范围已保存。"; }
                else await RunSync(platform, false);
            }
            catch (TaskValidationException error) { if (alive && viewRevision == expectedRevision) status.Text = error.Message; }
            catch { if (alive && viewRevision == expectedRevision) status.Text = "课程选择未能保存，请稍后重试。"; }
            finally { busy = false; SchedulePendingMobileNotificationTask(); }
        }
        Button(body, platform == "uai" ? "保存手动课程范围" : "保存选择并采集",
            () => { mobileCourseSave = SaveSelectionAsync(); });
        Text(page, PlatformCatalog.Sites.First(site => site.Id == platform).Status, 12, false, Muted);
    }
}
