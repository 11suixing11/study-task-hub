using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccessProbe;

public sealed record ChaoxingSessionCookie(string Name, string Value, string Domain, string Path,
    bool Secure, bool HttpOnly, DateTimeOffset? ExpiresUtc, string? SameSite = null)
{
    public string Key => Domain.ToLowerInvariant() + "\n" + Path + "\n" + Name;
    public bool IsExpired(DateTimeOffset now) => ExpiresUtc is { } expiry && expiry <= now;
    public Cookie ToCookie() => new(Name, Value, Path, Domain)
    {
        Secure = Secure, HttpOnly = HttpOnly,
        Expires = ExpiresUtc?.UtcDateTime ?? DateTime.MinValue
    };

    public static string KeyOf(Cookie cookie) => cookie.Domain.ToLowerInvariant() + "\n" + cookie.Path + "\n" + cookie.Name;

    public static ChaoxingSessionCookie FromCookie(Cookie cookie) => new(cookie.Name, cookie.Value, cookie.Domain,
        cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.Expires == DateTime.MinValue ? null
            : new DateTimeOffset(cookie.Expires.ToUniversalTime()));

    public bool AppliesTo(Uri address)
    {
        var host = Domain.TrimStart('.');
        return (address.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase)
                || Domain.StartsWith('.') && address.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
            && (!Secure || address.Scheme == Uri.UriSchemeHttps)
            && (address.AbsolutePath == Path || address.AbsolutePath.StartsWith(Path, StringComparison.Ordinal)
                && (Path.EndsWith('/') || address.AbsolutePath.Length > Path.Length && address.AbsolutePath[Path.Length] == '/'));
    }

    public override string ToString() => nameof(ChaoxingSessionCookie);
}

// Only the project's own official cookies are retained; DPAPI binds the archive to this Windows user.
// Restoring preserves the original expiry/session flag. The official server still validates every sync.
public sealed class ChaoxingSessionStore
{
    public const int MaximumCookies = 512;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("LearningTaskAggregator.chaoxing-session.v1"));
    private readonly string path;
    public string LastLoadOutcome { get; private set; } = "not_loaded";
    private sealed record Snapshot(int Schema, DateTimeOffset SavedAtUtc, ChaoxingSessionCookie[] Cookies);

    public ChaoxingSessionStore(string path) => this.path = System.IO.Path.GetFullPath(path);

    public IReadOnlyList<ChaoxingSessionCookie> Load(DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) { LastLoadOutcome = "unavailable"; return []; }
        if (!File.Exists(path)) { LastLoadOutcome = "not_saved"; return []; }
        byte[]? plain = null;
        try
        {
            if (new FileInfo(path).Length > MaximumBytes) { LastLoadOutcome = "invalid"; return []; }
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            if (plain.Length > MaximumBytes) { LastLoadOutcome = "invalid"; return []; }
            var snapshot = JsonSerializer.Deserialize<Snapshot>(plain);
            if (snapshot is null || snapshot.Schema != 1 || snapshot.Cookies is null
                || snapshot.Cookies.Length > MaximumCookies || snapshot.SavedAtUtc > now.AddMinutes(5))
            { LastLoadOutcome = "invalid"; return []; }
            if (snapshot.SavedAtUtc < now - MaximumAge) { LastLoadOutcome = "too_old"; return []; }
            if (snapshot.Cookies.Any(cookie => !IsValid(cookie))) { LastLoadOutcome = "invalid"; return []; }
            var cookies = snapshot.Cookies.Where(cookie => !cookie.IsExpired(now)).DistinctBy(cookie => cookie.Key).ToArray();
            LastLoadOutcome = cookies.Length == 0 ? "expired_or_empty" : "available";
            return cookies;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        { LastLoadOutcome = "unavailable"; return []; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(IEnumerable<ChaoxingSessionCookie> observed, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows local protection is required.");
        var cookies = observed.Where(cookie => IsValid(cookie) && !cookie.IsExpired(now))
            .DistinctBy(cookie => cookie.Key).Take(MaximumCookies + 1).ToArray();
        if (cookies.Length > MaximumCookies) throw new InvalidDataException("Session cookie limit exceeded.");
        if (cookies.Length == 0) { Clear(); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1, now, cookies));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (plain.Length > MaximumBytes) throw new InvalidDataException("Session archive limit exceeded.");
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, protectedBytes);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear() { if (File.Exists(path)) File.Delete(path); }

    public static bool IsValid(ChaoxingSessionCookie? cookie)
    {
        if (cookie is null || string.IsNullOrEmpty(cookie.Name) || cookie.Name.Length > 256
            || cookie.Value is null || cookie.Value.Length > 16384 || string.IsNullOrEmpty(cookie.Domain)
            || cookie.Domain.Length > 255 || !ProbePolicy.IsOfficialHost(cookie.Domain.TrimStart('.'))
            || cookie.Domain.StartsWith("..", StringComparison.Ordinal) || cookie.Domain.EndsWith('.')
            || string.IsNullOrEmpty(cookie.Path) || cookie.Path.Length > 2048 || !cookie.Path.StartsWith('/')
            || cookie.Domain.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
            || cookie.Name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is ';' or ',' or '=')
            || cookie.Value.Any(char.IsControl) || cookie.Path.Any(char.IsControl)
            || cookie.Name.StartsWith("__Secure-", StringComparison.Ordinal) && !cookie.Secure
            || cookie.Name.StartsWith("__Host-", StringComparison.Ordinal)
                && (!cookie.Secure || cookie.Domain.StartsWith('.') || cookie.Path != "/")
            || cookie.SameSite is not (null or "None" or "Lax" or "Strict")) return false;
        try { _ = cookie.ToCookie(); return true; }
        catch (CookieException) { return false; }
    }

    public static IReadOnlyList<ChaoxingSessionCookie> MissingForRestore(
        IReadOnlyList<ChaoxingSessionCookie> saved, IReadOnlyList<ChaoxingSessionCookie> current)
    {
        var existing = current.DistinctBy(cookie => cookie.Key).ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
        // A newer login/profile takes precedence. Do not combine different cookie generations or accounts.
        if (saved.Any(cookie => existing.TryGetValue(cookie.Key, out var value) && value.Value != cookie.Value)) return [];
        return saved.Where(cookie => !existing.ContainsKey(cookie.Key)).ToArray();
    }
}

public static class ChaoxingResponseCookies
{
    private static readonly Regex Boundary = new(@",(?=\s*[^=;,\s]+=)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<ChaoxingSessionCookie> Parse(Uri address, IEnumerable<string> headers, DateTimeOffset now)
    {
        if (address.Scheme != Uri.UriSchemeHttps || !ProbePolicy.IsAllowedNavigation(address.AbsoluteUri)) return [];
        var result = new Dictionary<string, ChaoxingSessionCookie>(StringComparer.Ordinal);
        foreach (var header in headers.Take(128))
        {
            if (header.Length > 32768 || header.Any(character => character is '\r' or '\n')) continue;
            foreach (var item in Boundary.Split(header))
            {
                var parts = item.Split(';', StringSplitOptions.TrimEntries);
                var pair = parts[0].Split('=', 2);
                if (pair.Length != 2) continue;
                var name = pair[0].Trim(); var value = pair[1].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                var domain = address.IdnHost;
                var slash = address.AbsolutePath.LastIndexOf('/');
                var path = slash > 0 ? address.AbsolutePath[..slash] : "/";
                bool secure = false, httpOnly = false, invalid = false;
                DateTimeOffset? expires = null; long? maxAge = null; string? sameSite = null;
                foreach (var part in parts.Skip(1))
                {
                    var attribute = part.Split('=', 2);
                    var key = attribute[0].ToLowerInvariant(); var content = attribute.Length == 2 ? attribute[1] : "";
                    switch (key)
                    {
                        case "domain": domain = "." + content.TrimStart('.').ToLowerInvariant(); break;
                        case "path": if (content.StartsWith('/')) path = content; break;
                        case "secure": secure = true; break;
                        case "httponly": httpOnly = true; break;
                        case "max-age":
                            if (long.TryParse(content, NumberStyles.Integer, CultureInfo.InvariantCulture, out var age)) maxAge = age;
                            else invalid = true;
                            break;
                        case "expires":
                            if (DateTimeOffset.TryParse(content, CultureInfo.InvariantCulture,
                                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var expiry)) expires = expiry.ToUniversalTime();
                            else invalid = true;
                            break;
                        case "samesite":
                            sameSite = content.ToLowerInvariant() switch { "none" => "None", "lax" => "Lax", "strict" => "Strict", _ => null };
                            break;
                    }
                }
                if (invalid) continue;
                try
                {
                    if (maxAge is { } age) expires = age <= 0 ? now.AddSeconds(-1) : now.AddSeconds(age);
                    var cookie = new ChaoxingSessionCookie(name, value, domain, path, secure, httpOnly, expires, sameSite);
                    if (!ChaoxingSessionStore.IsValid(cookie)) continue;
                    // CookieContainer validates that a response cannot set another host's cookie.
                    var scope = new CookieContainer(); scope.Add(address, cookie.ToCookie());
                    result[cookie.Key] = cookie;
                }
                catch (Exception error) when (error is CookieException or ArgumentOutOfRangeException) { }
            }
        }
        return result.Values.ToArray();
    }
}
