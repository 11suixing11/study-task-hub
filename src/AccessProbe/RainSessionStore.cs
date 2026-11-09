using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

public sealed class RainSessionStore(RainPlatform platform, string path, TaskStore repository)
{
    private readonly PlatformSessionArchive archive = new(platform.Id, path, repository);
    private readonly byte[] entropy = SHA256.HashData(Encoding.UTF8.GetBytes("LearningTaskAggregator." + platform.Id + "-session.v1"));
    private sealed record Snapshot(int Schema, DateTimeOffset SavedAtUtc, ChaoxingSessionCookie[] Cookies);
    public string LastLoadOutcome { get; private set; } = "not_loaded";

    public IReadOnlyList<ChaoxingSessionCookie> Load(DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) { LastLoadOutcome = "unavailable"; return []; }
        byte[]? plain = null;
        try
        {
            var bytes = archive.Read();
            if (bytes is null) { LastLoadOutcome = "not_saved"; return []; }
            plain = ProtectedData.Unprotect(bytes, entropy, DataProtectionScope.CurrentUser);
            var value = JsonSerializer.Deserialize<Snapshot>(plain);
            if (plain.Length > 2 * 1024 * 1024 || value is null || value.Schema != 1 || value.Cookies is null
                || value.Cookies.Length > 512 || value.SavedAtUtc > now.AddMinutes(5) || value.Cookies.Any(cookie => !IsValid(cookie)))
            { LastLoadOutcome = "invalid"; return []; }
            var result = value.Cookies.Where(cookie => !cookie.IsExpired(now)).DistinctBy(cookie => cookie.Key).ToArray();
            LastLoadOutcome = result.Length > 0 ? "available" : "expired_or_empty";
            return result;
        }
        catch (InvalidDataException) { LastLoadOutcome = "invalid"; return []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { LastLoadOutcome = "unavailable"; return []; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public bool IsValid(ChaoxingSessionCookie? cookie) => IsValid(platform, cookie);

    internal static bool IsValid(RainPlatform platform, ChaoxingSessionCookie? cookie)
    {
        if (cookie is null || string.IsNullOrEmpty(cookie.Name) || cookie.Value is null || string.IsNullOrEmpty(cookie.Domain)
            || string.IsNullOrEmpty(cookie.Path)) return false;
        var host = cookie.Domain.TrimStart('.').ToLowerInvariant();
        return (host is "yuketang.cn" || platform.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            && !cookie.Domain.StartsWith("..", StringComparison.Ordinal) && !cookie.Domain.EndsWith('.')
            && cookie.Name.Length is > 0 and <= 256 && cookie.Value.Length <= 16384 && cookie.Path.Length is > 0 and <= 2048
            && cookie.Path.StartsWith('/') && !cookie.Name.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is ';' or ',' or '=')
            && !cookie.Value.Any(character => char.IsControl(character) || character == ';') && !cookie.Path.Any(char.IsControl)
            && cookie.SameSite is null or "Lax" or "Strict" or "None"
            && (!cookie.Name.StartsWith("__Secure-", StringComparison.Ordinal) || cookie.Secure)
            && (!cookie.Name.StartsWith("__Host-", StringComparison.Ordinal) || cookie.Secure && !cookie.Domain.StartsWith('.') && cookie.Path == "/");
    }

    public void Save(IEnumerable<ChaoxingSessionCookie> observed, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows local protection is required.");
        var cookies = observed.Where(cookie => IsValid(cookie) && !cookie.IsExpired(now)).DistinctBy(cookie => cookie.Key).Take(513).ToArray();
        if (cookies.Length > 512) throw new InvalidDataException("Session cookie limit exceeded.");
        if (cookies.Length == 0) { Clear(); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1, now, cookies));
        try
        {
            if (plain.Length > 2 * 1024 * 1024) throw new InvalidDataException("Session size limit exceeded.");
            archive.Write(ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser), now);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Clear() => archive.Clear();
    public void Invalidate(DateTimeOffset now) => archive.Invalidate(now);
}
