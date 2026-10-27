using System.Text.Json;
using AccessProbe;
using Microsoft.Data.Sqlite;

internal static class TaskMetadataChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Task metadata check failed: " + (count + 1)); count++; }
        var now = new DateTimeOffset(2026, 10, 23, 20, 0, 0, TimeSpan.FromHours(8));
        foreach (var label in new[] { "已完成", "已提交", "已交", "待批阅", "已批阅", "已批改", "已提交 待批阅" })
            Check(ChaoxingTaskMetadata.ReadState(label) == TaskState.Completed);
        foreach (var label in new[] { "未交", "未完成", "未提交", "待提交", "待完成", "进行中", "作答中", "未开始" })
            Check(ChaoxingTaskMetadata.ReadState(label) == TaskState.Pending);
        foreach (var label in new[] { "被打回", "已打回", "退回重做", "需重做" })
            Check(ChaoxingTaskMetadata.ReadState(label) == TaskState.Returned);
        foreach (var label in new[] { "未完成 已提交", "未批阅", "未知状态", "" }) Check(ChaoxingTaskMetadata.ReadState(label) is null);

        var remaining = ChaoxingTaskMetadata.ReadRemaining("剩余 2天 3小时 4分钟", now);
        Check(remaining is { Evidence: DeadlineEvidence.RemainingTime, BeginUtc: null }
            && remaining.DeadlineUtc == now.AddDays(2).AddHours(3).AddMinutes(4).ToUniversalTime());
        Check(ChaoxingTaskMetadata.ReadRemaining("剩余217小时9分钟", now)?.DeadlineUtc == now.AddHours(217).AddMinutes(9));
        Check(ChaoxingTaskMetadata.ReadRemaining("剩余30秒", now)?.DeadlineUtc == now.AddSeconds(30));
        Check(ChaoxingTaskMetadata.ReadRemaining("剩余0分钟", now)?.DeadlineUtc == now);
        foreach (var label in new[] { "剩余", "剩余两小时", "剩余-1小时", "剩余1小时 已截止", "剩余99999天", "已经截止", "无截止时间" })
            Check(ChaoxingTaskMetadata.ReadRemaining(label, now) is null);

        WorkAnswerWindow Window(string start, string end) => WorkDetailParser.ParseDisplayedWindow("作答时间:" + start + "至" + end).AnswerWindow!;
        var inferred = ChaoxingTaskMetadata.ReadWindow(Window("10-14 19:04", "10-25 23:59"), now, TaskState.Completed);
        Check(inferred is { Evidence: DeadlineEvidence.InferredYear }
            && inferred.DeadlineUtc == new DateTimeOffset(2026, 10, 25, 23, 59, 0, TimeSpan.FromHours(8)).ToUniversalTime());
        var full = ChaoxingTaskMetadata.ReadWindow(Window("2025-04-01 08:00", "2025-04-10 20:00"), now, TaskState.Completed);
        Check(full is { Evidence: DeadlineEvidence.PlatformDate }
            && full.DeadlineUtc == new DateTimeOffset(2025, 4, 10, 20, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime());
        var january = new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.FromHours(8));
        var crossYear = ChaoxingTaskMetadata.ReadWindow(Window("12-31 22:00", "01-02 23:59"), january, TaskState.Pending);
        Check(crossYear?.BeginUtc?.ToOffset(TimeSpan.FromHours(8)).Year == 2026
            && crossYear.DeadlineUtc.ToOffset(TimeSpan.FromHours(8)).Year == 2027);
        var historical = ChaoxingTaskMetadata.ReadWindow(Window("04-15 09:11", "04-30 09:11"), now, TaskState.Completed);
        Check(historical?.DeadlineUtc.ToOffset(TimeSpan.FromHours(8)).Year == 2026);
        var mixed = ChaoxingTaskMetadata.ReadWindow(Window("2026-12-31 22:00", "01-02 23:59"), january, TaskState.Pending);
        Check(mixed?.DeadlineUtc.ToOffset(TimeSpan.FromHours(8)).Year == 2027 && mixed.Evidence == DeadlineEvidence.InferredYear);
        Check(WorkDetailParser.ParseDisplayedWindow("提交时间:2026-10-20 12:00").AnswerWindow is null);
        Check(WorkDetailParser.ParseDisplayedWindow("作答时间:02-30 09:00至03-01 09:00").AnswerWindow is null);

        var estimatedTask = ChaoxingTaskMetadata.Apply(new TaskItem { Source = TaskSource.Chaoxing, PlatformStatusText = "未交" }, "剩余1小时", now);
        Check(TaskRules.Priority(estimatedTask, now) == 1 && TaskRules.HasUsableDeadline(estimatedTask) && !TaskRules.HasTrustedDeadline(estimatedTask));
        Check(TaskRules.Priority(estimatedTask with { DeadlineUtc = now.AddHours(10) }, now) == 2);
        Check(TaskRules.Priority(estimatedTask with { DeadlineUtc = now.AddHours(30) }, now) == 3);
        Check(TaskRules.Priority(estimatedTask with { DeadlineUtc = now.AddMinutes(-1) }, now) == 0);
        Check(ReminderPlanner.TimedPlans(estimatedTask, now).Count == 0 && TaskRules.DeadlineName(estimatedTask).StartsWith("约 ", StringComparison.Ordinal));
        var confirmed = TaskRules.ApplyDraft(estimatedTask, new(estimatedTask.Title.Length == 0 ? "合成作业" : estimatedTask.Title, "chaoxing", "合成课程",
            DeadlineKind.Known, null, estimatedTask.DeadlineUtc, null, null, "", "", null) { ConfirmDeadline = true }, now);
        Check(confirmed.Overrides.HasFlag(TaskOverrides.Deadline) && TaskRules.HasTrustedDeadline(confirmed)
            && ReminderPlanner.TimedPlans(confirmed, now).Count == 2 && !TaskRules.DeadlineName(confirmed).StartsWith("约 ", StringComparison.Ordinal));
        var inferredTask = ChaoxingTaskMetadata.Apply(estimatedTask with { DetailTimeText = "作答时间:10-14 19:04至10-25 23:59" }, "剩余50小时", now);
        Check(inferredTask.DeadlineEvidence == DeadlineEvidence.InferredYear && TaskRules.DeadlineName(inferredTask).Contains("年份推定", StringComparison.Ordinal));
        var trustedTask = ChaoxingTaskMetadata.Apply(estimatedTask with { DetailTimeText = "作答时间:2026-10-14 19:04至2026-10-25 23:59" }, "", now);
        Check(TaskRules.HasTrustedDeadline(trustedTask) && ReminderPlanner.TimedPlans(trustedTask, now).Count == 2);

        var root = Path.Combine(Path.GetTempPath(), "aggregate-metadata-" + Guid.NewGuid().ToString("N"));
        var store = new TaskStore(Path.Combine(root, "tasks.db"));
        WorkTaskLocation Location(int work) => WorkListParser.ParseTaskLocation(
            "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=201&cpi=301&workId=" + work + "&enc=SYNTHETIC")!;
        var row = new WorkListTask("合成作业", "未交", "剩余1小时", Location(401)) { TimeObservedAtUtc = now.ToUniversalTime() };
        store.ImportWorks([row], "合成课程", now.AddMinutes(5));
        var task = store.LoadTasks().Single();
        Check(task.State == TaskState.Pending && task.DeadlineUtc == now.AddHours(1)); // Parse-time timestamp excludes delayed saving.
        store.ImportWorks([row with { PlatformStatusText = "待批阅" }], "合成课程", now);
        task = store.Get(task.Id)!;
        Check(task.State == TaskState.Completed && !task.Overrides.HasFlag(TaskOverrides.State));
        store.ImportWorks([row], "合成课程", now);
        task = store.Get(task.Id)!;
        Check(task.State == TaskState.Pending); // Reopened platform work returns to pending.
        task = store.SetState(task.Id, task.Revision, TaskState.Ignored, now);
        store.ImportWorks([row with { PlatformStatusText = "已完成" }], "合成课程", now);
        task = store.Get(task.Id)!;
        Check(task.State == TaskState.Ignored && task.Overrides.HasFlag(TaskOverrides.State));
        task = store.SetState(task.Id, task.Revision, TaskState.Completed, now);
        task = store.SetState(task.Id, task.Revision, TaskState.Returned, now);
        store.ImportWorks([row with { PlatformStatusText = "已完成" }], "合成课程", now);
        task = store.Get(task.Id)!;
        Check(task.State == TaskState.Returned);
        var customDeadline = now.AddDays(9);
        task = store.Edit(task.Id, task.Revision, new(task.Title, "chaoxing", task.CourseName, DeadlineKind.Known,
            null, customDeadline, task.Link, null, "", "用户备注", null), now);
        store.SaveDetail(row.Location!, WorkDetailParser.ParseDisplayedWindow("作答时间:10-14 19:04至10-25 23:59"), now);
        task = store.Get(task.Id)!;
        Check(task.DeadlineUtc == customDeadline && task.State == TaskState.Returned && task.Notes == "用户备注");
        task = store.SetState(task.Id, task.Revision, TaskState.Pending, now);
        store.ImportWorks([row with { PlatformStatusText = "已完成" }], "合成课程", now);
        Check(store.Get(task.Id) is { State: TaskState.Pending } restored && restored.Overrides.HasFlag(TaskOverrides.State));

        var datedRow = row with { Location = Location(402), DisplayedTimeText = "剩余50小时" };
        store.ImportWorks([datedRow], "合成课程", now.AddMinutes(5));
        var dated = store.LoadTasks().Single(value => value.SourceKey == TaskStore.ObservedWorkKey(datedRow.Location));
        Check(dated.PlatformListTimeObservedAtUtc == now && dated.DeadlineUtc == now.AddHours(50));
        dated = store.SaveDetail(datedRow.Location!, WindowDetail("10-14 19:04", "10-25 23:59"), now.AddHours(3));
        Check(dated.PlatformListTimeObservedAtUtc == now && dated.DeadlineEvidence == DeadlineEvidence.InferredYear);
        var retained = ChaoxingTaskMetadata.Apply(dated, "", now.AddYears(1));
        Check(retained.DeadlineUtc == dated.DeadlineUtc && retained.BeginUtc == dated.BeginUtc);
        var olderHeader = new TaskItem { Source = TaskSource.Chaoxing, PlatformStatusText = "已完成",
            DetailTimeText = "作答时间:10-14 19:04至10-25 23:59", DetailCollectedAtUtc = now, CollectedAtUtc = now.AddYears(1) };
        Check(ChaoxingTaskMetadata.Apply(olderHeader, "", now.AddYears(1)).DeadlineUtc == inferred!.DeadlineUtc);
        var invalidWindow = new TaskItem { Source = TaskSource.Chaoxing, PlatformStatusText = "未交",
            DetailTimeText = "作答时间:2026-02-30 09:00至2026-03-01 09:00", DetailCollectedAtUtc = now.AddHours(3) };
        Check(ChaoxingTaskMetadata.Apply(invalidWindow, "剩余1小时", now, refreshWindow: true).DeadlineUtc == now.AddHours(1));
        var fixedWindow = new TaskItem { Source = TaskSource.Chaoxing, PlatformStatusText = "已完成",
            State = TaskState.Pending, Overrides = TaskOverrides.State, DetailTimeText = "作答时间:12-01 09:00至12-10 09:00" };
        Check(ChaoxingTaskMetadata.Apply(fixedWindow, "", now).DeadlineUtc?.ToOffset(TimeSpan.FromHours(8)).Year == 2025);

        var fractionalEstimate = estimatedTask with { Title = "合成作业", CourseName = "合成课程", DeadlineUtc = now.AddHours(1).AddSeconds(27).AddMilliseconds(123) };
        var unchangedText = fractionalEstimate.DeadlineUtc!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var parsedUnchanged = TaskRules.ParseEditedLocalTime(unchangedText, fractionalEstimate.DeadlineUtc);
        Check(parsedUnchanged == fractionalEstimate.DeadlineUtc);
        var notesOnly = TaskRules.ApplyDraft(fractionalEstimate, new(fractionalEstimate.Title, "chaoxing", fractionalEstimate.CourseName,
            DeadlineKind.Known, null, parsedUnchanged, null, null, "", "只改备注", null), now);
        Check(!notesOnly.Overrides.HasFlag(TaskOverrides.Deadline) && !TaskRules.HasTrustedDeadline(notesOnly)
            && ReminderPlanner.TimedPlans(notesOnly, now).Count == 0 && notesOnly.Notes == "只改备注");
        var changedText = now.AddDays(2).ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Check(TaskRules.ParseEditedLocalTime(changedText, fractionalEstimate.DeadlineUtc) == TaskRules.ParseLocalTime(changedText));
        Check(TaskRules.ParseEditedLocalTime("", fractionalEstimate.DeadlineUtc) is null);
        Check(TaskRules.ParseEditedLocalTime(unchangedText, null) == TaskRules.ParseLocalTime(unchangedText));

        WorkDetailParseResult WindowDetail(string start, string end) => WorkDetailParser.ParseDisplayedWindow("作答时间:" + start + "至" + end);

        // A schema-1 payload from the prior release has no metadata version or manual-state override bit.
        var legacy = new TaskItem { Id = "legacy", Source = TaskSource.Chaoxing, State = TaskState.Pending, PlatformStatusText = "已完成",
            DetailTimeText = "作答时间:10-14 19:04至10-25 23:59", CollectedAtUtc = now.ToUniversalTime() };
        var legacyCompleted = legacy with { Id = "legacy-manual", State = TaskState.Completed, PlatformStatusText = "未交" };
        using (var connection = new SqliteConnection("Data Source=" + store.DatabasePath))
        {
            connection.Open();
            foreach (var value in new[] { legacy, legacyCompleted })
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO tasks(id,revision,source_key,payload) VALUES($id,1,NULL,$payload);";
                command.Parameters.AddWithValue("$id", value.Id); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value)); command.ExecuteNonQuery();
            }
        }
        Check(store.RefreshAutomaticMetadata(now) == 2);
        var migrated = store.Get(legacy.Id)!;
        Check(migrated.State == TaskState.Completed && migrated.DeadlineEvidence == DeadlineEvidence.InferredYear
            && migrated.DeadlineUtc is not null && !migrated.Overrides.HasFlag(TaskOverrides.State));
        Check(store.Get(legacyCompleted.Id) is { State: TaskState.Completed } manual && manual.Overrides.HasFlag(TaskOverrides.State));
        Check(store.RefreshAutomaticMetadata(now.AddDays(1)) == 0);
        Console.WriteLine($"PASS: {count} platform-state mapping, automatic dates, visible estimates, priorities, manual preservation and migration checks (synthetic data only).");
    }
}
