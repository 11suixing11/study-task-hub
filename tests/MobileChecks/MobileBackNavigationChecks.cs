using AccessProbe.Mobile;

public sealed record MobileBackNavigationCheckResult(string Outcome, int Checks, IReadOnlyList<string> Results);

public static class MobileBackNavigationChecks
{
    public static MobileBackNavigationCheckResult Run()
    {
        var results = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidDataException(label); results.Add(label); }
        var navigation = new MobileBackNavigation();
        Check(navigation.Handle(false, true, 0) == MobileBackAction.PromptExit && navigation.ExitPending,
            "first home back prompts even when monotonic uptime is zero");
        Check(navigation.Handle(false, true, 1) == MobileBackAction.Exit && !navigation.ExitPending,
            "second home back exits and consumes the confirmation");
        Check(navigation.Handle(false, true, 10) == MobileBackAction.PromptExit,
            "a consumed exit cannot be reused");
        Check(navigation.Handle(false, true, 2010) == MobileBackAction.Exit,
            "two-second boundary confirms exit");
        navigation.Handle(false, true, 3000);
        Check(navigation.Handle(false, true, 5001) == MobileBackAction.PromptExit,
            "expired confirmation prompts again");
        Check(navigation.Handle(false, true, 5002) == MobileBackAction.Exit,
            "repeated prompt starts a new two-second window");
        navigation.Handle(false, true, 6000);
        Check(navigation.Handle(true, true, 6001) == MobileBackAction.Parent && !navigation.ExitPending,
            "a task-home child returns to its parent and clears an exit request");
        Check(navigation.Handle(false, true, 6002) == MobileBackAction.PromptExit,
            "returning home needs a fresh first back");
        Check(navigation.Handle(true, false, 6003) == MobileBackAction.Parent && !navigation.ExitPending,
            "login, course and platform children prioritize their business parent");
        Check(navigation.Handle(false, false, 6004) == MobileBackAction.TaskHome && !navigation.ExitPending,
            "other root tabs go to task home instead of exiting");
        Check(navigation.Handle(false, true, 6005) == MobileBackAction.PromptExit,
            "a root-tab return is not the first half of double back");
        navigation.Reset();
        Check(!navigation.ExitPending && navigation.Handle(false, true, 6006) == MobileBackAction.PromptExit,
            "navigation or lifecycle reset cannot confirm an old exit");
        Check(navigation.Handle(false, true, 5000) == MobileBackAction.PromptExit,
            "backward monotonic input never exits");
        navigation.Reset();
        Check(navigation.Handle(false, true, -1) == MobileBackAction.PromptExit && !navigation.ExitPending,
            "invalid uptime cannot arm exit");
        Check(navigation.Handle(false, true, long.MaxValue - 1) == MobileBackAction.PromptExit
            && navigation.Handle(false, true, long.MaxValue) == MobileBackAction.Exit,
            "long uptime does not overflow confirmation arithmetic");
        return new("passed", results.Count, results);
    }
}
