using AccessProbe;
using AccessProbe.Mobile;

public sealed record MobileUiDataCheckResult(string Outcome, int Checks, IReadOnlyList<string> Results);

public static class MobileUiDataChecks
{
    public static async Task<MobileUiDataCheckResult> RunAsync(string directory, ILocalLinkProtector protector)
    {
        Directory.CreateDirectory(directory);
        var results = new List<string>();
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException("Mobile UI data check failed: " + label);
            results.Add(label);
        }
        async Task Cancelled(Task action, string label)
        {
            try { await action.ConfigureAwait(false); throw new InvalidDataException("Cancellation was ignored: " + label); }
            catch (OperationCanceledException) { Check(true, label); }
        }
        async Task Disposed(Task action, string label)
        {
            try { await action.ConfigureAwait(false); throw new InvalidDataException("Disposal was ignored: " + label); }
            catch (ObjectDisposedException) { Check(true, label); }
        }

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var observed = new ObservedProtector(protector);
        var store = new TaskStore(Path.Combine(directory, "dashboard-synthetic.db"), observed);
        var vault = new MobileVault(store, observed);
        var now = clock.Read();
        TaskItem Create(string title, string course, DateTimeOffset? deadline) => store.Create(new(title, "pta", course,
            deadline.HasValue ? DeadlineKind.Known : DeadlineKind.Unknown, null, deadline,
            "https://pintia.cn/problem-sets/920001/exam/overview", null, "", "", null), now);
        var overdue = Create("合成逾期任务", "合成课程", now.AddHours(-1));
        var upcoming = Create("合成近期任务", "合成课程", now.AddHours(5));
        var future = Create("合成未来任务", "合成课程", now.AddDays(2));
        var boundary = Create("合成截止边界", "合成课程", now.AddSeconds(4));
        var completed = Create("合成已完成任务", "合成课程", now.AddHours(2));
        store.SetState(completed.Id, completed.Revision, TaskState.Completed, now);
        var unknown = Create("合成未知截止", "合成课程", null);
        var hidden = Create("合成未选课程任务", "未选合成课程", now.AddHours(-2));
        var recording = store.LoadCourseRecordingOverview("pta");
        store.SaveCourseRecording("pta", false, [CourseRecordingRules.TaskCourseKey(upcoming)], true, recording.Revision, now);
        vault.Save(new(Guid.NewGuid().ToString("N"), "pta", now,
            [new("PTASession", "SYNTHETIC_UI_DATA_ONLY", "pintia.cn", "/", true, true, null, "Lax")]));
        store.SaveSettings(new(false, false) { AutomaticSyncEnabled = true });

        using var data = new MobileUiData(store, vault, ["pta"], clock.Read);
        Check(!data.TryGetCachedDashboard(out _), "cold cache performs no synchronous load");
        observed.Reset();
        Task<MobileDashboardSnapshot> load;
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new MarkedUiContext());
            load = data.LoadDashboardAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        var snapshot = await load.ConfigureAwait(false);
        Check(!observed.ReadOnUiContext && observed.Reads == 1,
            "dashboard decrypts only one platform session on a worker, never task links on the UI context");
        Check(snapshot.TaskCount == 6 && snapshot.Tasks.All(task => task.Id != hidden.Id && task.Link is null),
            "recorded task summaries respect course selection and omit protected links");
        Check(snapshot.ActiveCount == 5 && snapshot.OverdueCount == 1 && snapshot.DueWithin24HoursCount == 2
            && snapshot.CompletedCount == 1, "background summary preserves active, deadline and completion semantics");
        Check(snapshot.ConnectedCount == 1 && snapshot.ConnectedPlatforms.Single() == "pta" && snapshot.IncompleteCount == 1,
            "connected platform count uses validated saved sessions and retains incomplete coverage");
        Check(snapshot.CourseOptions.Count == 2 && snapshot.CourseOptions.Any(course => course.Key == CourseRecordingRules.TaskCourseKey(upcoming)),
            "course options are prepared once with the same recorded tasks");
        Check(data.TryGetCachedDashboard(out var cached) && ReferenceEquals(snapshot, cached)
            && ReferenceEquals(snapshot, await data.LoadDashboardAsync().ConfigureAwait(false)) && observed.Reads == 1,
            "repeated loads reuse the same current snapshot without credential or task reads");

        clock.Advance(TimeSpan.FromSeconds(5));
        Check(!data.TryGetCachedDashboard(out _) && !data.IsCurrent(snapshot),
            "crossing a deadline expires cached counts without a database change");
        var afterDeadline = await data.LoadDashboardAsync().ConfigureAwait(false);
        Check(afterDeadline.OverdueCount == 2 && afterDeadline.DueWithin24HoursCount == 1,
            "refreshed counts reflect a newly overdue active task");
        clock.Advance(TimeSpan.FromSeconds(31));
        Check(!data.TryGetCachedDashboard(out _), "cached session and coverage counts also have a bounded age");
        snapshot = await data.LoadDashboardAsync().ConfigureAwait(false);
        var initialRevision = snapshot.Revision;
        store.SaveMetadataSettings("pta", store.LoadMetadataSettings("pta") with
            { LastSyncOutcome = "completed", LastSyncAtUtc = clock.Read() });
        Check(!data.TryGetCachedDashboard(out _) && !data.IsCurrent(snapshot) && data.Revision > initialRevision,
            "TaskStore.Changed invalidates both the cache and existing snapshot revisions");
        snapshot = await data.LoadDashboardAsync().ConfigureAwait(false);
        Check(snapshot.IncompleteCount == 0, "coverage changes appear on the next dashboard load");

        observed.Reset();
        data.Invalidate();
        var concurrent = Enumerable.Range(0, 8).Select(_ => data.LoadDashboardAsync()).ToArray();
        var shared = await Task.WhenAll(concurrent).ConfigureAwait(false);
        Check(shared.All(item => ReferenceEquals(item, shared[0])) && observed.Reads == 1,
            "concurrent readers share one database snapshot load");

        using (var barrier = observed.BlockNextRead())
        {
            var pending = data.LoadDashboardAsync(force: true);
            try
            {
                await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var beforeChange = data.Revision;
                // Unprotect is paused under MobileVault's lock. These operations must not
                // wait on that lock through a cache/store lock inversion.
                data.Invalidate();
                var added = Create("合成读取途中新增", "合成课程", now.AddHours(6));
                Check(data.Revision > beforeChange && !data.TryGetCachedDashboard(out _),
                    "invalidation and task writes complete while a background vault read is paused");
                barrier.Release();
                var refreshed = await pending.ConfigureAwait(false);
                Check(refreshed.Tasks.Any(task => task.Id == added.Id) && data.IsCurrent(refreshed),
                    "a mutation during snapshot loading discards older results and reloads current data");
            }
            finally { barrier.Release(); }
        }

        using (var barrier = observed.BlockNextRead())
        {
            var pending = data.LoadDashboardAsync(force: true);
            try
            {
                await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                using var cancellation = new CancellationTokenSource();
                var follower = data.LoadDashboardAsync(cancellation: cancellation.Token);
                cancellation.Cancel();
                await Cancelled(follower, "a waiting view can cancel without cancelling another view's load");
                barrier.Release();
                var survived = await pending.ConfigureAwait(false);
                Check(data.IsCurrent(survived), "an independent load remains valid after a cancelled waiter");
            }
            finally { barrier.Release(); }
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var beforeRead = observed.Reads;
            await Cancelled(data.LoadDashboardAsync(force: true, cancellation: cancellation.Token),
                "an already cancelled load does not invalidate or start database work");
            Check(observed.Reads == beforeRead && data.TryGetCachedDashboard(out _),
                "pre-cancellation leaves current cached data available");
        }

        observed.FailNextRead();
        try { await data.LoadDashboardAsync(force: true).ConfigureAwait(false); throw new InvalidDataException("Synthetic failure was ignored."); }
        catch (SyntheticReadException) { Check(true, "unexpected local read failures propagate to the view for retry"); }
        Check(!data.TryGetCachedDashboard(out _), "a failed snapshot is never published as an empty task list");
        snapshot = await data.LoadDashboardAsync().ConfigureAwait(false);
        Check(data.IsCurrent(snapshot) && snapshot.TaskCount == 7, "retry recovers the loader after a local read failure");

        var plan = await data.LoadAutomaticSyncPlanAsync().ConfigureAwait(false);
        Check(plan.Enabled && plan.Platforms.SequenceEqual(["pta"]),
            "automatic-sync settings, session and selected-course checks run through the worker plan");
        var resume = await data.LoadTaskForResumeAsync(upcoming.Id).ConfigureAwait(false);
        Check(resume.Task?.Id == upcoming.Id && resume.Task.Link is not null && resume.ShouldSync,
            "task resume retains the full navigation link and checks saved credentials off the UI context");
        store.SaveSettings(store.LoadSettings() with { AutomaticSyncEnabled = false });
        Check(!(await data.LoadAutomaticSyncPlanAsync().ConfigureAwait(false)).Enabled
            && !(await data.LoadTaskForResumeAsync(upcoming.Id).ConfigureAwait(false)).ShouldSync,
            "disabled automatic synchronization also stops automatic task-resume refreshes");
        store.ClearPlatformSession("pta");
        Check((await data.LoadDashboardAsync().ConfigureAwait(false)).ConnectedCount == 0,
            "clearing a saved login updates dashboard connection counts");

        // Recreate a saved session so the final disposal check can pause an in-flight
        // worker inside credential loading without needing network or an Android device.
        vault.Save(new(Guid.NewGuid().ToString("N"), "pta", now,
            [new("PTASession", "SYNTHETIC_UI_DATA_ONLY", "pintia.cn", "/", true, true, null, "Lax")]));
        using (var barrier = observed.BlockNextRead())
        {
            var pending = data.LoadDashboardAsync(force: true);
            try
            {
                await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                data.Dispose();
                barrier.Release();
                await Disposed(pending, "an in-flight read cannot publish a snapshot after disposal");
            }
            finally { barrier.Release(); }
        }
        Check(!data.TryGetCachedDashboard(out _) && !data.IsCurrent(snapshot), "disposed data invalidates old view snapshots");
        await Disposed(data.LoadDashboardAsync(), "disposed services reject new dashboard work");
        var disposedRevision = data.Revision;
        store.SaveSettings(store.LoadSettings() with { NotificationsEnabled = false });
        Check(data.Revision == disposedRevision, "disposed cache is detached from TaskStore.Changed");
        return new("passed", results.Count, results);
    }

    private sealed class MarkedUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => throw new InvalidOperationException("Local dashboard reads captured the UI context.");
    }

    private sealed class TestClock(DateTimeOffset value)
    {
        private readonly object gate = new();
        public DateTimeOffset Read() { lock (gate) return value; }
        public void Advance(TimeSpan amount) { lock (gate) value = value.Add(amount); }
    }

    private sealed class SyntheticReadException : Exception { }

    private sealed class ObservedProtector(ILocalLinkProtector inner) : ILocalLinkProtector
    {
        private int reads;
        private int readOnUiContext;
        private int failNextRead;
        private ReadBarrier? nextBarrier;
        public int Reads => Volatile.Read(ref reads);
        public bool ReadOnUiContext => Volatile.Read(ref readOnUiContext) != 0;
        public byte[] Protect(string value) => inner.Protect(value);
        public string Unprotect(byte[] value)
        {
            Interlocked.Increment(ref reads);
            if (SynchronizationContext.Current is MarkedUiContext) Interlocked.Exchange(ref readOnUiContext, 1);
            var barrier = Interlocked.Exchange(ref nextBarrier, null);
            if (barrier is not null)
            {
                barrier.Started.TrySetResult(true);
                if (!barrier.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic read barrier timed out.");
            }
            if (Interlocked.Exchange(ref failNextRead, 0) != 0) throw new SyntheticReadException();
            return inner.Unprotect(value);
        }
        public void Reset() { Interlocked.Exchange(ref reads, 0); Interlocked.Exchange(ref readOnUiContext, 0); }
        public void FailNextRead() => Interlocked.Exchange(ref failNextRead, 1);
        public ReadBarrier BlockNextRead()
        {
            var barrier = new ReadBarrier();
            if (Interlocked.CompareExchange(ref nextBarrier, barrier, null) is not null)
                throw new InvalidOperationException("A synthetic read is already blocked.");
            return barrier;
        }
    }

    private sealed class ReadBarrier : IDisposable
    {
        private readonly ManualResetEventSlim released = new();
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Wait(TimeSpan timeout) => released.Wait(timeout);
        public void Release() => released.Set();
        public void Dispose() { Release(); released.Dispose(); }
    }
}
