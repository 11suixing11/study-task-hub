using AccessProbe;
using Android.OS;
using Android.Views;
using Android.Widget;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text.Json;
using Path = System.IO.Path;
using View = Android.Views.View;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    // Physical-device benchmarks require the dedicated, network-disabled app ID.
    // They share this source between the preserved baseline and the improved UI.
    private async Task RunMobilePerformanceChecks()
    {
        if (!syntheticUi || PackageName != "cn.yuki.learningaggregator.performance") return;
        var root = Path.Combine(MobileData.Root(this), "synthetic-performance");
        Directory.CreateDirectory(root);
        var results = new List<string>(); var timings = new List<object>();
        var taskCount = Math.Clamp(Intent?.GetIntExtra("performance_task_count", 500) ?? 500, 20, 2000);
        var courseCount = Math.Clamp(Intent?.GetIntExtra("performance_course_count", 80) ?? 80, 4, 256);
        var protector = new AndroidDataProtector("LearningTaskAggregator.PerformanceFixture.v1");
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidDataException(label);
            results.Add(label);
        }
        void Report(string outcome, string? error = null) => File.WriteAllText(Path.Combine(root, "performance-checks.json"),
            JsonSerializer.Serialize(new
            {
                schema = 1, outcome,
#if MOBILE_PERFORMANCE_BASELINE
                implementation = "baseline",
#else
                implementation = "optimized",
#endif
                taskCount, courseCount, checks = results.Count, results, timings, error,
                androidApi = (int)Build.VERSION.SdkInt, device = Build.Model,
                officialNetworkAccessed = false, productionDataAccessed = false,
                systemNotificationsRequested = false, measuredAtUtc = DateTimeOffset.UtcNow
            }));
        IEnumerable<View> Descendants(View view)
        {
            yield return view;
            if (view is ViewGroup group)
                for (var index = 0; index < group.ChildCount; index++)
                    foreach (var child in Descendants(group.GetChildAt(index)!)) yield return child;
        }
        View RootView() => FindViewById(Android.Resource.Id.Content)!;
        EditText Search() => Descendants(page).OfType<EditText>().Single(text => text.ContentDescription == "搜索任务");
        async Task WaitForDashboard()
        {
#if !MOBILE_PERFORMANCE_BASELINE
            await mobilePageLoad.WaitAsync(TimeSpan.FromSeconds(30));
            await dashboardLoad.WaitAsync(TimeSpan.FromSeconds(30));
            await dashboardRender.WaitAsync(TimeSpan.FromSeconds(30));
#endif
            await Task.Delay(100);
        }
        try
        {
            Layout("性能验收", "正在准备独立的合成任务…");
            var now = DateTimeOffset.UtcNow;
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var seeded = await Task.Run(() =>
            {
                var local = new TaskStore(Path.Combine(directory, "tasks.db"), protector);
                local.SaveSettings(new(false, true) { AutomaticSyncEnabled = false });
                // Populate one local fixture transaction; no platform requests or user files.
                using var connection = new SqliteConnection("Data Source=" + local.DatabasePath); connection.Open();
                using var transaction = connection.BeginTransaction();
                for (var index = 0; index < taskCount; index++)
                {
                    var task = new TaskItem
                    {
                        Id = index.ToString("x32"), Title = $"合成任务 {index:0000}", PlatformId = "pta",
                        CourseName = $"合成课程 {index % courseCount:000}", State = index % 5 == 0 ? TaskState.Completed : TaskState.Pending,
                        DeadlineKind = DeadlineKind.Known, DeadlineUtc = now.AddHours(index % 72 - 24),
                        CreatedAtUtc = now, UpdatedAtUtc = now, Notes = "独立性能验收，未连接真实账号"
                    };
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "INSERT INTO tasks(id,revision,payload,protected_link) VALUES($id,1,$payload,$link)";
                    command.Parameters.AddWithValue("$id", task.Id);
                    command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(task));
                    command.Parameters.AddWithValue("$link", protector.Protect("https://example.invalid/task/" + index));
                    command.ExecuteNonQuery();
                }
                transaction.Commit(); return local;
            });
            store = seeded; vault = new(store, protector); sync = new(store, vault, _ => new OfflineUiHandler());
#if !MOBILE_PERFORMANCE_BASELINE
            MobileTaskListAdapterChecks.Run(this, Check);
            RunCourseFilterPerformanceChecks(Check);
#endif
            async Task Measure(string label, Action action)
            {
                var timer = Stopwatch.StartNew(); action(); var dispatchMs = timer.Elapsed.TotalMilliseconds;
                await WaitForDashboard(); timer.Stop();
                timings.Add(new { operation = label, dispatchMs, readyMs = timer.Elapsed.TotalMilliseconds,
                    attachedViews = Descendants(RootView()).Count() });
            }
            ResetMobileTaskFilters(); taskMoreFiltersOpen = false;
            await Measure("dashboard_cold", Dashboard);
            Check(Descendants(RootView()).OfType<TextView>().Any(view => view.Text?.StartsWith("任务清单 · ", StringComparison.Ordinal) == true),
                "dashboard exposes the complete task count");
#if !MOBILE_PERFORMANCE_BASELINE
            Check(dashboardAdapter?.Count == taskCount && dashboardAdapter.CreatedViewCount < 30,
                "large task list creates only viewport card views");
            var retainedPage = page; var retainedAdapter = dashboardAdapter; var retainedRevision = viewRevision;
#endif
            await Measure("dashboard_warm", Dashboard);
            var search = Search();
            var searchTimer = Stopwatch.StartNew();
            foreach (var character in "合成任务 004") search.Text += character;
            var typingDispatchMs = searchTimer.Elapsed.TotalMilliseconds;
            await Task.Delay(250); await WaitForDashboard(); searchTimer.Stop();
            timings.Add(new { operation = "search_typing", dispatchMs = typingDispatchMs,
                readyMs = searchTimer.Elapsed.TotalMilliseconds, attachedViews = Descendants(RootView()).Count() });
#if !MOBILE_PERFORMANCE_BASELINE
            Check(dashboardAdapter!.Count == Math.Min(10, taskCount - 40), "debounced search publishes the last complete query");
            retainedPage = page; retainedAdapter = dashboardAdapter; retainedRevision = viewRevision;
#endif
            var filterTimer = Stopwatch.StartNew();
            SelectMobileTaskShortcut(3); RefreshMobileTaskFilters();
            await WaitForDashboard(); filterTimer.Stop();
            timings.Add(new { operation = "completed_filter", readyMs = filterTimer.Elapsed.TotalMilliseconds,
                attachedViews = Descendants(RootView()).Count() });
#if !MOBILE_PERFORMANCE_BASELINE
            Check(ReferenceEquals(page, retainedPage) && ReferenceEquals(dashboardAdapter, retainedAdapter) && viewRevision == retainedRevision,
                "filter selection retains the current page and list adapter");
            Check(dashboardAdapter!.Count == 2 && Enumerable.Range(0, dashboardAdapter.Count).All(index => dashboardAdapter[index].State == TaskState.Completed),
                "completion and search filters preserve existing matching rules");
#endif
            ResetMobileTaskFilters(); Dashboard(); await WaitForDashboard();
#if !MOBILE_PERFORMANCE_BASELINE
            dashboardList!.SetSelectionFromTop(36, -Dp(7)); await Task.Delay(150);
            var firstPosition = dashboardList.FirstVisiblePosition; var firstTop = dashboardList.GetChildAt(0)!.Top;
            Check(firstPosition > 0, "the large task list can move beyond its header");
            TaskDetail(dashboardAdapter![Math.Min(firstPosition - 1, dashboardAdapter.Count - 1)].Id); await mobileTaskDetailLoad;
            HandleSystemBack(); await WaitForDashboard();
            Check(dashboardList!.FirstVisiblePosition == firstPosition && Math.Abs(dashboardList.GetChildAt(0)!.Top - firstTop) <= Dp(2),
                "returning from task detail restores the previous list position");
            using (var lockEntered = new ManualResetEventSlim())
            {
                var heldStore = Task.Run(() => store.UpdateSettings(settings =>
                {
                    lockEntered.Set(); Thread.Sleep(500); return settings;
                }));
                await Task.Run(() => lockEntered.Wait(TimeSpan.FromSeconds(10)));
                var taskId = dashboardAdapter![0].Id;
                var detailDispatch = Stopwatch.StartNew(); TaskDetail(taskId); detailDispatch.Stop();
                await heldStore; await mobileTaskDetailLoad;
                Check(detailDispatch.Elapsed.TotalMilliseconds < 150,
                    "opening task detail stays responsive while synchronization holds the task store");
                Dashboard(); await WaitForDashboard();
            }
            var markingTask = dashboardAdapter![0];
            TaskDetail(markingTask.Id); await mobileTaskDetailLoad;
            using (var lockEntered = new ManualResetEventSlim())
            {
                var heldStore = Task.Run(() => store.UpdateSettings(settings =>
                {
                    lockEntered.Set(); Thread.Sleep(500); return settings;
                }));
                await Task.Run(() => lockEntered.Wait(TimeSpan.FromSeconds(10)));
                var markDispatch = Stopwatch.StartNew();
                Descendants(page).OfType<Button>().Single(button => button.Text == "个人标记为已完成").PerformClick();
                markDispatch.Stop(); await heldStore; await mobileTaskWrite; await dashboardLoad; await dashboardRender;
                Check(markDispatch.Elapsed.TotalMilliseconds < 150,
                    "personal completion stays responsive while synchronization holds the task store");
                var latest = await Task.Run(() => store.Get(markingTask.Id)!);
                Check(latest.State == TaskState.Completed, "asynchronous personal completion actually saves the task state");
                await Task.Run(() => store.SetState(latest.Id, latest.Revision, markingTask.State, DateTimeOffset.UtcNow));
                Dashboard(); await WaitForDashboard();
            }
            var deadlineTask = dashboardAdapter![0];
            EditTaskDeadline(deadlineTask.Id); await mobileTaskEditorLoad;
            using (var lockEntered = new ManualResetEventSlim())
            {
                var heldStore = Task.Run(() => store.UpdateSettings(settings =>
                {
                    lockEntered.Set(); Thread.Sleep(500); return settings;
                }));
                await Task.Run(() => lockEntered.Wait(TimeSpan.FromSeconds(10)));
                var deadlineDispatch = Stopwatch.StartNew();
                Descendants(page).OfType<Button>().Single(button => button.Text == "保存确认").PerformClick();
                deadlineDispatch.Stop(); await heldStore; await mobileTaskWrite; await mobileTaskDetailLoad;
                Check(deadlineDispatch.Elapsed.TotalMilliseconds < 150,
                    "confirming a task deadline stays responsive while synchronization holds the store");
                Dashboard(); await WaitForDashboard();
            }
            AddTask();
            Descendants(page).OfType<EditText>().First().Text = "保存响应性验收";
            using (var lockEntered = new ManualResetEventSlim())
            {
                var heldStore = Task.Run(() => store.UpdateSettings(settings =>
                {
                    lockEntered.Set(); Thread.Sleep(500); return settings;
                }));
                await Task.Run(() => lockEntered.Wait(TimeSpan.FromSeconds(10)));
                var addDispatch = Stopwatch.StartNew();
                Descendants(page).OfType<Button>().Single(button => button.Text == "保存任务").PerformClick();
                addDispatch.Stop(); await heldStore; await mobileTaskWrite; await dashboardLoad; await dashboardRender;
                Check(addDispatch.Elapsed.TotalMilliseconds < 150,
                    "manually adding a task stays responsive while synchronization holds the store");
                var addedForSave = await Task.Run(() => store.LoadTaskSummaries().Single(task => task.Title == "保存响应性验收"));
                Check(addedForSave.Title == "保存响应性验收", "asynchronous manual addition actually saves the task");
                await Task.Run(() => store.Delete(addedForSave.Id, addedForSave.Revision));
                Dashboard(); await WaitForDashboard();
            }
            var currentAdapter = dashboardAdapter!; var currentPage = page; var currentRevision = viewRevision;
            var added = await Task.Run(() => store.Create(new("异步新增验收", "pta", "合成课程 000", DeadlineKind.Unknown,
                null, null, null, null, "", "", null), DateTimeOffset.UtcNow));
            for (var retry = 0; retry < 100 && !Enumerable.Range(0, currentAdapter.Count).Any(index => currentAdapter[index].Id == added.Id); retry++)
                await Task.Delay(40);
            Check(ReferenceEquals(dashboardAdapter, currentAdapter) && ReferenceEquals(page, currentPage) && viewRevision == currentRevision
                && Enumerable.Range(0, currentAdapter.Count).Any(index => currentAdapter[index].Id == added.Id),
                "a completed background database update refreshes the current homepage without replacing it");
            taskMoreFiltersOpen = true; RefreshMobileTaskFilters(); await WaitForDashboard();
            var oldSpinner = Descendants(page).OfType<Spinner>().Single(spinner => spinner.ContentDescription == "任务状态筛选");
            SelectMobileTaskShortcut(1); RefreshMobileTaskFilters(); await WaitForDashboard();
            oldSpinner.SetSelection((int)TaskFilterState.Completed); await Task.Delay(150);
            Check(taskFilterState == TaskFilterState.Active, "a detached same-page filter cannot overwrite the newer filter selection");
            taskMoreFiltersOpen = false; ResetMobileTaskFilters(); Dashboard(); await WaitForDashboard();
            currentAdapter = dashboardAdapter!; currentPage = page;
            var oldSearch = Search(); oldSearch.Text = "异步新增";
            SettingsPage(); await mobilePageLoad; await Task.Delay(300);
            Check(rootPage == "settings" && dashboardAdapter is null && !ReferenceEquals(page, currentPage),
                "a late debounced search cannot replace a newer settings page");
            Dashboard(); await WaitForDashboard(); ResetMobileTaskFilters(); Search().Text = ""; RefreshMobileTaskFilters(); await WaitForDashboard();
            var courseOption = dashboardCourseOptions.First(option => option.Key.Length > 0);
            taskFilterCourse = courseOption.Key; taskMoreFiltersOpen = false; RefreshMobileTaskFilters(); await WaitForDashboard();
            var hidden = await Task.Run(() =>
            {
                var current = store.LoadCourseRecordingOverview("pta");
                return store.SaveCourseRecording("pta", false, current.Courses.Where(course => course.Key != courseOption.Key).Select(course => course.Key).ToArray(),
                    true, current.Revision, DateTimeOffset.UtcNow);
            });
            for (var retry = 0; retry < 100 && taskFilterCourse is not null; retry++) await Task.Delay(40);
            Check(taskFilterCourse is null && !taskMoreFiltersOpen,
                "a course removed from the visible snapshot clears its invalid filter even while advanced controls are collapsed");
            await Task.Run(() => store.SaveCourseRecording("pta", false, hidden.Courses.Select(course => course.Key).ToArray(), true,
                hidden.Revision, DateTimeOffset.UtcNow));
            await WaitForDashboard();
            Platform("pta"); status.Text = "课程读取完成后保留同步结果"; await mobilePageLoad;
            Check(status.Text == "课程读取完成后保留同步结果", "a background platform read preserves the current operation result");
            PlatformsPage(); status.Text = "平台列表读取完成后保留结果"; await mobilePageLoad;
            Check(status.Text == "平台列表读取完成后保留结果", "a background platform-list read preserves the current operation result");
            SettingsPage(); status.Text = "设置读取完成后保留结果"; await mobilePageLoad;
            Check(status.Text == "设置读取完成后保留结果", "a background settings read preserves the current operation result");
#endif
            var courses = Enumerable.Range(0, courseCount).Select(index => new RecordingCourse(
                CourseRecordingRules.MetadataKey("pta", "performance-fixture", index.ToString(), "class"),
                $"合成课程 {index:000}", "独立性能验收目录")).ToArray();
            Layout("课程选择", "批量勾选性能验收");
            BuildRecordingCourseChoices(page, new CourseRecordingSettings { Courses = courses, SelectionConfirmed = true });
            var selectTimer = Stopwatch.StartNew();
            Descendants(page).OfType<Button>().Single(button => button.Text == "全选当前列表").PerformClick();
            selectTimer.Stop();
            timings.Add(new { operation = "select_all_courses", dispatchMs = selectTimer.Elapsed.TotalMilliseconds,
                selected = courseFilterChoices.Count(choice => choice.Box.Checked), attachedViews = Descendants(RootView()).Count() });
            Check(courseFilterChoices.All(choice => choice.Box.Checked), "course bulk selection preserves every matching selection");
            await Measure("platforms", PlatformsPage);
            await Measure("settings", SettingsPage);
            await Measure("return_dashboard", Dashboard);
            await Task.Delay(500);
            Report("passed");
            status.Text = "性能验收完成，可滚动和搜索合成任务。";
        }
        catch (Exception error)
        {
            Report("failed", error.GetType().Name + ": " + error.Message);
            if (alive && status is not null) status.Text = "性能验收未通过：" + error.GetType().Name;
        }
    }
}
