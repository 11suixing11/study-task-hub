using AccessProbe;

internal static class PtaLoginRecoveryChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "pta-login-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MetadataLoginDiagnostics Create(string label, MetadataPlatform? platform = null)
        {
            var diagnostics = new MetadataLoginDiagnostics(platform ?? MetadataPlatform.Pta, Path.Combine(root, label + ".json"));
            diagnostics.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
            return diagnostics;
        }
        void HomeStarted(MetadataLoginDiagnostics diagnostics, ulong id) => diagnostics.Record(MetadataLoginStage.MainNavigation,
            MetadataLoginResult.Started, MetadataPlatform.Pta.Home.AbsoluteUri, navigationId: id);
        void HomeCompleted(MetadataLoginDiagnostics diagnostics, ulong id) => diagnostics.Record(MetadataLoginStage.MainNavigation,
            MetadataLoginResult.Ready, MetadataPlatform.Pta.Home.AbsoluteUri, navigationId: id, documentCompleted: true);
        void Verified(MetadataLoginDiagnostics diagnostics, MetadataLoginResult result = MetadataLoginResult.MetadataReady)
            => diagnostics.Record(MetadataLoginStage.SessionCheck, result);
        void Blocked(MetadataLoginDiagnostics diagnostics, MetadataLoginStage stage = MetadataLoginStage.MainNavigation)
            => diagnostics.Record(stage, MetadataLoginResult.Blocked, "https://unconfirmed.example/private?code=synthetic-private-value");

        foreach (var stage in new[] { MetadataLoginStage.MainNavigation, MetadataLoginStage.FrameNavigation })
        {
            var diagnostics = Create(stage.ToString());
            Blocked(diagnostics, stage); Verified(diagnostics);
            check(diagnostics.Snapshot().PageFailure?.Stage == stage, "account confirmation alone preserves blocked login document");
            HomeStarted(diagnostics, 1);
            diagnostics.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, MetadataPlatform.Pta.Home.AbsoluteUri, navigationId: 1);
            Verified(diagnostics);
            check(diagnostics.Snapshot().PageFailure is not null && !diagnostics.PtaHomeConfirmed,
                "DOMContentLoaded cannot prove successful PTA return");
            HomeCompleted(diagnostics, 1);
            check(diagnostics.Snapshot().PageFailure is null && diagnostics.PtaHomeConfirmed,
                "fresh account verification and completed new student-home navigation recover old block");
            check(diagnostics.Snapshot().BlockedNavigations == 1 && diagnostics.Snapshot().Events.Any(item => item.Result == MetadataLoginResult.Blocked),
                "recovery preserves historical blocked diagnostic event");
        }

        var oldCompletion = Create("old-completion");
        HomeStarted(oldCompletion, 1); Blocked(oldCompletion); HomeCompleted(oldCompletion, 1); Verified(oldCompletion);
        check(oldCompletion.Snapshot().PageFailure is not null, "old in-flight home completion cannot erase a newer block");
        HomeStarted(oldCompletion, 2); HomeCompleted(oldCompletion, 1); Verified(oldCompletion);
        check(oldCompletion.Snapshot().PageFailure is not null, "obsolete navigation ID cannot recover current page");
        HomeCompleted(oldCompletion, 2);
        check(oldCompletion.Snapshot().PageFailure is null, "latest successful home completion recovers the matching flow");

        var newBlock = Create("new-frame-block");
        Blocked(newBlock); HomeStarted(newBlock, 1); HomeCompleted(newBlock, 1); Blocked(newBlock, MetadataLoginStage.FrameNavigation); Verified(newBlock);
        check(newBlock.Snapshot().PageFailure?.Stage == MetadataLoginStage.FrameNavigation, "new frame error survives old successful home evidence");

        foreach (var rejection in new[] { MetadataLoginResult.LoginRequired, MetadataLoginResult.AccountUnknown, MetadataLoginResult.AwaitingLogin })
        {
            var diagnostics = Create("rejection-" + rejection);
            Blocked(diagnostics); HomeStarted(diagnostics, 1); HomeCompleted(diagnostics, 1); Verified(diagnostics, rejection);
            check(diagnostics.Snapshot().PageFailure is not null && !diagnostics.PtaHomeConfirmed && diagnostics.AccountValidationRevision == 0,
                "unverified session cannot clear PTA page error");
        }
        var accountOnly = Create("account-only");
        Blocked(accountOnly); HomeStarted(accountOnly, 1); HomeCompleted(accountOnly, 1); Verified(accountOnly, MetadataLoginResult.AccountReady);
        check(accountOnly.Snapshot().PageFailure is null && accountOnly.PtaHomeConfirmed && !accountOnly.SessionConfirmed,
            "verified account can recover login return while catalog remains partial");

        foreach (var change in new[] { MetadataLoginResult.SessionChanged, MetadataLoginResult.AccountMismatch })
        {
            var diagnostics = Create("change-" + change);
            HomeStarted(diagnostics, 1); HomeCompleted(diagnostics, 1); Verified(diagnostics);
            diagnostics.Record(MetadataLoginStage.SessionValidation, change);
            Blocked(diagnostics); HomeCompleted(diagnostics, 1);
            check(diagnostics.AccountValidationRevision == 0 && !diagnostics.PtaHomeConfirmed && diagnostics.Snapshot().PageFailure is not null,
                "account change invalidates previous recovery permission");
            Verified(diagnostics); HomeCompleted(diagnostics, 1);
            check(!diagnostics.PtaHomeConfirmed && diagnostics.Snapshot().PageFailure is not null,
                "fresh account check cannot revive a navigation from the previous session");
            HomeStarted(diagnostics, 2); HomeCompleted(diagnostics, 2); Verified(diagnostics);
            check(diagnostics.Snapshot().PageFailure is null, "newly verified session recovers only after a new student return");
        }

        var popup = Create("popup");
        HomeStarted(popup, 1); HomeCompleted(popup, 1); Verified(popup); Blocked(popup, MetadataLoginStage.Popup);
        check(popup.Snapshot().PageFailure is not null, "new blocked popup requires a later verification");
        Verified(popup);
        check(popup.Snapshot().PageFailure is null && popup.Snapshot().BlockedNavigations == 1, "fresh official-home verification recovers blocked post-login popup");
        popup.Record(MetadataLoginStage.Popup, MetadataLoginResult.PopupSuppressed);
        check(popup.Snapshot().PageFailure is null && popup.Snapshot().BlockedNavigations == 2, "suppressed post-login popup stays a historical event");
        popup.RecordSessionChange("profile_changed"); Blocked(popup, MetadataLoginStage.Popup);
        HomeCompleted(popup, 1); Verified(popup);
        check(!popup.PtaHomeConfirmed && popup.Snapshot().PageFailure is not null,
            "post-login popup cannot recover using a document from an invalidated session");

        foreach (var (stage, result) in new[]
        {
            (MetadataLoginStage.Certificate, MetadataLoginResult.NetworkFailed),
            (MetadataLoginStage.Renderer, MetadataLoginResult.Failed),
            (MetadataLoginStage.Preparation, MetadataLoginResult.Failed),
            (MetadataLoginStage.Resource, MetadataLoginResult.ScriptUnavailable),
            (MetadataLoginStage.MainNavigation, MetadataLoginResult.NetworkFailed)
        })
        {
            var diagnostics = Create("failure-" + stage);
            diagnostics.Record(stage, result); HomeStarted(diagnostics, 1); HomeCompleted(diagnostics, 1); Verified(diagnostics);
            check(diagnostics.Snapshot().PageFailure is { } failure && failure.Stage == stage && failure.Result == result,
                "successful account check cannot erase real page failure");
        }

        var retry = Create("retry");
        HomeStarted(retry, 1); HomeCompleted(retry, 1); Verified(retry);
        var oldValidation = retry.AccountValidationRevision;
        retry.Record(MetadataLoginStage.Preparation, MetadataLoginResult.Started);
        check(retry.AccountValidationRevision == 0 && !retry.PtaHomeConfirmed, "new login attempt clears previous navigation and account evidence");
        Blocked(retry); HomeCompleted(retry, 1); Verified(retry);
        check(retry.Snapshot().PageFailure is not null && retry.AccountValidationRevision > oldValidation,
            "old navigation cannot recover a new login attempt");

        var ucampus = Create("ucampus", MetadataPlatform.Ucampus);
        ucampus.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Blocked);
        ucampus.Record(MetadataLoginStage.MainNavigation, MetadataLoginResult.Ready, "https://u.unipus.cn/user/student/index", navigationId: 1, documentCompleted: true);
        Verified(ucampus);
        check(ucampus.Snapshot().PageFailure is not null && !ucampus.PtaHomeConfirmed, "PTA return recovery does not broaden Ucampus main-document recovery");
        check(Directory.EnumerateFiles(root).All(path => !File.ReadAllText(path).Contains("synthetic-private-value", StringComparison.Ordinal)
            && !File.ReadAllText(path).Contains("unconfirmed.example", StringComparison.Ordinal)), "recovery diagnostics contain categories instead of private URLs");
    }
}
