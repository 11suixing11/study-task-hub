using AccessProbe;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using System.Text.Json;
using Path = System.IO.Path;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private async Task RunImprovementUiChecks(string mode)
    {
        var directory = Path.Combine(MobileData.Root(this), "synthetic-improvement-ui-test");
        Directory.CreateDirectory(directory); var checks = new List<string>();
        var output = Path.Combine(directory, "ui-checks-" + mode + ".json");
        void Check(bool value, string label) { if (!value) throw new InvalidDataException(label); checks.Add(label); }
        IEnumerable<View> Descendants(View view)
        {
            yield return view;
            if (view is ViewGroup group)
                for (var i = 0; i < group.ChildCount; i++)
                    foreach (var child in Descendants(group.GetChildAt(i)!)) yield return child;
        }
        bool Has(string value) => Descendants(page).OfType<TextView>().Any(view => view.Text?.Contains(value, StringComparison.Ordinal) == true);
        Button Action(string text) => Descendants(page).OfType<Button>().Single(view => view.Text == text);
        void Report(string outcome, string? error = null) => File.WriteAllText(output, JsonSerializer.Serialize(new
        { schema = 1, outcome, mode, checks = checks.Count, results = checks, error,
            officialNetworkAccessed = false, productionDataAccessed = false, realAccountAcceptance = "pending" }));
        try
        {
            Check(mode is "ui" or "app_task" or "app_home" or "web" or "unavailable", "only configured isolated scenarios are accepted");
            var protector = new AndroidDataProtector("LearningTaskAggregator.SyntheticImprovements.v1");
            store = new TaskStore(Path.Combine(directory, Guid.NewGuid().ToString("N"), "tasks.db"), protector);
            vault = new(store, protector); sync = new(store, vault, _ => new OfflineUiHandler());
            store.SaveSettings(new(false, true) { AutomaticSyncEnabled = false });
            var now = DateTimeOffset.UtcNow;
            const string entry = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=201&cpi=301&workId=401&answerId=501&enc=SYNTHETIC%2BONLY";
            if (mode != "ui")
            {
                Layout("隔离任务跳转验收", "使用无网络权限的测试 App");
                if (mode is "app_task" or "app_home")
                {
                    var info = PackageManager!.GetApplicationInfo("com.chaoxing.mobile", Android.Content.PM.PackageInfoFlags.MetaData);
                    Check(PackageManager.GetApplicationLabel(info)?.ToString() == "隔离任务路由测试", "native destination is the isolated task fixture");
                }
                var route = MobileTaskRouting.Open("chaoxing", entry, "合成课程", true, TryOpenTaskApp, TryOpenTaskWeb);
                Check(route.Outcome == mode, "native fallback matches expected " + mode);
                status.Text = route.Message;
                if (mode != "unavailable")
                {
                    await Task.Delay(1400);
                    Check(resumed && web is null, "return from native destination keeps aggregator activity available");
                }
                Report("passed"); return;
            }

            var cx = CourseRecordingRules.Chaoxing(new("合成课程", "101", "201", "301", new Uri(entry)));
            var catalog = store.ObserveRecordingCourses("chaoxing", [cx], true, now);
            store.SaveCourseRecording("chaoxing", false, [cx.Course.Key], true, catalog.Revision, now);
            store.ImportWorks([new("手机日期验收任务", "未交", "剩余2小时", WorkListParser.ParseTaskLocation(entry))], "合成课程", now);
            var task = store.LoadTasks().Single(); TaskDetail(task.Id); await mobileTaskDetailLoad;
            Check(Has("约 ") && Has("打开官方任务") && Has("打开任务网页") && Has("打开官方课程入口"), "task detail shows estimated deadline and three official routing actions");
            Check(Has("个人标记为已完成"), "local completion is labelled as a personal mark");
            Check(!store.LoadReminderHistory().Any(row => row.Plan.Kind is ReminderKind.DueSoon or ReminderKind.Overdue), "estimated date has no deadline notification before confirmation");
            EditTaskDeadline(task.Id); await mobileTaskEditorLoad;
            Check(Descendants(page).OfType<Spinner>().Single().SelectedItemPosition == (int)DeadlineKind.Known, "deadline editor opens with current kind");
            Action("保存确认").PerformClick(); await mobileTaskWrite; await mobileTaskDetailLoad;
            task = store.Get(task.Id)!;
            Check(task.Overrides.HasFlag(TaskOverrides.Deadline) && task.DeadlineEvidence == DeadlineEvidence.RemainingTime, "saving unchanged estimate confirms deadline while preserving original evidence");
            Check(store.LoadReminderHistory().Any(row => row.Plan.Kind == ReminderKind.DueSoon), "confirmed deadline schedules local reminders");
            Check(!Has("约 ") && Has("截止时间已确认"), "confirmed date returns to task detail without estimated label");
            var saved = task;
            EditTaskDeadline(task.Id); await mobileTaskEditorLoad; pageBack!();
            Check(store.Get(task.Id) == saved, "cancelling deadline editor leaves stored task unchanged");
            EditTaskDeadline(task.Id); await mobileTaskEditorLoad;
            Descendants(page).OfType<EditText>().Single().Text = "not-a-time";
            Action("保存确认").PerformClick(); await mobileTaskWrite; await mobileTaskDetailLoad;
            Check(store.Get(task.Id) == saved && status.Text?.Contains("时间格式", StringComparison.Ordinal) == true, "invalid date stays in editor without writing task");
            EditTaskDeadline(task.Id); await mobileTaskEditorLoad; store.SetState(task.Id, task.Revision, TaskState.NeedsReview, now);
            var raced = store.Get(task.Id)!; Action("保存确认").PerformClick(); await mobileTaskWrite; await mobileTaskDetailLoad;
            Check(store.Get(task.Id) == raced && status.Text?.Contains("刚被更新", StringComparison.Ordinal) == true, "revision conflict preserves latest task instead of saving stale deadline");
            EditTaskDeadline(task.Id); await mobileTaskEditorLoad; Descendants(page).OfType<Spinner>().Single().SetSelection((int)DeadlineKind.None);
            Action("保存确认").PerformClick(); await mobileTaskWrite; await mobileTaskDetailLoad; task = store.Get(task.Id)!;
            Check(task.DeadlineKind == DeadlineKind.None && task.DeadlineUtc is null && Has("无截止时间"), "no-deadline choice clears time and is displayed");
            Check(store.LoadReminderHistory().All(row => row.Plan.Kind is not (ReminderKind.DueSoon or ReminderKind.Overdue)), "no-deadline choice cancels unsent deadline notifications");
            var manual = store.Create(new("手机人工补录", "chaoxing", "合成课程", DeadlineKind.Known, null, now.AddHours(4), null, null, "", "", null), now);
            Dashboard();
            await dashboardLoad; await dashboardRender;
            Check(dashboardAdapter!.Count > 0 && Enumerable.Range(0, dashboardAdapter.Count).Any(index => dashboardAdapter[index].Id == manual.Id)
                && store.LoadCourseRecordingOverview("chaoxing").SelectedCourseKeys.Contains(CourseRecordingRules.TaskCourseKey(manual)), "manual task is visible after official course selection");
            Platform("chaoxing");
            await mobilePageLoad;
            Check(Has("手动录入") && Descendants(page).OfType<CheckBox>().Count(box => box.Text == "合成课程") == 2, "manual and official courses are both selectable without a missing-catalog warning");

            var ptaCourse = CourseRecordingRules.Metadata("pta", "900001", new("910001", "关注验收", "class", "合成班课"));
            var ptaCatalog = store.ObserveRecordingCourses("pta", [ptaCourse], true, now);
            store.SaveCourseRecording("pta", false, [ptaCourse.Course.Key], true, ptaCatalog.Revision, now);
            PlatformCollectedTask Row(TaskState state) => new("pta", "900001", "910001", "class", "920001", "problem-set", "手机官网重做验收", "关注验收",
                state, "官网合成状态", "explicit", "", null, null, "", "https://pintia.cn/problem-sets/920001/exam/overview") { MetadataVersion = 2 };
            store.ImportMetadataTasks("pta", [Row(TaskState.Completed)], now); var attention = store.LoadTasks().Single(row => row.Source == TaskSource.Pta);
            attention = store.SetState(attention.Id, attention.Revision, TaskState.Completed, now);
            store.ImportMetadataTasks("pta", [Row(TaskState.Returned)], now); TaskDetail(attention.Id); await mobileTaskDetailLoad;
            Check(Has("官网要求重做") && Has("已核对官网要求"), "official reopening is visible with an acknowledgement action");
            Action("已核对官网要求").PerformClick(); await mobileTaskWrite; await mobileTaskDetailLoad; attention = store.Get(attention.Id)!;
            Check(attention.State == TaskState.Completed && !TaskRules.HasPlatformAttention(attention) && !Has("已核对官网要求"), "acknowledgement keeps personal completion and clears attention UI");

            using var first = MobileReminderScheduler.CreateOpenIntent(this, manual.Id);
            using var second = MobileReminderScheduler.CreateOpenIntent(this, attention.Id);
            Check(first.DataString != second.DataString && first.GetStringExtra("task_id") == manual.Id, "notification intents identify separate tasks");
            DisplayNotificationTask(first); await mobileNotificationTaskLoad;
            Check(Has(manual.Title), "notification task ID opens the matching detail");
            using var missing = MobileReminderScheduler.CreateOpenIntent(this, new string('a', 32));
            DisplayNotificationTask(missing); await mobileNotificationTaskLoad;
            Check(Has(manual.Title) && status.Text?.Contains("已不存在", StringComparison.Ordinal) == true, "stale notification does not open a deleted or unknown task");
            using var malformed = new Intent(); malformed.PutExtra("task_id", "not-an-id");
            Check(!DisplayNotificationTask(malformed), "malformed notification task ID is ignored");
            Platform("uai");
            await mobilePageLoad;
            Check(Has("读取首页概要") && Has("保存手动课程范围") && Has("暂不自动导入"), "UAI page clearly presents preview and manual course scope");
            var uaiManual = store.Create(new("AI版本地记录", "uai", "AI课程", DeadlineKind.Unknown, null, null, null, null, "", "", null), now);
            Platform("uai"); await mobilePageLoad; Action("保存手动课程范围").PerformClick(); await mobileCourseSave;
            Check(store.Get(uaiManual.Id) is not null && status.Text == "手动课程范围已保存。" && vault.Load("uai") is null, "UAI manual scope saves without requiring official login");
            await RunTaskFilterUiChecks();
            TaskDetail(manual.Id); await mobileTaskDetailLoad; Report("passed");
        }
        catch (Exception error) { Report("failed", error.GetType().Name + ": " + error.Message); }
    }
}
