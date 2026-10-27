using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessProbe;
using Microsoft.Data.Sqlite;

internal static class TaskStoreChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "aggregate-task-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tasks.db");
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        var count = 0;
        void Check(bool condition) { count++; if (!condition) throw new Exception("Task persistence or rule check failed: " + count); }
        void Rejected(Action action, string code)
        {
            try { action(); throw new Exception("Expected task validation failure."); }
            catch (TaskValidationException error) { Check(error.Code == code); }
        }
        var protection = new SyntheticProtector();
        var store = new TaskStore(path, protection);
        var manualDraft = new TaskDraft("手动任务", "mooc", "合成课程", DeadlineKind.Known, null, now.AddHours(1),
            "https://example.invalid/task?enc=SYNTHETIC-SECRET-ONLY", null, "", "演示备注", null);
        var manual = store.Create(manualDraft, now);
        Check(manual.Source == TaskSource.Manual && manual.State == TaskState.Pending && manual.Revision == 1);
        Check(store.LoadTasks().Count == 1);
        Check(TaskRules.Priority(manual, now) == 1);
        Check(TaskRules.Priority(manual with { DeadlineUtc = now.AddHours(10) }, now) == 2);
        Check(TaskRules.Priority(manual with { DeadlineUtc = now.AddDays(3) }, now) == 3);
        Check(TaskRules.Priority(manual with { DeadlineUtc = now.AddSeconds(-1) }, now) == 0);
        Check(TaskRules.Priority(manual with { DeadlineKind = DeadlineKind.Unknown, DeadlineUtc = null, PriorityOverride = 1 }, now) == 4);
        Check(TaskRules.Priority(manual with { DeadlineKind = DeadlineKind.None, DeadlineUtc = null }, now) == 3);
        Check(TaskRules.Priority(manual with { DeadlineKind = DeadlineKind.None, DeadlineUtc = null, PriorityOverride = 2 }, now) == 2);
        Check(TaskRules.Priority(manual with { DeadlineUtc = now, PriorityOverride = 3 }, now) == 0);
        Check(store.LoadReminderHistory().Count == 2);
        Check(store.Get(manual.Id)!.Link == manualDraft.Link);
        Check(!JsonSerializer.Serialize(manual).Contains("SYNTHETIC-SECRET", StringComparison.Ordinal));
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("SYNTHETIC-SECRET", StringComparison.Ordinal));
        var reopened = new TaskStore(path, protection);
        Check(reopened.Get(manual.Id)!.Title == manual.Title);
        Check(reopened.Get(manual.Id)!.DeadlineUtc == manual.DeadlineUtc);
        manual = store.SetState(manual.Id, manual.Revision, TaskState.Completed, now);
        Check(manual.State == TaskState.Completed && store.LoadReminderHistory().Count == 0);
        manual = store.SetState(manual.Id, manual.Revision, TaskState.Returned, now);
        Check(TaskRules.IsActive(manual) && store.LoadReminderHistory().Count == 2);
        manual = store.SetState(manual.Id, manual.Revision, TaskState.Ignored, now);
        Check(store.LoadReminderHistory().Count == 0);
        manual = store.SetState(manual.Id, manual.Revision, TaskState.Pending, now);
        Check(store.LoadReminderHistory().Count == 2);
        Rejected(() => store.SetState(manual.Id, manual.Revision, TaskState.Returned, now), "TASK002");
        Rejected(() => store.Edit(manual.Id, 1, manualDraft, now), "TASK004");

        const string target = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=202&cpi=303&workId=404&answerId=505&enc=SYNTHETIC%2BONLY";
        var row = new WorkListTask("采集任务", "已完成", "剩余48小时", WorkListParser.ParseTaskLocation(target));
        var imported = store.ImportWorks([row], "合成导入课程", now);
        Check(imported == new ImportSummary(1, 0, 0, 0));
        var collected = store.LoadTasks().Single(task => task.Source == TaskSource.Chaoxing);
        Check(collected.State == TaskState.Completed && collected.PlatformStatusText == "已完成" && collected.ProgressPercent is null);
        Check(collected.DeadlineUtc == now.AddHours(48) && !TaskRules.HasTrustedDeadline(collected));
        Check(collected.Link == target && collected.SourceKey is not null);
        collected = store.SetState(collected.Id, collected.Revision, TaskState.Completed, now);
        var userDraft = manualDraft with { Title = "我的名称", PlatformId = "chaoxing", CourseName = "我的课程",
            DeadlineUtc = now.AddHours(20), Link = collected.Link, Notes = "用户备注" };
        collected = store.Edit(collected.Id, collected.Revision, userDraft, now);
        Rejected(() => store.Edit(collected.Id, collected.Revision, userDraft with { ProgressPercent = 99m }, now), "TASK003");
        var refreshed = row with
        {
            Title = "平台改名", PlatformStatusText = "待完成",
            Location = WorkListParser.ParseTaskLocation(target.Replace("answerId=505", "answerId=506").Replace("SYNTHETIC%2BONLY", "SYNTHETIC%26NEW"))
        };
        var result = store.ImportWorks([refreshed, refreshed], "新平台课程", now.AddMinutes(1));
        Check(result == new ImportSummary(0, 1, 0, 1));
        collected = store.Get(collected.Id)!;
        Check(collected.Title == "我的名称" && collected.CourseName == "我的课程" && collected.State == TaskState.Completed);
        Check(collected.DeadlineUtc == userDraft.DeadlineUtc && collected.Notes == "用户备注");
        Check(collected.PlatformTitle == "平台改名" && collected.PlatformStatusText == "待完成");
        Check(collected.Link == refreshed.Location!.PageUri.OriginalString);
        const string replacement = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=202&cpi=303&workId=404&answerId=999&enc=SYNTHETIC-MANUAL";
        var linkEdited = store.Edit(collected.Id, collected.Revision, userDraft with { Link = replacement }, now);
        Check(linkEdited.Link == replacement && linkEdited.Overrides.HasFlag(TaskOverrides.Link));
        store.ImportWorks([refreshed], "", now);
        collected = store.Get(collected.Id)!;
        Check(collected.Link == replacement && collected.State == TaskState.Completed);
        Check(TaskRules.Priority(collected with { DeadlineKind = DeadlineKind.None, DeadlineUtc = null }, now) == 3);
        Check(TaskRules.Priority(collected with { DeadlineKind = DeadlineKind.None, DeadlineUtc = null, Overrides = TaskOverrides.None }, now) == 4);
        var sameTitleOtherWork = row with { Location = WorkListParser.ParseTaskLocation(target.Replace("workId=404", "workId=405")) };
        Check(store.ImportWorks([sameTitleOtherWork], "", now).Added == 1);
        var sameWorkOtherAccount = row with { Location = WorkListParser.ParseTaskLocation(target.Replace("cpi=303", "cpi=304")) };
        Check(store.ImportWorks([sameWorkOtherAccount], "", now).Added == 1);
        Check(store.LoadTasks().Count == 4);
        Check(store.ImportWorks([row with { Location = null }, row with { Location = null }], "", now).Unresolved == 2);
        Check(store.LoadTasks().Count == 6);
        var beforeFailedBatch = store.LoadTasks().Count;
        Rejected(() => store.ImportWorks([sameTitleOtherWork with { Location = null }, row with { Title = new string('x', 301) }], "", now), "TASK001");
        Check(store.LoadTasks().Count == beforeFailedBatch);
        var detail = WorkDetailParser.Parse("<p class='rightBord'>作答时间:<em>10-14 19:04</em>至<em>10-25 23:59</em></p>");
        collected = store.SaveDetail(refreshed.Location!, detail, now.AddMinutes(2));
        Check(collected.DisplayedTimeText.Contains("10-25 23:59", StringComparison.Ordinal));
        Check(collected.DeadlineUtc == userDraft.DeadlineUtc && collected.State == TaskState.Completed);
        Check(new TaskStore(path, protection).Get(collected.Id)!.State == TaskState.Completed);

        var progressTask = collected with { State = TaskState.Pending, ProgressPercent = 99m };
        Rejected(() => TaskRules.Transition(progressTask, TaskState.Completed), "TASK003");
        Check(TaskRules.CanComplete(progressTask with { ProgressPercent = 100m }));
        Check(TaskRules.CanComplete(progressTask with { ProgressPercent = null, ScoreText = "100分" }));
        var unchanged = progressTask with { State = TaskState.Completed };
        Check(ReferenceEquals(TaskRules.Transition(unchanged, TaskState.Completed), unchanged));
        Check(TaskRules.CanComplete(manual with { ProgressPercent = 1m }));
        Rejected(() => TaskRules.Validate(manualDraft with { ProgressPercent = 101m }), "TASK003");
        Rejected(() => TaskRules.Validate(manualDraft with { DeadlineUtc = null }), "TIME001");
        Rejected(() => TaskRules.Validate(manualDraft with { BeginUtc = now.AddDays(3) }), "TIME001");
        Rejected(() => TaskRules.Validate(manualDraft with { DeadlineUtc = DateTimeOffset.MinValue }), "TIME001");
        Rejected(() => TaskRules.Validate(manualDraft with { DeadlineUtc = DateTimeOffset.MaxValue }), "TIME001");
        foreach (var unsafeLink in new[] { "http://example.invalid", "file:///C:/Windows", "javascript:alert(1)",
            "https://user:password@example.invalid/", "https://example.invalid/\" onclick=x",
            "https://example.invalid/<li>", "https://example.invalid/path\\task" })
            Rejected(() => TaskRules.Validate(manualDraft with { Link = unsafeLink }), "LINK001");

        var precisionStore = new TaskStore(Path.Combine(root, "precision.db"), protection);
        var precise = precisionStore.Create(manualDraft with { DeadlineUtc = now.AddHours(-4).AddTicks(12345) }, now);
        var preciseDue = precisionStore.TakeDue(now);
        Check(preciseDue.Count == 1 && preciseDue[0].Plan.Kind == ReminderKind.Overdue);
        var reminderRoot = Path.Combine(root, "reminders.db");
        var reminders = new TaskStore(reminderRoot, protection);
        var timed = reminders.Create(manualDraft, now);
        var claimed = reminders.TakeDue(now);
        Check(claimed.Count == 1 && reminders.TakeDue(now).Count == 0);
        reminders.ReleaseReminderClaims(claimed.Select(item => item.Plan.Id).ToArray());
        Check(reminders.LoadReminderHistory().Single(item => item.Plan.Id == claimed[0].Plan.Id).Attempts == 0);
        var due = reminders.TakeDue(now);
        Check(due.Count == 1 && due[0].Plan.Kind == ReminderKind.DueSoon);
        Check(reminders.TakeDue(now).Count == 0);
        reminders.RecordNotificationRequest([due[0].Plan.Id], true, now);
        reminders.ReleaseReminderClaims([due[0].Plan.Id]);
        Check(new TaskStore(reminderRoot, protection).TakeDue(now).Count == 0);
        var overdue = reminders.TakeDue(now.AddHours(3));
        Check(overdue.Count == 1 && overdue[0].Plan.Kind == ReminderKind.Overdue);
        reminders.RecordNotificationRequest([overdue[0].Plan.Id], false, now.AddHours(3));
        Check(reminders.TakeDue(now.AddHours(3).AddSeconds(30)).Count == 0);
        Check(reminders.TakeDue(now.AddHours(3).AddMinutes(1)).Count == 1);
        timed = reminders.Edit(timed.Id, timed.Revision, manualDraft with { DeadlineUtc = now.AddDays(2) }, now.AddHours(3));
        Check(reminders.LoadReminderHistory().Count == 3); // old requested event plus two replacement plans
        Check(reminders.TakeDue(now.AddHours(3)).Count == 0);
        reminders.SaveSettings(new(false, true));
        Check(!new TaskStore(reminderRoot, protection).LoadSettings().NotificationsEnabled);
        Check(reminders.TakeDue(now.AddDays(3)).Count == 0);
        reminders.SaveSettings(new(true, true));
        var overdueAfterRestart = new TaskStore(reminderRoot, protection).TakeDue(now.AddDays(3));
        Check(overdueAfterRestart.Count == 1 && overdueAfterRestart[0].Plan.Kind == ReminderKind.Overdue);
        Check(reminders.LoadReminderHistory().All(item => item.Plan.TaskId == timed.Id));
        reminders.Delete(timed.Id, timed.Revision);
        Check(reminders.LoadTasks().Count == 0 && reminders.LoadReminderHistory().Count == 0);

        // A broken key must not erase a saved link when editing other fields.
        var corruptView = new TaskStore(path, new BrokenProtector());
        var corrupted = corruptView.Get(manual.Id)!;
        Check(corrupted.Link is null && corrupted.LinkIssue == "LINK002");
        corruptView.SetState(corrupted.Id, corrupted.Revision, TaskState.Completed, now);
        Check(new TaskStore(path, protection).Get(manual.Id)!.Link == manualDraft.Link);

        if (OperatingSystem.IsWindows())
        {
            var dpapiPath = Path.Combine(root, "dpapi.db");
            var dpapi = new TaskStore(dpapiPath);
            var secured = dpapi.Create(manualDraft, now);
            Check(new TaskStore(dpapiPath).Get(secured.Id)!.Link == manualDraft.Link);
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(dpapiPath)).Contains("SYNTHETIC-SECRET", StringComparison.Ordinal));
        }
        Check(TaskRules.ParseLocalTime("2026-10-25 23:59")!.Value.Offset == TimeSpan.Zero);
        Rejected(() => TaskRules.ParseLocalTime("10-25 23:59"), "TIME001");
        Check(ReminderPlanner.TimedPlans(manual with { State = TaskState.Ignored }, now).Count == 0);
        Check(ReminderPlanner.TimedPlans(manual with { State = TaskState.Completed }, now).Count == 0);
        Check(ReminderPlanner.TimedPlans(manual with { DeadlineKind = DeadlineKind.Unknown, DeadlineUtc = null }, now).Count == 0);
        Check(ReminderPlanner.TimedPlans(manual with { DeadlineKind = DeadlineKind.None, DeadlineUtc = null }, now).Count == 0);
        Console.WriteLine($"PASS: {count} task persistence, import, state, local protection and reminder checks (synthetic data only).");
        File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(new { outcome = "passed", count, data = "synthetic_only" }));
    }

    private sealed class SyntheticProtector : ILocalLinkProtector
    {
        public byte[] Protect(string link) => Encoding.UTF8.GetBytes(link).Select(value => (byte)(value ^ 0xC7)).ToArray();
        public string Unprotect(byte[] protectedLink) => Encoding.UTF8.GetString(protectedLink.Select(value => (byte)(value ^ 0xC7)).ToArray());
    }
    private sealed class BrokenProtector : ILocalLinkProtector
    {
        public byte[] Protect(string link) => throw new CryptographicException();
        public string Unprotect(byte[] link) => throw new CryptographicException();
    }
}
