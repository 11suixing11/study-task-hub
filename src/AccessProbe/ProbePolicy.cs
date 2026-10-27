namespace AccessProbe;

public static class ProbePolicy
{
    // Upgrade only existing official HTTP links. Preserve the opaque path/query/fragment bytes.
    public static bool TryGetHttpsNavigation(string address, out string httpsAddress)
    {
        httpsAddress = string.Empty;
        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || address.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)
                || character is '\\' or '\"' or '\'' or '<' or '>')
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo) || !IsOfficialHost(uri.IdnHost)) return false;

        var authorityEnd = address.IndexOfAny(['/', '?', '#'], 7);
        if (authorityEnd < 0) authorityEnd = address.Length;
        var authority = address[7..authorityEnd];
        // An explicit HTTP default port must not turn into HTTPS on port 80.
        var portSeparator = authority.LastIndexOf(':');
        if (portSeparator >= 0) authority = authority[..portSeparator];
        var upgraded = "https://" + authority + address[authorityEnd..];
        if (!IsAllowedNavigation(upgraded)) return false;
        httpsAddress = upgraded;
        return true;
    }

    public static string NavigationCategory(string address)
    {
        if (address == "about:blank") return "blank";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return "invalid_address";
        if (uri.Scheme == Uri.UriSchemeHttp) return "insecure_http";
        if (uri.Scheme != Uri.UriSchemeHttps) return "external_protocol";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "embedded_credentials";
        if (!uri.IsDefaultPort) return "non_default_port";
        return IsOfficialHost(uri.IdnHost) ? "official_https" : "external_domain";
    }

    public static bool IsAllowedNavigation(string address)
    {
        if (address == "about:blank") return true;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && IsOfficialHost(uri.IdnHost);
    }

    public static bool IsOfficialHost(string host) =>
        IsDomain(host, "chaoxing.com") || IsDomain(host, "xuexitong.com");

    private static bool IsDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    public static string SafeResponseCategory(string address, int statusCode)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !IsOfficialHost(uri.IdnHost)) return "other";
        var family = statusCode / 100;
        return family is >= 1 and <= 5 ? $"official-http-{family}xx" : "official-http-unknown";
    }
}
