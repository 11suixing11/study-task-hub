using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccessProbe;

public sealed record MetadataSessionCookie(string Name, string Value, string Domain, string Path,
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
    public override string ToString() => nameof(MetadataSessionCookie);
}

public sealed class MetadataSessionStore(MetadataPlatform platform, string path, TaskStore? repository = null)
{
    private readonly PlatformSessionArchive archive = new(platform.Id, path, repository);
    private const int MaximumBytes = 2 * 1024 * 1024;
    private readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("LearningTaskAggregator." + platform.Id + "-session.v1"));
    private sealed record Snapshot(int Schema, DateTimeOffset SavedAtUtc, MetadataSessionCookie[] Cookies, string? Token = null, string? TokenBinding = null, string? TokenSource = null);
    public string? Token { get; private set; }
    public string? TokenBinding { get; private set; }
    public string? TokenSource { get; private set; }
    public string LastLoadOutcome { get; private set; } = "not_loaded";

    public IReadOnlyList<MetadataSessionCookie> Load(DateTimeOffset now) => LoadCore(now, false);
    public IReadOnlyList<MetadataSessionCookie> LoadForRevalidation(DateTimeOffset now) => LoadCore(now, platform.Id is "ucampus" or "zhihuishu");
    private IReadOnlyList<MetadataSessionCookie> LoadCore(DateTimeOffset now, bool includeExpired)
    {
        Token = TokenBinding = TokenSource = null;
        if (!OperatingSystem.IsWindows()) { LastLoadOutcome = "unavailable"; return []; }
        byte[]? plain = null;
        try
        {
            var bytes = archive.Read(includeExpired);
            if (bytes is null) { LastLoadOutcome = "not_saved"; return []; }
            plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            var value = JsonSerializer.Deserialize<Snapshot>(plain);
            if (plain.Length > MaximumBytes || value is null || value.Schema is not (1 or 2) || value.Cookies is null
                || value.Cookies.Length > 512 || value.SavedAtUtc > now.AddMinutes(5) || value.Cookies.Any(cookie => !IsValid(platform, cookie))
                || value.Token is { } token && (platform.Id != "zhihuishu" || token.Length > 16384 || token.Any(char.IsControl)
                    || value.TokenBinding is not { Length: 64 } binding || !binding.All(char.IsAsciiHexDigit))
                || value.Token is null && value.TokenBinding is not null
                || value.TokenSource is not (null or "https://onlineweb.zhihuishu.com" or "https://onlinestuh5.zhihuishu.com"))
            { LastLoadOutcome = "invalid"; return []; }
            if (!archive.UsesRepository && value.SavedAtUtc < now.AddDays(-30)) { LastLoadOutcome = "too_old"; return []; }
            var result = value.Cookies.Where(cookie => !cookie.IsExpired(now)).DistinctBy(cookie => cookie.Key).ToArray();
            Token = value.Token; TokenBinding = value.TokenBinding;
            TokenSource = value.TokenSource;
            if (value.Schema == 1 && value.Token is not null)
            {
                // Migrate only a binding proved by the original protected snapshot. Expired auth still rejects the token.
                var original = CookieBinding(platform, value.Cookies, legacy: true);
                if (original == value.TokenBinding && CookieBinding(platform, value.Cookies) == CookieBinding(platform, result))
                    TokenBinding = CookieBinding(platform, result);
                else Token = TokenBinding = TokenSource = null;
            }
            LastLoadOutcome = result.Length > 0 ? "available" : "expired_or_empty"; return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { LastLoadOutcome = "unavailable"; return []; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(IEnumerable<MetadataSessionCookie> observed, DateTimeOffset now, string? token = null, string? tokenBinding = null, string? tokenSource = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows local protection is required.");
        if (token is not null && (platform.Id != "zhihuishu" || token.Length > 16384 || token.Any(char.IsControl)
            || tokenBinding is not { Length: 64 } binding || !binding.All(char.IsAsciiHexDigit)) || token is null && tokenBinding is not null)
            throw new InvalidDataException("Invalid session token binding.");
        if (tokenSource is not (null or "https://onlineweb.zhihuishu.com" or "https://onlinestuh5.zhihuishu.com"))
            throw new InvalidDataException("Invalid session token source.");
        var cookies = observed.Where(cookie => IsValid(platform, cookie) && !cookie.IsExpired(now)).DistinctBy(cookie => cookie.Key).Take(513).ToArray();
        if (cookies.Length > 512) throw new InvalidDataException("Session cookie limit exceeded.");
        if (cookies.Length == 0) { Clear(); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(2, now, cookies, token, tokenBinding, tokenSource));
        try
        {
            if (plain.Length > MaximumBytes) throw new InvalidDataException("Session archive limit exceeded.");
            var bytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            archive.Write(bytes, now);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Clear() => archive.Clear();
    public void Invalidate(DateTimeOffset now) => archive.Invalidate(now, platform.Id is "ucampus" or "zhihuishu");

    public static bool IsValid(MetadataPlatform platform, MetadataSessionCookie? cookie)
    {
        if (cookie is null || string.IsNullOrEmpty(cookie.Name) || cookie.Name.Length > 256
            || cookie.Value is null || cookie.Value.Length > 16384 || string.IsNullOrEmpty(cookie.Domain)
            || !platform.IsCookieDomain(cookie.Domain.TrimStart('.')) || cookie.Domain.StartsWith("..", StringComparison.Ordinal)
            || cookie.Domain.EndsWith('.') || cookie.Domain.Length > 255 || string.IsNullOrEmpty(cookie.Path)
            || !cookie.Path.StartsWith('/') || cookie.Path.Length > 2048 || cookie.Path.Any(char.IsControl)
            || cookie.Domain.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
            || cookie.Name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is ';' or ',' or '=')
            || cookie.Value.Any(character => char.IsControl(character) || character == ';') || cookie.SameSite is not (null or "None" or "Lax" or "Strict")
            || cookie.Name.StartsWith("__Secure-", StringComparison.Ordinal) && !cookie.Secure
            || cookie.Name.StartsWith("__Host-", StringComparison.Ordinal) && (!cookie.Secure || cookie.Domain.StartsWith('.') || cookie.Path != "/")) return false;
        try { _ = cookie.ToCookie(); return true; } catch (CookieException) { return false; }
    }

    public static IReadOnlyList<MetadataSessionCookie> MissingForRestore(MetadataPlatform platform, IReadOnlyList<MetadataSessionCookie> saved, IReadOnlyList<MetadataSessionCookie> current)
    {
        var existing = current.DistinctBy(cookie => cookie.Key).ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
        if (current.Any(cookie => platform.IsAccountCookie(cookie) && !saved.Any(value => value.Key == cookie.Key && value.Value == cookie.Value))) return [];
        if (saved.Any(cookie => platform.IsAccountCookie(cookie) && existing.TryGetValue(cookie.Key, out var value) && value.Value != cookie.Value)) return [];
        return saved.Where(cookie => !existing.ContainsKey(cookie.Key)).ToArray();
    }

    public static string CookieBinding(MetadataPlatform platform, IEnumerable<MetadataSessionCookie> cookies, bool legacy = false)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cookies.Where(cookie => legacy ? platform.IsLegacyAccountCookie(cookie) : platform.IsAccountCookie(cookie))
            .OrderBy(cookie => cookie.Key, StringComparer.Ordinal).Select(cookie => new[] { cookie.Key, cookie.Value }));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

}

public static class MetadataResponseCookies
{
    private static readonly Regex Boundary = new(@",(?=\s*[^=;,\s]+=)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static IReadOnlyList<MetadataSessionCookie> Parse(MetadataPlatform platform, Uri address, IEnumerable<string> headers, DateTimeOffset now)
    {
        if (!TaskRules.IsSafeLink(address.AbsoluteUri) || !platform.IsCookieDomain(address.IdnHost)) return [];
        var result = new Dictionary<string, MetadataSessionCookie>(StringComparer.Ordinal);
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
                    var cookie = new MetadataSessionCookie(name, value, domain, path, secure, httpOnly, expires, sameSite);
                    if (!MetadataSessionStore.IsValid(platform, cookie)) continue;
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
