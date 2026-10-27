using System.Net;
using System.Net.Http;
using System.Text.Json;
using AccessProbe;

internal static class TaskNavigationChecks
{
    private const string Entry = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=201&cpi=301&workId=401&answerId=501&enc=SYNTHETIC%2BONLY";

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Task navigation check failed: " + (checks + 1)); checks++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); throw new InvalidOperationException("Expected rejection."); }
            catch (TaskValidationException error) { Check(error.Code == code); }
        }
        string Target(string route) => Entry.Replace("/work/task?", "/work/" + route + "?", StringComparison.Ordinal)
            .Replace("enc=SYNTHETIC%2BONLY", "enc=REFRESHED%2BVALUE%26ONLY", StringComparison.Ordinal);
        var destinations = new[] { (Route: "dowork", Kind: "answer"), (Route: "view", Kind: "view"), (Route: "prompt", Kind: "submitted") };
        foreach (var (route, kind) in destinations)
        {
            var target = Target(route) + "&standardEnc=OPAQUE%2BVALUE";
            var content = new UnreadableContent();
            var handler = new CaptureHandler(_ => new(HttpStatusCode.Found) { Headers = { Location = new(target) }, Content = content });
            var cookieReads = 0;
            using var reader = new ChaoxingHttpReader((address, token) =>
            {
                token.ThrowIfCancellationRequested(); Check(address.OriginalString == Entry); cookieReads++;
                return Task.FromResult<IReadOnlyList<Cookie>>([]);
            }, handler);
            var result = await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None);
            Check(result.Outcome == "ready" && result.Destination == kind && result.Address == target);
            Check(handler.Requests.Count == 1 && handler.Requests.Single().Method == HttpMethod.Get && cookieReads == 1);
            Check(handler.Requests.All(request => request.Address.AbsolutePath.EndsWith("/task", StringComparison.Ordinal)) && content.Reads == 0);
            Check(!JsonSerializer.Serialize(result).Contains("REFRESHED", StringComparison.Ordinal) && result.ToString() == nameof(TaskLinkResolution));
        }
        var upgradedTarget = Target("dowork");
        var upgradeHandler = new CaptureHandler(_ => new(HttpStatusCode.Found)
            { Headers = { Location = new(upgradedTarget.Replace("https://", "http://", StringComparison.Ordinal)) } });
        using (var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), upgradeHandler))
            Check((await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None)).Address == upgradedTarget && upgradeHandler.Requests.Count == 1);
        var finalContent = new UnreadableContent();
        var plainHandler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = finalContent });
        using (var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), plainHandler))
            Check((await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None)) is { Outcome: "ready", Destination: "entry" }
                && finalContent.Reads == 0 && plainHandler.Requests.Count == 1);
        var nextEntry = Entry.Replace("enc=SYNTHETIC%2BONLY", "enc=NEW%2BVALUE", StringComparison.Ordinal);
        var chain = new CaptureHandler(request => new(HttpStatusCode.Found)
            { Headers = { Location = new(request.RequestUri!.OriginalString == Entry ? nextEntry : Target("dowork")) } });
        using (var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), chain))
            Check((await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None)).Destination == "answer" && chain.Requests.Count == 2);
        var loop = new CaptureHandler(request => new(HttpStatusCode.Found) { Headers = { Location = request.RequestUri } });
        using (var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), loop))
            Check((await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None)).Outcome == "redirect_loop" && loop.Requests.Count == 1);
        foreach (var target in new[]
        {
            Target("dowork").Replace("courseId=101", "courseId=999", StringComparison.Ordinal),
            Target("dowork").Replace("classId=201", "classId=999", StringComparison.Ordinal),
            Target("dowork").Replace("cpi=301", "cpi=999", StringComparison.Ordinal),
            Target("dowork").Replace("workId=401", "workId=999", StringComparison.Ordinal),
            Target("dowork").Replace("answerId=501", "answerId=999", StringComparison.Ordinal),
            Target("dowork").Replace("&answerId=501", "", StringComparison.Ordinal),
            Target("dowork") + "&courseId=101", Target("submit"),
            Target("view").Replace("mooc1.chaoxing.com", "evil.example", StringComparison.Ordinal),
            Target("view").Replace("enc=REFRESHED%2BVALUE%26ONLY", "enc=", StringComparison.Ordinal)
        })
        {
            var handler = new CaptureHandler(_ => new(HttpStatusCode.Found) { Headers = { Location = new(target) } });
            using var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), handler);
            Check((await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None)).Outcome == "unsupported_redirect" && handler.Requests.Count == 1);
        }
        foreach (var (status, outcome) in new[] { (HttpStatusCode.Unauthorized, "login_required"), (HttpStatusCode.Forbidden, "forbidden"),
            (HttpStatusCode.TooManyRequests, "rate_limited"), (HttpStatusCode.InternalServerError, "http_error") })
        {
            using var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), new CaptureHandler(_ => new(status)));
            Check((await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None)).Outcome == outcome);
        }
        using (var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), new CaptureHandler(_ => new(HttpStatusCode.Found)
            { Headers = { Location = new("https://passport2.chaoxing.com/login?service=SYNTHETIC") } })))
            Check((await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None)).Outcome == "login_required");
        using (var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), new CaptureHandler(_ => new(HttpStatusCode.Found))))
            Check((await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None)).Outcome == "unsupported_redirect");
        using (var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), new CaptureHandler(_ => throw new HttpRequestException())))
            Check((await reader.ResolveTaskAddressAsync(new(Entry), CancellationToken.None)).Outcome == "network_error");
        var invalidHandler = new CaptureHandler(_ => throw new InvalidOperationException("Invalid target was fetched."));
        using (var reader = new ChaoxingHttpReader((_, _) => Task.FromResult<IReadOnlyList<Cookie>>([]), invalidHandler))
        {
            Check((await reader.ResolveTaskAddressAsync(new(Target("dowork")), CancellationToken.None)).Outcome == "invalid_link" && invalidHandler.Requests.Count == 0);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await reader.ResolveTaskAddressAsync(new(Entry), cancelled.Token); throw new InvalidOperationException("Expected cancellation."); }
            catch (OperationCanceledException) { Check(invalidHandler.Requests.Count == 0); }
        }

        var root = Path.Combine(Path.GetTempPath(), "aggregate-navigation-" + Guid.NewGuid().ToString("N"));
        var store = new TaskStore(Path.Combine(root, "tasks.db"));
        var now = DateTimeOffset.UtcNow;
        store.ImportWorks([new("合成任务", "未交", "剩余50小时", WorkListParser.ParseTaskLocation(Entry))], "合成课程", now);
        var task = store.LoadTasks().Single();
        var baseline = task;
        var opened = new List<string>(); var resolves = 0;
        var navigation = new TaskNavigationService(store, (address, _) =>
        { resolves++; Check(address.OriginalString == Entry); return Task.FromResult(new TaskLinkResolution("ready", "answer", Target("dowork"))); }, opened.Add);
        Check((await navigation.OpenAsync(task.Id, CancellationToken.None)).Destination == "answer" && opened.Single() == Target("dowork"));
        Check(store.Get(task.Id) == baseline && resolves == 1); // Jumping does not complete, submit or edit a task.
        var fallback = new TaskNavigationService(store, (_, _) => Task.FromResult(new TaskLinkResolution("login_required", "none")), opened.Add);
        Check((await fallback.OpenAsync(task.Id, CancellationToken.None)).Destination == "login_required" && opened.Last() == Entry);
        var wrong = new TaskNavigationService(store, (_, _) => Task.FromResult(new TaskLinkResolution("ready", "answer",
            Target("dowork").Replace("workId=401", "workId=999", StringComparison.Ordinal))), opened.Add);
        var before = opened.Count;
        await Reject(() => wrong.OpenAsync(task.Id, CancellationToken.None), "LINK003");
        Check(opened.Count == before);
        var unrecognized = new TaskNavigationService(store, (_, _) => Task.FromResult(new TaskLinkResolution("unsupported_redirect", "none")), opened.Add);
        await Reject(() => unrecognized.OpenAsync(task.Id, CancellationToken.None), "LINK003");
        Check(opened.Count == before);
        Check(TaskNavigationRules.Destination(new("https://i.chaoxing.com/"), new(Target("view"))) is null);
        var refreshed = Entry.Replace("enc=SYNTHETIC%2BONLY", "enc=LATEST%2BVALUE", StringComparison.Ordinal);
        store.ImportWorks([new("合成任务", "未交", "剩余50小时", WorkListParser.ParseTaskLocation(refreshed))], "合成课程", now);
        var freshNavigation = new TaskNavigationService(store, (address, _) =>
        { Check(address.OriginalString == refreshed); return Task.FromResult(new TaskLinkResolution("ready", "entry", address.OriginalString)); }, opened.Add);
        await freshNavigation.OpenAsync(task.Id, CancellationToken.None);
        Check(opened.Last() == refreshed);
        var race = new TaskNavigationService(store, (address, _) =>
        {
            var current = store.Get(task.Id)!;
            store.Edit(current.Id, current.Revision, Draft(current, "https://example.org/user-changed"), now);
            return Task.FromResult(new TaskLinkResolution("ready", "answer", address.OriginalString.Replace("/work/task?", "/work/dowork?", StringComparison.Ordinal)));
        }, opened.Add);
        await race.OpenAsync(task.Id, CancellationToken.None);
        Check(opened.Last() == "https://example.org/user-changed" && store.Get(task.Id)!.Overrides.HasFlag(TaskOverrides.Link));
        var skip = new TaskNavigationService(store, (_, _) => throw new InvalidOperationException("Manual link must not resolve."), opened.Add);
        await skip.OpenAsync(task.Id, CancellationToken.None);
        Check(opened.Last() == "https://example.org/user-changed");
        var manual = store.Create(new("合成手动任务", "mooc", "合成课程", DeadlineKind.Unknown, null, null,
            "https://example.org/manual-submission", null, "", "", null), now);
        Check((await skip.OpenAsync(manual.Id, CancellationToken.None)).Destination == "manual" && opened.Last() == manual.Link);
        var noLink = store.Create(new("无链接合成任务", "mooc", "合成课程", DeadlineKind.Unknown, null, null, null, null, "", "", null), now);
        await Reject(() => skip.OpenAsync(noLink.Id, CancellationToken.None), "LINK001");
        await Reject(() => skip.OpenAsync("missing-task", CancellationToken.None), "TASK004");
        var deletedTask = store.Create(new("删除竞态合成任务", "mooc", "合成课程", DeadlineKind.Unknown, null, null,
            "https://example.org/deleted", null, "", "", null), now);
        store.Delete(deletedTask.Id, deletedTask.Revision);
        await Reject(() => skip.OpenAsync(deletedTask.Id, CancellationToken.None), "TASK004");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); before = opened.Count;
            try { await skip.OpenAsync(task.Id, cancelled.Token); throw new InvalidOperationException("Expected cancellation."); }
            catch (OperationCanceledException) { Check(opened.Count == before); }
        }
        Check(TaskNavigationRules.ActionName(task).Contains("提交", StringComparison.Ordinal)
            && TaskNavigationRules.ActionName(task with { State = TaskState.Completed }).Contains("查看", StringComparison.Ordinal));
        Console.WriteLine($"PASS: {checks} task-specific external navigation, signed redirects, no answer-page reads, latest links and cancellation checks (synthetic data only).");
    }

    private static TaskDraft Draft(TaskItem task, string link) => new(task.Title, task.PlatformId, task.CourseName,
        task.DeadlineKind, task.BeginUtc, task.DeadlineUtc, link, task.ProgressPercent, task.ScoreText, task.Notes, task.PriorityOverride);

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Address, HttpMethod Method)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add((request.RequestUri!, request.Method));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        public int Reads { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Reads++; throw new InvalidOperationException("Task navigation must not read a page body."); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
