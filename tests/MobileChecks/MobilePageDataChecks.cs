using AccessProbe;
using AccessProbe.Mobile;
using Microsoft.Data.Sqlite;

public sealed record MobilePageDataCheckResult(string Outcome, int Checks, IReadOnlyList<string> Results);

public static class MobilePageDataChecks
{
    public static async Task<MobilePageDataCheckResult> RunAsync(string directory, ILocalLinkProtector protector)
    {
        Directory.CreateDirectory(directory);
        var results = new List<string>();
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException("Mobile page data check failed: " + label);
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

        var now = DateTimeOffset.UtcNow;
        var clock = new PageReadClock(now);
        var observed = new ObservedProtector(protector);
        var store = new TaskStore(Path.Combine(directory, "pages-synthetic.db"), observed);
        var vault = new MobileVault(store, observed);
        TaskItem Create(string platform, string title, string course) => store.Create(new(title, platform, course,
            DeadlineKind.Known, null, now.AddHours(3), "https://pintia.cn/problem-sets/920001/exam/overview",
            null, "", "", null), now);
        var first = Create("pta", "合成平台任务一", "共同合成课程");
        Create("pta", "合成平台任务二", "共同合成课程");
        Create("pta", "合成未选任务", "未选合成课程");
        Create("mooc", "合成MOOC任务", "共同合成课程");
        Create("uai", "合成AI手动任务", "AI手动合成课程");
        Create("fif", "合成仅手动任务", "未显示平台课程");
        var recording = store.LoadCourseRecordingOverview("pta");
        store.SaveCourseRecording("pta", false, [CourseRecordingRules.TaskCourseKey(first)], true, recording.Revision, now);
        vault.Save(new(Guid.NewGuid().ToString("N"), "pta", now,
            [new("PTASession", "SYNTHETIC_PAGE_DATA_ONLY", "pintia.cn", "/", true, true, null, "Lax")]));
        store.ConfirmPlatformSession("pta", now);
        store.ExpirePlatformSession("mooc", now);
        var report = store.SaveConnectionReport(new("pta")
        {
            CheckedAtUtc = now, AccountVerified = true, CatalogCovered = true, Courses = 2, SelectedCourses = 1,
            SelectionConfirmed = true, Tasks = 2, TaskListsCovered = false
        });
        store.SaveMetadataSettings("pta", store.LoadMetadataSettings("pta") with { LastSyncAtUtc = now, LastSyncOutcome = "partial" });
        var uaiSummary = new UaiPortalSummary(now, "partial", true, 5, 7, 4, true, []);
        store.SaveUaiPortalSummary(uaiSummary, CancellationToken.None);
        var settings = new AppSettings(false, true) { AutomaticSyncEnabled = false, ChaoxingAutomaticSyncEnabled = false };
        store.SaveSettings(settings);

        using var data = new MobileUiData(store, vault, ["pta", "mooc", "uai"], clock.Read);
        observed.Reset();
        Task<MobilePlatformsSnapshot> load;
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new MarkedUiContext());
            load = data.LoadPlatformsAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        var platforms = await load.ConfigureAwait(false);
        Check(!clock.ReadOnUiContext && observed.Reads == 0,
            "platform pages read on a worker without decrypting any task URL or saved platform session");
        Check(platforms.Platforms.Select(platform => platform.PlatformId).SequenceEqual(["pta", "mooc", "uai"]),
            "platform summaries preserve the configured platform order and exclude unlisted manual platforms");
        var pta = platforms.Platforms.Single(platform => platform.PlatformId == "pta");
        var mooc = platforms.Platforms.Single(platform => platform.PlatformId == "mooc");
        var uai = platforms.Platforms.Single(platform => platform.PlatformId == "uai");
        Check(pta.VisibleTaskCount == 2 && mooc.VisibleTaskCount == 1 && uai.VisibleTaskCount == 1,
            "one task-summary batch gives per-platform visible counts with course selection applied");
        Check(pta.SelectedCourseCount == 1 && pta.Recording.SelectionConfirmed && mooc.Recording.Courses.Count == 0,
            "platform list recording settings retain saved course-selection semantics");
        Check(pta.SessionDescription.Contains("上次登录已确认", StringComparison.Ordinal)
            && mooc.SessionDescription.Contains("官网要求重新确认", StringComparison.Ordinal)
            && uai.SessionDescription.Contains("尚未保存登录", StringComparison.Ordinal),
            "connection descriptions distinguish confirmed, expired and missing saved sessions without vault decryption");
        Check(pta.ConnectionReport?.CheckedAtUtc == report.CheckedAtUtc && pta.ConnectionReport.AccountVerified
            && mooc.ConnectionReport is null, "platform summaries include the saved connection report without inventing one");
        Check(pta.SyncStatus.Outcome == "partial" && pta.SyncStatus.CheckedAtUtc == now && mooc.SyncStatus.Outcome == "not_started"
            && uai.SyncStatus.Outcome == "partial", "partial and absent synchronization coverage stay explicit");

        var ptaDetail = await data.LoadPlatformAsync("pta").ConfigureAwait(false);
        var moocDetail = await data.LoadPlatformAsync("mooc").ConfigureAwait(false);
        var uaiDetail = await data.LoadPlatformAsync("uai").ConfigureAwait(false);
        Check(ptaDetail.Recording.Courses.Count == 2 && ptaDetail.Recording.SelectedCourseKeys.Count == 1,
            "platform details retain both selected and unselected courses for course management");
        Check(moocDetail.Recording.Courses.Count == 1 && !moocDetail.Recording.SelectionConfirmed,
            "course overview includes local courses missing from the saved official catalog");
        Check(ptaDetail.ConnectionReport?.CheckedAtUtc == report.CheckedAtUtc && ptaDetail.UaiSummary is null
            && uaiDetail.UaiSummary is { } savedUai && savedUai.CheckedAtUtc == uaiSummary.CheckedAtUtc
            && savedUai.Outcome == uaiSummary.Outcome && savedUai.ShelfResources == uaiSummary.ShelfResources
            && savedUai.OverviewTasks == uaiSummary.OverviewTasks && savedUai.DisplayedTasks == uaiSummary.DisplayedTasks
            && !savedUai.FullTaskListsCovered,
            "only the AI platform detail includes its saved partial portal overview");
        Check(await data.LoadSettingsAsync().ConfigureAwait(false) == settings && observed.Reads == 0,
            "settings and all platform details load without task-link or platform-credential decryption");
        var preferenceStore = new TaskStore(Path.Combine(directory, "preferences-synthetic.db"), observed);
        preferenceStore.SaveSettings(settings);
        await Task.WhenAll(Task.Run(() => preferenceStore.UpdateSettings(saved => saved with { AutomaticSyncEnabled = true })),
            Task.Run(() => preferenceStore.UpdateSettings(saved => saved with { NotificationsEnabled = true })));
        var concurrentPreferences = preferenceStore.LoadSettings();
        Check(concurrentPreferences.AutomaticSyncEnabled && concurrentPreferences.NotificationsEnabled
            && concurrentPreferences.DisclaimerSeen && !concurrentPreferences.ChaoxingAutomaticSyncEnabled,
            "overlapping preference writes preserve both changes and unrelated settings atomically");
        var previousPreferences = concurrentPreferences;
        try
        {
            preferenceStore.UpdateSettings(_ => throw new IOException("synthetic_preference_failure"));
            throw new InvalidDataException("The synthetic update failure was ignored.");
        }
        catch (IOException) { }
        Check(preferenceStore.LoadSettings() == previousPreferences, "a failed preference update rolls back its transaction");
        Check(data.IsCurrent(platforms) && data.IsCurrent(ptaDetail) && platforms.Revision == ptaDetail.Revision,
            "unchanged platform list and detail snapshots share the current store revision");

        store.SaveMetadataSettings("pta", store.LoadMetadataSettings("pta") with { LastSyncOutcome = "completed" });
        Check(!data.IsCurrent(platforms) && !data.IsCurrent(ptaDetail),
            "TaskStore.Changed rejects previously loaded platform list and detail snapshots");
        platforms = await data.LoadPlatformsAsync().ConfigureAwait(false);
        Check(platforms.Platforms.Single(platform => platform.PlatformId == "pta").SyncStatus.Outcome == "completed",
            "reloading platform summaries exposes a changed synchronization result");

        var clockReads = clock.Reads;
        clock.OnNextRead(() =>
        {
            // Runs after the worker has read every field, immediately before publication.
            // A revision retry must pick up the task count and session change together.
            Create("pta", "合成读取途中任务", "共同合成课程");
            store.ClearPlatformSession("pta");
        });
        platforms = await data.LoadPlatformsAsync().ConfigureAwait(false);
        pta = platforms.Platforms.Single(platform => platform.PlatformId == "pta");
        Check(clock.Reads >= clockReads + 2 && pta.VisibleTaskCount == 3
            && pta.SessionDescription.Contains("尚未保存登录", StringComparison.Ordinal) && pta.ConnectionReport is null
            && data.IsCurrent(platforms), "a mutation before publication discards and reloads a mixed platform-list result");
        clockReads = clock.Reads;
        clock.OnNextRead(() => Create("mooc", "合成读取途中课程任务", "新增合成课程"));
        moocDetail = await data.LoadPlatformAsync("mooc").ConfigureAwait(false);
        Check(clock.Reads >= clockReads + 2 && moocDetail.Recording.Courses.Count == 2 && data.IsCurrent(moocDetail),
            "platform detail retries when its course overview changes before publication");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            clockReads = clock.Reads;
            await Cancelled(data.LoadPlatformsAsync(cancellation.Token), "already cancelled platform-list loads do not start work");
            await Cancelled(data.LoadPlatformAsync("pta", cancellation.Token), "already cancelled platform-detail loads do not start work");
            await Cancelled(data.LoadSettingsAsync(cancellation.Token), "already cancelled settings loads do not start work");
            Check(clock.Reads == clockReads && observed.Reads == 0, "pre-cancellation preserves the no-decryption page-read contract");
        }
        using (var barrier = clock.BlockNextRead())
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = data.LoadPlatformsAsync(cancellation.Token);
            try
            {
                await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                cancellation.Cancel(); barrier.Release();
                await Cancelled(pending, "a cancelled page load cannot publish a completed worker result");
            }
            finally { barrier.Release(); }
        }
        Check(data.IsCurrent(await data.LoadPlatformsAsync().ConfigureAwait(false)),
            "a cancelled page load does not poison subsequent platform reads");
        try { await data.LoadPlatformAsync("not-a-platform").ConfigureAwait(false); throw new InvalidDataException("Invalid platform was accepted."); }
        catch (TaskValidationException error) { Check(error.Code == "COURSE001", "invalid platform detail requests preserve validation feedback"); }

        var failureStore = new TaskStore(Path.Combine(directory, "page-failure-synthetic.db"), observed);
        failureStore.SaveMetadataSettings("pta", new());
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = failureStore.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE settings SET value=$value WHERE key='platform:pta';";
            command.Parameters.AddWithValue("$value", "{synthetic-malformed-json");
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Synthetic metadata fixture was not created.");
        }
        using (var failureData = new MobileUiData(failureStore, new(failureStore, observed), ["pta"]))
        {
            try { await failureData.LoadPlatformsAsync().ConfigureAwait(false); throw new InvalidDataException("Malformed settings were ignored."); }
            catch (TaskValidationException error) { Check(error.Code == "DB002", "corrupt platform settings surface an error instead of an empty platform list"); }
            failureStore.SaveMetadataSettings("pta", new());
            Check((await failureData.LoadPlatformsAsync().ConfigureAwait(false)).Platforms.Count == 1,
                "platform-list reads can retry after a local data-read failure");
        }

        using (var barrier = clock.BlockNextRead())
        {
            var pending = data.LoadPlatformAsync("mooc");
            try
            {
                await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                data.Dispose(); barrier.Release();
                await Disposed(pending, "a platform worker cannot publish a detail snapshot after its service is disposed");
            }
            finally { barrier.Release(); }
        }
        Check(!data.IsCurrent(platforms) && !data.IsCurrent(moocDetail), "disposing page data invalidates loaded list and detail revisions");
        await Disposed(data.LoadPlatformsAsync(), "disposed services reject new platform-list work");
        await Disposed(data.LoadPlatformAsync("pta"), "disposed services reject new platform-detail work");
        await Disposed(data.LoadSettingsAsync(), "disposed services reject new settings work");
        Check(observed.Reads == 0, "all page reads, retries and cancellation paths leave encrypted data untouched");
        return new("passed", results.Count, results);
    }

    private sealed class MarkedUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => throw new InvalidOperationException("Platform data reads captured the UI context.");
    }

    private sealed class ObservedProtector(ILocalLinkProtector inner) : ILocalLinkProtector
    {
        private int reads;
        public int Reads => Volatile.Read(ref reads);
        public byte[] Protect(string value) => inner.Protect(value);
        public string Unprotect(byte[] value) { Interlocked.Increment(ref reads); return inner.Unprotect(value); }
        public void Reset() => Interlocked.Exchange(ref reads, 0);
    }

    private sealed class PageReadClock(DateTimeOffset value)
    {
        private int reads;
        private int readOnUiContext;
        private Action? nextAction;
        private ReadBarrier? nextBarrier;
        public int Reads => Volatile.Read(ref reads);
        public bool ReadOnUiContext => Volatile.Read(ref readOnUiContext) != 0;
        public DateTimeOffset Read()
        {
            Interlocked.Increment(ref reads);
            if (SynchronizationContext.Current is MarkedUiContext) Interlocked.Exchange(ref readOnUiContext, 1);
            Interlocked.Exchange(ref nextAction, null)?.Invoke();
            if (Interlocked.Exchange(ref nextBarrier, null) is { } barrier)
            {
                barrier.Started.TrySetResult(true);
                if (!barrier.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic page read timed out.");
            }
            return value;
        }
        public void OnNextRead(Action action)
        {
            if (Interlocked.CompareExchange(ref nextAction, action, null) is not null)
                throw new InvalidOperationException("A synthetic page mutation is already pending.");
        }
        public ReadBarrier BlockNextRead()
        {
            var barrier = new ReadBarrier();
            if (Interlocked.CompareExchange(ref nextBarrier, barrier, null) is not null)
                throw new InvalidOperationException("A synthetic page read is already blocked.");
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
