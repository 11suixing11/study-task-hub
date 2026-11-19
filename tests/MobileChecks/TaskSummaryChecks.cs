using AccessProbe;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

internal static class TaskSummaryChecks
{
    public static void Run(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tasks.db");
        var now = DateTimeOffset.UtcNow;
        var protector = new CountingProtector();
        var store = new TaskStore(path, protector);
        store.SaveSettings(new(false, true) { AutomaticSyncEnabled = false });
        var first = store.Create(new("显示元数据", "pta", "本地课程", DeadlineKind.Known, null,
            now.AddDays(2), "https://example.invalid/task/SYNTHETIC-ONLY", null, "", "合成备注", null), now);
        var second = store.Create(new("另一门课程", "mooc", "第二课程", DeadlineKind.Unknown,
            null, null, "https://example.invalid/second/SYNTHETIC-ONLY", null, "", "", null), now);
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidDataException(label);
            checks++;
        }
        protector.Reads = 0;
        var summaries = store.LoadTaskSummaries();
        Check(summaries.Count == 2 && summaries.All(task => task.Link is null && task.LinkIssue is null)
            && protector.Reads == 0, "list metadata is read without decrypting protected official links");
        Check(summaries.Single(task => task.Id == first.Id) == first with { Link = null },
            "summary retains all task fields and revisions except the protected URL");
        Check(store.Get(first.Id)!.Link == first.Link && protector.Reads == 1,
            "opening a single task still decrypts its original official URL");
        var recording = store.LoadCourseRecordingOverview("pta");
        Check(protector.Reads == 1 && recording.Courses.Single().Key == CourseRecordingRules.TaskCourseKey(first),
            "course overview builds historical course identity without decrypting task links");
        store.SaveCourseRecording("pta", false, [], true, recording.Revision, now);
        var full = store.LoadRecordedTasks().Select(task => task with { Link = null, LinkIssue = null }).ToArray();
        protector.Reads = 0;
        Check(store.LoadRecordedTaskSummaries().SequenceEqual(full) && protector.Reads == 0
            && store.LoadRecordedTaskSummaries().Single().Id == second.Id,
            "recorded summaries preserve course visibility without deleting excluded task history");
        var selected = store.LoadCourseRecording("pta");
        store.SaveCourseRecording("pta", false, [CourseRecordingRules.TaskCourseKey(first)], true, selected.Revision, now);
        Check(store.LoadRecordedTaskSummaries().Count == 2, "reselecting a course restores its task summary");
        selected = store.LoadCourseRecording("pta");
        store.SaveCourseRecording("pta", false, [], false, selected.Revision, now);
        Check(store.LoadRecordedTaskSummaries().Count == 2, "retained history follows the existing hide-unselected preference");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { store.LoadTaskSummaries(cancelled.Token); throw new InvalidDataException("cancelled summary read accepted"); }
        catch (OperationCanceledException) { checks++; }
        protector.RejectReads = true;
        Check(store.LoadTaskSummaries().Count == 2 && store.Get(first.Id)!.LinkIssue == "LINK002",
            "a temporarily unreadable protected link cannot block list metadata or conceal the detail link error");
        using var connection = new SqliteConnection("Data Source=" + path); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET payload=json_set(payload,'$.Revision',999) WHERE id=$id";
        command.Parameters.AddWithValue("$id", first.Id); command.ExecuteNonQuery();
        try { store.LoadTaskSummaries(); throw new InvalidDataException("invalid task metadata accepted"); }
        catch (TaskValidationException error) { Check(error.Code == "DB002", "summary reads retain task payload integrity validation"); }
        Console.WriteLine($"PASS: {checks} list metadata, preserved protected links, visibility and cancellation checks (synthetic only).");
    }

    private sealed class CountingProtector : ILocalLinkProtector
    {
        public int Reads { get; set; }
        public bool RejectReads { get; set; }
        public byte[] Protect(string value) => Encoding.UTF8.GetBytes(value);
        public string Unprotect(byte[] value)
        {
            Reads++;
            if (RejectReads) throw new CryptographicException("synthetic_protector_unavailable");
            return Encoding.UTF8.GetString(value);
        }
    }
}
