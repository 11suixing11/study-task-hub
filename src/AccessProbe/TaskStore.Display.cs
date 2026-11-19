namespace AccessProbe;

public sealed partial class TaskStore
{
    // List pages need task metadata, not the protected official URL. Resolve that
    // URL through Get when opening or editing one task, rather than visiting the
    // Android keystore for every task whenever a page or filter changes.
    public IReadOnlyList<TaskItem> LoadTaskSummaries(CancellationToken cancellation = default)
        => LoadTaskSummaries(recordedOnly: false, cancellation);

    public IReadOnlyList<TaskItem> LoadRecordedTaskSummaries(CancellationToken cancellation = default)
        => LoadTaskSummaries(recordedOnly: true, cancellation);

    private IReadOnlyList<TaskItem> LoadTaskSummaries(bool recordedOnly, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var recordings = recordedOnly
                ? PlatformCatalog.Sites.ToDictionary(site => site.Id,
                    site => ReadCourseRecording(connection, transaction, site.Id), StringComparer.Ordinal)
                : null;
            using var command = Command(connection, transaction,
                "SELECT id,revision,payload,NULL AS protected_link FROM tasks ORDER BY id;");
            using var reader = command.ExecuteReader();
            var tasks = new List<TaskItem>();
            while (reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                var task = ReadItem(reader, readLink: false);
                if (recordings is null || !recordings.TryGetValue(task.PlatformId, out var recording)
                    || CourseRecordingRules.IsVisible(recording, task)) tasks.Add(task);
            }
            return tasks;
        }
    }
}
