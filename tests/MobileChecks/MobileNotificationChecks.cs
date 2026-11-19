using AccessProbe.Mobile;

public sealed record MobileNotificationCheckResult(string Outcome, int Checks, IReadOnlyList<string> Results);

public static class MobileNotificationChecks
{
    public static MobileNotificationCheckResult Run()
    {
        var results = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidDataException(label); results.Add(label); }
        var ready = MobileNotificationStatus.FromSignals(true, true, true, true);
        Check(ready.CanRequest, "enabled runtime permission, app and channel permit a system request");
        var blocked = new[]
        {
            MobileNotificationStatus.FromSignals(false, true, true, true),
            MobileNotificationStatus.FromSignals(true, false, true, true),
            MobileNotificationStatus.FromSignals(true, true, true, false),
            MobileNotificationStatus.FromSignals(true, true, false, true),
            MobileNotificationStatus.FromSignals(true, true, true, true, false)
        };
        Check(blocked.Select(state => state.Kind).SequenceEqual(new[]
        {
            MobileNotificationStatusKind.PermissionDenied, MobileNotificationStatusKind.AppDisabled,
            MobileNotificationStatusKind.ChannelDisabled, MobileNotificationStatusKind.ChannelMissing, MobileNotificationStatusKind.Unavailable
        }), "denied permission, disabled app/channel, missing channel and missing system service have distinct explanations");
        var requests = 0;
        foreach (var state in blocked)
        {
            var result = MobileNotificationRequests.Attempt(state, () => requests++, true);
            Check(result.Outcome == MobileNotificationRequestOutcome.Blocked && result.Message == state.Message,
                "blocked " + state.Kind + " does not claim a successful test request");
        }
        Check(requests == 0, "blocked notifications never invoke the system request callback");
        var sent = MobileNotificationRequests.Attempt(ready, () => requests++, true);
        Check(requests == 1 && sent.Requested && sent.Message.Contains("请求", StringComparison.Ordinal)
            && sent.Message.Contains("查看通知栏确认", StringComparison.Ordinal),
            "test feedback acknowledges the system request and asks the user to verify visibility");
        var failed = MobileNotificationRequests.Attempt(ready, () => throw new InvalidOperationException("synthetic_only"), true);
        Check(failed.Outcome == MobileNotificationRequestOutcome.Failed && !failed.Requested,
            "a failed system request is not reported as delivered");

        const string first = "11111111111111111111111111111111";
        const string second = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var routing = new MobileNotificationRouting();
        Check(routing.Receive(null).Outcome == MobileNotificationRouteOutcome.None && !routing.HasPending,
            "an ordinary app launch does not invent a notification task");
        Check(routing.Receive("invalid").Outcome == MobileNotificationRouteOutcome.Invalid && !routing.HasPending,
            "invalid task identifiers cannot navigate");
        routing.Receive(first);
        var existenceChecks = 0;
        bool Exists(string id) { existenceChecks++; return id == first || id == second; }
        var duringLogin = routing.Resolve(true, false, false, Exists);
        Check(duringLogin.Outcome == MobileNotificationRouteOutcome.Deferred && routing.PendingTaskId == first
            && existenceChecks == 0 && duringLogin.Message.Contains("登录", StringComparison.Ordinal),
            "a notification during login retains its task without querying or replacing the login page");
        var duringEdit = routing.Resolve(false, true, false, Exists);
        Check(duringEdit.Outcome == MobileNotificationRouteOutcome.Deferred && routing.HasPending && existenceChecks == 0
            && duringEdit.Message.Contains("编辑", StringComparison.Ordinal),
            "unsaved editing is protected until save or cancel");
        Check(routing.Resolve(false, false, true, Exists).Outcome == MobileNotificationRouteOutcome.Deferred
            && routing.HasPending && existenceChecks == 0, "an ongoing operation retains the notification target");
        routing.Receive(second.ToUpperInvariant());
        Check(routing.PendingTaskId == second, "the most recently clicked task replaces an older pending notification and normalizes hex casing");
        routing.Receive("not-a-task");
        routing.Receive(null);
        Check(routing.PendingTaskId == second, "invalid and ordinary intents cannot discard a valid pending notification");
        var resolved = routing.Resolve(false, false, false, Exists);
        Check(resolved.Outcome == MobileNotificationRouteOutcome.Open && resolved.TaskId == second && !routing.HasPending,
            "after login/edit/operation finishes, the latest clicked task opens once");
        Check(routing.Resolve(false, false, false, Exists).Outcome == MobileNotificationRouteOutcome.None,
            "a consumed notification cannot reopen itself on the next resume");
        routing.Receive(first);
        var missing = routing.Resolve(false, false, false, _ => false);
        Check(missing.Outcome == MobileNotificationRouteOutcome.Missing && !routing.HasPending
            && missing.Message.Contains("已不存在", StringComparison.Ordinal),
            "deleted notification targets produce an explanation and do not force navigation");
        routing.Receive(first);
        var unreadable = routing.Resolve(false, false, false, _ => throw new IOException("synthetic_read_failure"));
        Check(unreadable.Outcome == MobileNotificationRouteOutcome.Unavailable && routing.PendingTaskId == first,
            "a transient task read failure retains the target for a later retry");
        Check(routing.Resolve(false, false, false, Exists).Outcome == MobileNotificationRouteOutcome.Open && !routing.HasPending,
            "the retained target opens after a transient read failure recovers");
        return new("passed", results.Count, results);
    }
}
