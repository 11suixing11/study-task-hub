using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccessProbe;

public sealed record MoocSessionCookie(string Name, string Value, string Domain, string Path,
    bool Secure, bool HttpOnly, DateTimeOffset? ExpiresUtc, string? SameSite = null)
{
    public string Key => Domain.ToLowerInvariant() + "\n" + Path + "\n" + Name;
    public bool IsExpired(DateTimeOffset now) => ExpiresUtc is { } expiry && expiry <= now;
    public Cookie ToCookie() => new(Name, Value, Path, Domain)
    { Secure = Secure, HttpOnly = HttpOnly, Expires = ExpiresUtc?.UtcDateTime ?? DateTime.MinValue };
    public bool AppliesTo(Uri uri)
    {
        var host = Domain.TrimStart('.');
        return (uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase)
                || Domain.StartsWith('.') && uri.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
            && (!Secure || uri.Scheme == Uri.UriSchemeHttps)
            && (uri.AbsolutePath == Path || uri.AbsolutePath.StartsWith(Path, StringComparison.Ordinal)
                && (Path.EndsWith('/') || uri.AbsolutePath.Length > Path.Length && uri.AbsolutePath[Path.Length] == '/'));
    }
    public override string ToString() => nameof(MoocSessionCookie);
}

public sealed class MoocSessionStore(string path)
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("LearningTaskAggregator.mooc-session.v1"));
    private sealed record Snapshot(int Schema, DateTimeOffset SavedAtUtc, MoocSessionCookie[] Cookies);
    public string LastLoadOutcome { get; private set; } = "not_loaded";

    public IReadOnlyList<MoocSessionCookie> Load(DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) { LastLoadOutcome = "unavailable"; return []; }
        if (!File.Exists(path)) { LastLoadOutcome = "not_saved"; return []; }
        byte[]? plain = null;
        try
        {
            if (new FileInfo(path).Length > MaximumBytes) { LastLoadOutcome = "invalid"; return []; }
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var value = JsonSerializer.Deserialize<Snapshot>(plain);
            if (plain.Length > MaximumBytes || value is null || value.Schema != 1 || value.Cookies is null
                || value.Cookies.Length > 512 || value.SavedAtUtc > now.AddMinutes(5) || value.Cookies.Any(cookie => !IsValid(cookie)))
            { LastLoadOutcome = "invalid"; return []; }
            if (value.SavedAtUtc < now.AddDays(-30)) { LastLoadOutcome = "too_old"; return []; }
            var result = value.Cookies.Where(cookie => !cookie.IsExpired(now)).DistinctBy(cookie => cookie.Key).ToArray();
            LastLoadOutcome = result.Length > 0 ? "available" : "expired_or_empty"; return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { LastLoadOutcome = "unavailable"; return []; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(IEnumerable<MoocSessionCookie> observed, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows local protection is required.");
        var cookies = observed.Where(cookie => IsValid(cookie) && !cookie.IsExpired(now)).DistinctBy(cookie => cookie.Key).Take(513).ToArray();
        if (cookies.Length > 512) throw new InvalidDataException("Session cookie limit exceeded.");
        if (cookies.Length == 0) { Clear(); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1, now, cookies));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (plain.Length > MaximumBytes) throw new InvalidDataException("Session archive limit exceeded.");
            var bytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }

    public static bool IsValid(MoocSessionCookie? cookie)
    {
        if (cookie is null || string.IsNullOrEmpty(cookie.Name) || cookie.Name.Length > 256
            || cookie.Value is null || cookie.Value.Length > 16384 || string.IsNullOrEmpty(cookie.Domain)
            || !MoocPlatform.IsCookieDomain(cookie.Domain.TrimStart('.')) || cookie.Domain.StartsWith("..", StringComparison.Ordinal)
            || cookie.Domain.EndsWith('.') || cookie.Domain.Length > 255 || string.IsNullOrEmpty(cookie.Path)
            || !cookie.Path.StartsWith('/') || cookie.Path.Length > 2048 || cookie.Path.Any(char.IsControl)
            || cookie.Domain.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
            || cookie.Name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is ';' or ',' or '=')
            || cookie.Value.Any(character => char.IsControl(character) || character == ';') || cookie.SameSite is not (null or "None" or "Lax" or "Strict")
            || cookie.Name.StartsWith("__Secure-", StringComparison.Ordinal) && !cookie.Secure
            || cookie.Name.StartsWith("__Host-", StringComparison.Ordinal) && (!cookie.Secure || cookie.Domain.StartsWith('.') || cookie.Path != "/")) return false;
        try { _ = cookie.ToCookie(); return true; } catch (CookieException) { return false; }
    }

    public static IReadOnlyList<MoocSessionCookie> MissingForRestore(IReadOnlyList<MoocSessionCookie> saved, IReadOnlyList<MoocSessionCookie> current)
    {
        var existing = current.DistinctBy(cookie => cookie.Key).ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
        if (saved.Any(cookie => IsAccountEvidence(cookie) && existing.TryGetValue(cookie.Key, out var value) && value.Value != cookie.Value)) return [];
        return saved.Where(cookie => !existing.ContainsKey(cookie.Key)).ToArray();
    }

    // Official page-view analytics can renew independently of the signed-in account.
    public static bool IsAccountEvidence(MoocSessionCookie cookie) => !cookie.Name.StartsWith("Hm_lpvt_", StringComparison.Ordinal)
        && !cookie.Name.StartsWith("Hm_lvt_", StringComparison.Ordinal) && cookie.Name is not ("HMACCOUNT" or "NETEASE_WDA_UID");
}

public static class MoocResponseCookies
{
    private static readonly Regex Boundary = new(@",(?=\s*[^=;,\s]+=)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static IReadOnlyList<MoocSessionCookie> Parse(Uri address, IEnumerable<string> headers, DateTimeOffset now)
    {
        if (!MoocPlatform.IsOfficialNavigation(address.AbsoluteUri)) return [];
        var result = new Dictionary<string, MoocSessionCookie>(StringComparer.Ordinal);
        foreach (var header in headers.Take(128))
        {
            if (header.Length > 32768 || header.Any(character => character is '\r' or '\n')) continue;
            foreach (var item in Boundary.Split(header))
            {
                var parts = item.Split(';', StringSplitOptions.TrimEntries); var pair = parts[0].Split('=', 2);
                if (pair.Length != 2) continue;
                var name = pair[0].Trim(); var value = pair[1].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                var domain = address.IdnHost; var slash = address.AbsolutePath.LastIndexOf('/');
                var path = slash > 0 ? address.AbsolutePath[..slash] : "/";
                bool secure = false, httpOnly = false, invalid = false; DateTimeOffset? expires = null;
                long? maxAge = null; string? sameSite = null;
                foreach (var part in parts.Skip(1))
                {
                    var attribute = part.Split('=', 2); var content = attribute.Length > 1 ? attribute[1] : "";
                    switch (attribute[0].ToLowerInvariant())
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
                            if (DateTimeOffset.TryParse(content, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var expiry)) expires = expiry.ToUniversalTime();
                            else invalid = true;
                            break;
                        case "samesite": sameSite = content.ToLowerInvariant() switch { "none" => "None", "lax" => "Lax", "strict" => "Strict", _ => null }; break;
                    }
                }
                if (invalid) continue;
                try
                {
                    if (maxAge is { } age) expires = age <= 0 ? now.AddSeconds(-1) : now.AddSeconds(age);
                    var cookie = new MoocSessionCookie(name, value, domain, path, secure, httpOnly, expires, sameSite);
                    if (!MoocSessionStore.IsValid(cookie)) continue;
                    var host = cookie.Domain.TrimStart('.');
                    if (!address.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase)
                        && !(cookie.Domain.StartsWith('.') && address.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))) continue;
                    result[cookie.Key] = cookie;
                }
                catch (Exception error) when (error is CookieException or ArgumentOutOfRangeException) { }
            }
        }
        return result.Values.ToArray();
    }
}
