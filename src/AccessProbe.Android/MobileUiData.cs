using AccessProbe;

namespace AccessProbe.Mobile;

public sealed record MobileDashboardSnapshot(
    long Revision,
    DateTimeOffset LoadedAtUtc,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<string> ConnectedPlatforms,
    int IncompleteCount,
    int ActiveCount,
    int OverdueCount,
    int DueWithin24HoursCount,
    int CompletedCount,
    IReadOnlyList<TaskCourseFilterOption> CourseOptions)
{
    public int TaskCount => Tasks.Count;
    public int ConnectedCount => ConnectedPlatforms.Count;
    internal DateTimeOffset RefreshAfterUtc { get; init; }
}

public sealed record MobileAutomaticSyncPlan(bool Enabled, IReadOnlyList<string> Platforms);
public sealed record MobileTaskResumeSnapshot(TaskItem? Task, bool ShouldSync);

public sealed record MobilePlatformSummarySnapshot(
    string PlatformId,
    int VisibleTaskCount,
    CourseRecordingSettings Recording,
    string SessionDescription,
    ConnectionReport? ConnectionReport,
    MobilePlatformStatus SyncStatus)
{
    public int SelectedCourseCount => Recording.Courses.Count(course => CourseRecordingRules.IsSelected(Recording, course.Key));
}

public sealed record MobilePlatformsSnapshot(
    long Revision,
    DateTimeOffset LoadedAtUtc,
    IReadOnlyList<MobilePlatformSummarySnapshot> Platforms);

public sealed record MobilePlatformSnapshot(
    long Revision,
    DateTimeOffset LoadedAtUtc,
    string PlatformId,
    CourseRecordingSettings Recording,
    string SessionDescription,
    ConnectionReport? ConnectionReport,
    UaiPortalSummary? UaiSummary);

/// <summary>Reads local data on workers; cached snapshots contain no platform credentials.</summary>
public sealed class MobileUiData : IDisposable
{
    private static readonly TimeSpan MaximumDashboardAge = TimeSpan.FromSeconds(30);
    private readonly TaskStore store;
    private readonly MobileVault vault;
    private readonly string[] platformIds;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly object cacheGate = new();
    private readonly SemaphoreSlim dashboardLoadGate = new(1, 1);
    private MobileDashboardSnapshot? dashboard;
    private long revision;
    private bool disposed;

    public MobileUiData(TaskStore store, MobileVault vault, IEnumerable<string>? platformIds = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));
        this.platformIds = (platformIds ?? PlatformCatalog.Sites.Select(site => site.Id)
            .Where(CourseRecordingRules.SupportsAutomatic)).Distinct(StringComparer.Ordinal).ToArray();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        store.Changed += Invalidate;
    }

    public long Revision { get { lock (cacheGate) return revision; } }

    public void Invalidate()
    {
        // TaskStore.Changed may be raised while MobileVault holds its lock. Never read either
        // store or vault under cacheGate, including from this event handler.
        lock (cacheGate)
        {
            if (disposed) return;
            revision++;
            dashboard = null;
        }
    }

    public bool TryGetCachedDashboard(out MobileDashboardSnapshot snapshot)
    {
        var now = utcNow();
        lock (cacheGate)
        {
            if (!disposed && dashboard is { } cached && cached.Revision == revision
                && now >= cached.LoadedAtUtc && now < cached.RefreshAfterUtc)
            {
                snapshot = cached;
                return true;
            }
        }
        snapshot = null!;
        return false;
    }

    public bool IsCurrent(MobileDashboardSnapshot snapshot)
    {
        var now = utcNow();
        lock (cacheGate)
            return !disposed && ReferenceEquals(dashboard, snapshot) && snapshot.Revision == revision
                && now >= snapshot.LoadedAtUtc && now < snapshot.RefreshAfterUtc;
    }

    public async Task<MobileDashboardSnapshot> LoadDashboardAsync(bool force = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (force) Invalidate();
        if (TryGetCachedDashboard(out var cached)) return cached;

        await dashboardLoadGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (TryGetCachedDashboard(out cached)) return cached;
                long expectedRevision;
                lock (cacheGate) expectedRevision = revision;

                var loaded = await Task.Run(() => ReadDashboard(expectedRevision, cancellation), cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var publicationTime = utcNow();
                lock (cacheGate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    // A change between the task read and the platform reads invalidates the
                    // whole snapshot. Re-read rather than publishing mixed or older data.
                    if (expectedRevision != revision || publicationTime < loaded.LoadedAtUtc
                        || publicationTime >= loaded.RefreshAfterUtc) continue;
                    dashboard = loaded;
                    return loaded;
                }
            }
        }
        finally { dashboardLoadGate.Release(); }
    }

    private MobileDashboardSnapshot ReadDashboard(long expectedRevision, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var tasks = store.LoadRecordedTaskSummaries(cancellation).ToArray();
        var connected = new List<string>();
        var incomplete = 0;
        foreach (var platform in platformIds)
        {
            cancellation.ThrowIfCancellationRequested();
            if (vault.LoadForRevalidation(platform) is null) continue;
            connected.Add(platform);
            if (MobilePlatformStatus.Read(store, platform).Outcome != "completed") incomplete++;
        }

        var now = utcNow();
        var active = 0;
        var overdue = 0;
        var dueWithin24Hours = 0;
        var completed = 0;
        var refreshAfter = now.Add(MaximumDashboardAge);
        foreach (var task in tasks)
        {
            cancellation.ThrowIfCancellationRequested();
            if (task.State == TaskState.Completed) completed++;
            if (!TaskRules.IsActive(task)) continue;
            active++;
            if (!TaskRules.HasUsableDeadline(task) || task.DeadlineUtc is not { } deadline) continue;
            if (deadline < now) overdue++;
            else
            {
                if (deadline - now <= TimeSpan.FromHours(24)) dueWithin24Hours++;
                // Overdue is strictly after the deadline; refresh even when a cached count
                // is used on a page that crosses that boundary without a database change.
                var becomesOverdue = deadline == DateTimeOffset.MaxValue ? deadline : deadline.AddTicks(1);
                if (becomesOverdue < refreshAfter) refreshAfter = becomesOverdue;
                var enters24Hours = deadline.Subtract(TimeSpan.FromHours(24));
                if (enters24Hours > now && enters24Hours < refreshAfter) refreshAfter = enters24Hours;
            }
        }
        var courses = TaskFiltering.Courses(tasks).ToArray();
        cancellation.ThrowIfCancellationRequested();
        return new(expectedRevision, now, Array.AsReadOnly(tasks), connected.AsReadOnly(), incomplete,
            active, overdue, dueWithin24Hours, completed, Array.AsReadOnly(courses)) { RefreshAfterUtc = refreshAfter };
    }

    public Task<MobileAutomaticSyncPlan> LoadAutomaticSyncPlanAsync(CancellationToken cancellation = default)
        => Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (!store.LoadSettings().AutomaticSyncEnabled)
                return new MobileAutomaticSyncPlan(false, Array.Empty<string>());
            var platforms = new List<string>();
            foreach (var platform in platformIds)
            {
                cancellation.ThrowIfCancellationRequested();
                if (vault.LoadForRevalidation(platform) is not null
                    && (platform == UaiPortal.Id || store.LoadCourseRecording(platform).SelectionConfirmed))
                    platforms.Add(platform);
            }
            cancellation.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return new MobileAutomaticSyncPlan(true, platforms.AsReadOnly());
        }, cancellation);

    public Task<MobileTaskResumeSnapshot> LoadTaskForResumeAsync(string id, CancellationToken cancellation = default)
        => Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var task = store.Get(id);
            var shouldSync = task is not null && store.LoadSettings().AutomaticSyncEnabled && vault.Load(task.PlatformId) is not null;
            cancellation.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return new MobileTaskResumeSnapshot(task, shouldSync);
        }, cancellation);

    public bool IsCurrent(MobilePlatformsSnapshot snapshot) => IsCurrentPageRevision(snapshot.Revision);
    public bool IsCurrent(MobilePlatformSnapshot snapshot) => IsCurrentPageRevision(snapshot.Revision);

    public Task<MobilePlatformsSnapshot> LoadPlatformsAsync(CancellationToken cancellation = default)
        => LoadStablePageAsync(ReadPlatforms, cancellation);

    private MobilePlatformsSnapshot ReadPlatforms(long expectedRevision, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Read all task payloads once. Neither the list counts nor session descriptions
        // need URLs or protected credentials, so this path never calls MobileVault.
        var tasksByPlatform = store.LoadTaskSummaries(cancellation).GroupBy(task => task.PlatformId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var platforms = new List<MobilePlatformSummarySnapshot>(platformIds.Length);
        foreach (var platform in platformIds)
        {
            cancellation.ThrowIfCancellationRequested();
            var recording = store.LoadCourseRecording(platform);
            var visibleCount = 0;
            if (tasksByPlatform.TryGetValue(platform, out var tasks))
                foreach (var task in tasks)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (CourseRecordingRules.IsVisible(recording, task)) visibleCount++;
                }
            var sessionDescription = store.LoadPlatformSessionInfo(platform).Description;
            cancellation.ThrowIfCancellationRequested();
            var connection = store.LoadConnectionReport(platform);
            cancellation.ThrowIfCancellationRequested();
            var syncStatus = MobilePlatformStatus.Read(store, platform);
            platforms.Add(new(platform, visibleCount, recording, sessionDescription, connection, syncStatus));
        }
        cancellation.ThrowIfCancellationRequested();
        return new(expectedRevision, utcNow(), platforms.AsReadOnly());
    }

    public Task<MobilePlatformSnapshot> LoadPlatformAsync(string platform, CancellationToken cancellation = default)
        => LoadStablePageAsync((expectedRevision, token) => ReadPlatform(platform, expectedRevision, token), cancellation);

    private MobilePlatformSnapshot ReadPlatform(string platform, long expectedRevision, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // The overview includes unselected local courses for course management. Its
        // TaskStore API reads task summaries, keeping URL decryption out of this page.
        var recording = store.LoadCourseRecordingOverview(platform);
        cancellation.ThrowIfCancellationRequested();
        var sessionDescription = store.LoadPlatformSessionInfo(platform).Description;
        cancellation.ThrowIfCancellationRequested();
        var connection = store.LoadConnectionReport(platform);
        cancellation.ThrowIfCancellationRequested();
        var uaiSummary = platform == UaiPortal.Id ? store.LoadUaiPortalSummary() : null;
        cancellation.ThrowIfCancellationRequested();
        return new(expectedRevision, utcNow(), platform, recording, sessionDescription, connection, uaiSummary);
    }

    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellation = default)
        => LoadStablePageAsync((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return store.LoadSettings();
        }, cancellation);

    private async Task<T> LoadStablePageAsync<T>(Func<long, CancellationToken, T> read, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            long expectedRevision;
            lock (cacheGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                expectedRevision = revision;
            }
            // Never hold cacheGate while entering TaskStore or waiting on a worker.
            var loaded = await Task.Run(() => read(expectedRevision, cancellation), cancellation).ConfigureAwait(false);
            lock (cacheGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellation.ThrowIfCancellationRequested();
                if (expectedRevision == revision) return loaded;
            }
            // An active import can invalidate multiple reads in succession. Yield
            // briefly before retrying so cancellation stays responsive without spinning.
            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
    }

    private bool IsCurrentPageRevision(long expectedRevision)
    {
        lock (cacheGate) return !disposed && expectedRevision == revision;
    }

    private void ThrowIfDisposed()
    {
        lock (cacheGate) ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        lock (cacheGate)
        {
            if (disposed) return;
            disposed = true;
            revision++;
            dashboard = null;
        }
        store.Changed -= Invalidate;
        // An in-flight read can still release dashboardLoadGate. It owns no unmanaged
        // handle, so let it be collected after those callers have finished.
    }
}
