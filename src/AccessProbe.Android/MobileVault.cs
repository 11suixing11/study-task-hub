using AccessProbe;
using System.Security.Cryptography;
using System.Text.Json;

namespace AccessProbe.Mobile;

public sealed class MobileVault(TaskStore store, ILocalLinkProtector protector)
{
    private static readonly object Gate = new();
    public MobileSession? Load(string platform) => LoadCore(platform, false);
    public MobileSession? LoadForRevalidation(string platform) => LoadCore(platform, platform is "ucampus" or "zhihuishu");
    private MobileSession? LoadCore(string platform, bool includeExpired)
    {
        lock (Gate)
        {
            if (store.LoadPlatformSessionInfo(platform).State == "expired" && !includeExpired) return null;
            var protectedData = store.ReadProtectedPlatformSession(platform).Data;
            if (protectedData is null) return null;
            try
            {
                var value = JsonSerializer.Deserialize<MobileSession>(protector.Unprotect(protectedData));
                return value is not null && MobileSessionRules.IsValid(value, platform, DateTimeOffset.UtcNow) ? value : null;
            }
            catch (Exception error) when (error is CryptographicException or JsonException) { return null; }
        }
    }
    public void Save(MobileSession session)
    {
        lock (Gate)
        {
            if (!MobileSessionRules.IsValid(session, session.PlatformId, DateTimeOffset.UtcNow))
                throw new InvalidDataException("Invalid mobile session.");
            store.WriteProtectedPlatformSession(session.PlatformId, protector.Protect(JsonSerializer.Serialize(session)), DateTimeOffset.UtcNow);
        }
    }
    public MobileSession Validate(MobileSession expected, CancellationToken cancellation)
    {
        lock (Gate)
        {
            cancellation.ThrowIfCancellationRequested();
            var current = LoadForRevalidation(expected.PlatformId);
            if (current is null || current.Id != expected.Id || MobileSessionRules.Fingerprint(current) != MobileSessionRules.Fingerprint(expected))
                throw new OperationCanceledException("The official login changed during synchronization.", cancellation);
            return current;
        }
    }
    public MobileSession Renew(MobileSession expected, IReadOnlyList<MetadataSessionCookie> observed,
        IReadOnlyList<MetadataSessionCookie> changes, CancellationToken cancellation)
    {
        lock (Gate)
        {
            var current = Validate(expected, cancellation);
            var renewed = MobileSessionRules.Renew(current, observed, changes, DateTimeOffset.UtcNow);
            if (changes.Count > 0) Save(renewed);
            return renewed;
        }
    }
    public MobileSession BindAccount(MobileSession expected, string account, CancellationToken cancellation)
    {
        lock (Gate)
        {
            var current = Validate(expected, cancellation);
            if (!MetadataTaskNavigation.IsId(account) || current.AccountId is not null && current.AccountId != account)
                throw new OperationCanceledException("The official account changed during synchronization.", cancellation);
            var updated = current with { AccountId = account };
            if (current.AccountId is null) Save(updated);
            return updated;
        }
    }
    public void Finish(MobileSession expected, bool loginRequired, bool accountVerified, CancellationToken cancellation)
    {
        lock (Gate)
        {
            Validate(expected, cancellation);
            if (loginRequired) store.ExpirePlatformSession(expected.PlatformId, DateTimeOffset.UtcNow, preserveForRevalidation: expected.PlatformId is "ucampus" or "zhihuishu");
            else if (accountVerified) store.ConfirmPlatformSession(expected.PlatformId, DateTimeOffset.UtcNow);
        }
    }
}
