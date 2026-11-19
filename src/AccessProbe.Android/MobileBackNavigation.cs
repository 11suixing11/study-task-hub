namespace AccessProbe.Mobile;

public enum MobileBackAction { Parent, TaskHome, PromptExit, Exit }

// elapsedRealtime is monotonic; a wall-clock adjustment cannot confirm an exit.
public sealed class MobileBackNavigation
{
    public const long ExitConfirmationMilliseconds = 2000;
    private long? requestedAt;

    public bool ExitPending => requestedAt.HasValue;

    public void Reset() => requestedAt = null;

    public MobileBackAction Handle(bool hasParent, bool isTaskHome, long elapsedRealtime)
    {
        if (hasParent) { Reset(); return MobileBackAction.Parent; }
        if (!isTaskHome) { Reset(); return MobileBackAction.TaskHome; }
        if (elapsedRealtime >= 0 && requestedAt is { } previous && elapsedRealtime >= previous
            && elapsedRealtime - previous <= ExitConfirmationMilliseconds)
        { Reset(); return MobileBackAction.Exit; }
        requestedAt = elapsedRealtime >= 0 ? elapsedRealtime : null;
        return MobileBackAction.PromptExit;
    }
}
