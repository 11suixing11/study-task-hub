using AccessProbe;
using System.Security.Cryptography;
using System.Text.Json;

namespace AccessProbe.Mobile;

public sealed record MobileSession(string Id, string PlatformId, DateTimeOffset SavedAtUtc,
    MetadataSessionCookie[] Cookies, string? AccountId = null, string? Token = null,
    string? TokenBinding = null, string[]? InferredScopeKeys = null)
{
    public override string ToString() => nameof(MobileSession);
}

public static class MobileSessionRules
{
    public static bool IsValid(MobileSession value, string platform, DateTimeOffset now) =>
        value.PlatformId == platform && CourseRecordingRules.SupportsAutomatic(platform)
        && value.Id is { Length: 32 } && value.Id.All(char.IsAsciiHexDigit)
        && value.SavedAtUtc <= now.AddMinutes(5) && value.Cookies is { Length: > 0 and <= 512 }
        && value.Cookies.All(cookie => CookieValid(platform, cookie))
        && value.Cookies.Select(cookie => cookie.Key).Distinct(StringComparer.Ordinal).Count() == value.Cookies.Length
        && (value.AccountId is null || MetadataTaskNavigation.IsId(value.AccountId))
        && (value.Token is null && value.TokenBinding is null || platform == "zhihuishu"
            && value.Token is { Length: > 0 and <= 16384 } && !value.Token.Any(char.IsControl)
            && value.TokenBinding == CookieFingerprint(platform, value.Cookies)
            && Account(platform, value.Cookies) is not null)
        && (value.InferredScopeKeys is null || value.InferredScopeKeys.Length <= 512
            && value.InferredScopeKeys.All(key => value.Cookies.Any(cookie => cookie.Key == key && !cookie.Domain.StartsWith('.'))));

    public static bool CookieValid(string platform, MetadataSessionCookie? cookie)
    {
        if (cookie is null) return false;
        if (platform is "ucampus" or "zhihuishu" or "pta") return MetadataSessionStore.IsValid(MetadataPlatform.Get(platform), cookie);
        if (platform == "xuetang") return XuetangSessionStore.IsValid(new(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite));
        if (platform == "mooc") return MoocSessionStore.IsValid(new(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite));
        var scoped = new ChaoxingSessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.ExpiresUtc, cookie.SameSite);
        return platform == "chaoxing" ? ChaoxingSessionStore.IsValid(scoped)
            : platform is "yuketang" or "changjiang" && RainSessionStore.IsValid(RainPlatform.Find(platform), scoped);
    }

    public static bool IsAccountCookie(string platform, MetadataSessionCookie cookie) => platform is "ucampus" or "zhihuishu" or "pta"
        ? MetadataPlatform.Get(platform).IsAccountCookie(cookie)
        : !cookie.Name.StartsWith("_ga", StringComparison.Ordinal) && !cookie.Name.StartsWith("Hm_", StringComparison.Ordinal)
            && cookie.Name is not ("sensorsdata2015jssdkcross" or "_gid" or "_gat" or "django_language");

    public static string CookieFingerprint(string platform, IEnumerable<MetadataSessionCookie> cookies)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cookies.Where(cookie => IsAccountCookie(platform, cookie))
            .OrderBy(cookie => cookie.Key, StringComparer.Ordinal).Select(cookie => new[] { cookie.Key, cookie.Value }));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Fingerprint(MobileSession session)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { CookieFingerprint(session.PlatformId, session.Cookies), session.AccountId, session.Token, session.TokenBinding });
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string? Account(string platform, IEnumerable<MetadataSessionCookie> cookies) => platform switch
    {
        "zhihuishu" => MetadataPlatformParser.ZhihuishuAccount(cookies),
        "ucampus" => MetadataPlatformParser.UcampusAccount(cookies),
        _ => null
    };

    public static MobileSession Renew(MobileSession current, IReadOnlyList<MetadataSessionCookie> observed,
        IReadOnlyList<MetadataSessionCookie> changes, DateTimeOffset now)
    {
        var cookies = current.Cookies.ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
        var inferred = new HashSet<string>(current.InferredScopeKeys ?? [], StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!CookieValid(current.PlatformId, change)) throw new InvalidDataException("Invalid response cookie scope.");
            var before = observed.FirstOrDefault(cookie => cookie.Key == change.Key);
            cookies.TryGetValue(change.Key, out var actual);
            if (actual is not null && actual.Value != before?.Value) continue;
            // WebView exposes pairs without scope. A known Set-Cookie replaces only matching inferred aliases.
            foreach (var alias in cookies.Values.Where(cookie => inferred.Contains(cookie.Key) && cookie.Key != change.Key
                && cookie.Name == change.Name && cookie.Path == change.Path
                && observed.Any(sent => sent.Name == cookie.Name && sent.Path == cookie.Path && sent.Value == cookie.Value)
                && change.AppliesTo(new Uri("https://" + cookie.Domain + cookie.Path))).ToArray())
            { cookies.Remove(alias.Key); inferred.Remove(alias.Key); }
            inferred.Remove(change.Key);
            if (change.IsExpired(now)) cookies.Remove(change.Key); else cookies[change.Key] = change;
        }
        var previousAccount = Account(current.PlatformId, current.Cookies);
        var nextAccount = Account(current.PlatformId, cookies.Values);
        if (previousAccount is not null && nextAccount is not null && previousAccount != nextAccount)
            throw new OperationCanceledException("The response belongs to a different official account.");
        var preserveToken = current.Token is not null && previousAccount is not null && nextAccount == previousAccount
            && current.TokenBinding == CookieFingerprint(current.PlatformId, current.Cookies);
        if (cookies.Count == 0) throw new OperationCanceledException("The official login was cleared.");
        return current with { Cookies = cookies.Values.ToArray(), SavedAtUtc = now,
            Token = preserveToken ? current.Token : null,
            TokenBinding = preserveToken ? CookieFingerprint(current.PlatformId, cookies.Values) : null,
            InferredScopeKeys = inferred.ToArray() };
    }
}
