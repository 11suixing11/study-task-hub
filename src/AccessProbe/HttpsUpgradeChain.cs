namespace AccessProbe;

// Targets remain in memory only, to stop repeated HTTPS -> HTTP -> HTTPS redirects.
public sealed class HttpsUpgradeChain
{
    public const int MaximumUpgrades = 4;
    private readonly HashSet<string> upgradedTargets = new(StringComparer.Ordinal);
    private string? scheduledTarget;

    public void NavigationStarting(string address, bool isRedirected)
    {
        if (!isRedirected && !string.Equals(address, scheduledTarget, StringComparison.Ordinal)) Reset();
        scheduledTarget = null;
    }

    public bool TrySchedule(string httpsAddress)
    {
        if (!httpsAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !ProbePolicy.IsAllowedNavigation(httpsAddress)
            || upgradedTargets.Count >= MaximumUpgrades || !upgradedTargets.Add(httpsAddress)) return false;
        scheduledTarget = httpsAddress;
        return true;
    }

    public void Reset()
    {
        upgradedTargets.Clear();
        scheduledTarget = null;
    }
}
