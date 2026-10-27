using System.Net;
using System.Net.Http;
using System.Text;
using System.IO;

namespace AccessProbe;

public sealed record ChaoxingRedirectObservation(int StatusCode, int Hop, string TargetHost, string TargetPath,
    IReadOnlyList<string> ParameterNames);

public sealed class ChaoxingHttpReader : IChaoxingPageReader, IDisposable
{
    private readonly HttpClient client;
    private readonly Func<Uri, CancellationToken, Task<IReadOnlyList<Cookie>>> getCookies;
    private readonly Action<ChaoxingRedirectObservation>? observeRedirect;
    private readonly Func<Uri, IReadOnlyList<Cookie>, IReadOnlyList<ChaoxingSessionCookie>, CancellationToken, Task>? saveResponseCookies;
    private sealed record ResponseCookie(ChaoxingSessionCookie Cookie, ChaoxingSessionCookie? ProfileBefore);
    private readonly Dictionary<string, ResponseCookie> responseCookies = new(StringComparer.Ordinal);

    public ChaoxingHttpReader(Func<Uri, CancellationToken, Task<IReadOnlyList<Cookie>>> getCookies,
        HttpMessageHandler? testHandler = null, Action<ChaoxingRedirectObservation>? observeRedirect = null,
        Func<Uri, IReadOnlyList<Cookie>, IReadOnlyList<ChaoxingSessionCookie>, CancellationToken, Task>? saveResponseCookies = null)
    {
        this.getCookies = getCookies;
        this.observeRedirect = observeRedirect;
        this.saveResponseCookies = saveResponseCookies;
        var handler = testHandler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LearningTaskAggregator/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
    }

    public async Task<ChaoxingPage> ReadAsync(Uri address, CancellationToken cancellation)
    {
        if (!ChaoxingSyncParser.IsReadAddress(address)) return new("unsupported_redirect", null, address, "");
        var current = address;
        try
        {
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                cancellation.ThrowIfCancellationRequested();
                using var response = await ReadHeadersAsync(current, cancellation);
                var status = (int)response.StatusCode;
                if (status is 401) return new("login_required", status, current, "");
                if (status is 403) return new("forbidden", status, current, "");
                if (status is 429) return new("rate_limited", status, current, "");
                if (status is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null || !Uri.TryCreate(current, location, out var next)) return new("unsupported_redirect", status, current, "");
                    observeRedirect?.Invoke(DescribeRedirect(status, redirects + 1, next));
                    if (ChaoxingSyncParser.IsAuthenticationAddress(next)) return new("login_required", status, current, "");
                    if (next.Scheme == Uri.UriSchemeHttp && ProbePolicy.TryGetHttpsNavigation(next.OriginalString, out var https)) next = new Uri(https);
                    // The platform may route an unfinished work into answering. Preserve list metadata without entering it.
                    if (ChaoxingSyncParser.IsWorkActionRedirect(current, next)) return new("detail_requires_user_action", status, current, "");
                    if (!ChaoxingSyncParser.IsReadAddress(next) || !ChaoxingSyncParser.IsCourseRedirect(current, next)
                        || !ChaoxingSyncParser.IsWorkRedirect(current, next) || redirects == 5)
                        return new("unsupported_redirect", status, current, "");
                    current = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode) return new("http_error", status, current, "");
                if (response.Content.Headers.ContentLength > ChaoxingSyncParser.MaximumBytes)
                    return new("response_too_large", status, current, "");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
                using var body = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
                {
                    if (body.Length + count > ChaoxingSyncParser.MaximumBytes) return new("response_too_large", status, current, "");
                    body.Write(buffer, 0, count);
                }
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                Encoding encoding;
                try { encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
                catch (ArgumentException) { return new("encoding_unrecognized", status, current, ""); }
                var html = encoding.GetString(body.ToArray());
                if (ChaoxingSyncParser.IsLoginPage(html)) return new("login_required", status, current, "");
                if (html.Contains("没有此页面访问权限", StringComparison.Ordinal)) return new("forbidden", status, current, "");
                return new("ok", status, current, html);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return new("timeout", null, current, ""); }
        catch (HttpRequestException) { return new("network_error", null, current, ""); }
        return new("unsupported_redirect", null, current, "");
    }

    // User-clicked deep links only: inspect dispatcher response headers, never fetch the answering page or read its body.
    public async Task<TaskLinkResolution> ResolveTaskAddressAsync(Uri address, CancellationToken cancellation)
    {
        if (WorkListParser.ParseTaskLocation(address.OriginalString) is null) return new("invalid_link", "none");
        var current = address;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var hop = 0; hop < 6; hop++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!visited.Add(current.AbsoluteUri)) return new("redirect_loop", "none");
                using var response = await ReadHeadersAsync(current, cancellation);
                var status = (int)response.StatusCode;
                if (status == 401) return new("login_required", "none");
                if (status == 403) return new("forbidden", "none");
                if (status == 429) return new("rate_limited", "none");
                if (status is >= 300 and < 400)
                {
                    if (response.Headers.Location is not { } location || !Uri.TryCreate(current, location, out var target))
                        return new("unsupported_redirect", "none");
                    if (ChaoxingSyncParser.IsAuthenticationAddress(target)) return new("login_required", "none");
                    if (target.Scheme == Uri.UriSchemeHttp && ProbePolicy.TryGetHttpsNavigation(target.OriginalString, out var https))
                        target = new Uri(https);
                    if (TaskNavigationRules.Destination(address, target) is not { } destination)
                        return new("unsupported_redirect", "none");
                    // These are final user destinations. Do not GET them inside the aggregator.
                    if (destination is "answer" or "view" or "submitted") return new("ready", destination, target.OriginalString);
                    current = target;
                    continue;
                }
                if (!response.IsSuccessStatusCode) return new("http_error", "none");
                return new("ready", "entry", current.OriginalString);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new("timeout", "none"); }
        catch (HttpRequestException) { return new("network_error", "none"); }
        return new("redirect_loop", "none");
    }

    private async Task<HttpResponseMessage> ReadHeadersAsync(Uri address, CancellationToken cancellation)
    {
        // Copy only cookies applicable to this exact URL from the project's own WebView profile.
        var observed = await getCookies(address, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var profile = observed.Select(ChaoxingSessionCookie.FromCookie).Where(ChaoxingSessionStore.IsValid)
            .DistinctBy(cookie => cookie.Key).ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
        var outgoing = new Dictionary<string, ChaoxingSessionCookie>(profile, StringComparer.Ordinal);
        // A changed WebView cookie is a newer login/renewal. Otherwise retain this reader's response
        // updates (including deletions), even when a caller does not provide a write-back callback.
        foreach (var item in responseCookies.ToArray())
        {
            if (!item.Value.Cookie.AppliesTo(address)) continue;
            profile.TryGetValue(item.Key, out var current);
            if (current != item.Value.ProfileBefore) { responseCookies.Remove(item.Key); continue; }
            outgoing[item.Key] = item.Value.Cookie;
        }
        var cookies = new CookieContainer(512, 512, 32768);
        foreach (var cookie in outgoing.Values.Where(cookie => !cookie.IsExpired(DateTimeOffset.UtcNow) && cookie.AppliesTo(address)))
        {
            var scoped = cookie.ToCookie();
            if (!cookie.Domain.StartsWith('.'))
                scoped = new Cookie(cookie.Name, cookie.Value, cookie.Path)
                { Secure = cookie.Secure, HttpOnly = cookie.HttpOnly, Expires = scoped.Expires };
            cookies.Add(new Uri("https://" + cookie.Domain.TrimStart('.') + "/"), scoped);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        var header = cookies.GetCookieHeader(address);
        if (header.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", header);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var renewed = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? ChaoxingResponseCookies.Parse(address, values, DateTimeOffset.UtcNow) : [];
            if (saveResponseCookies is not null) await saveResponseCookies(address, observed, renewed, cancellation);
            cancellation.ThrowIfCancellationRequested();
            foreach (var cookie in renewed)
                responseCookies[cookie.Key] = new(cookie, profile.GetValueOrDefault(cookie.Key));
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    public void Dispose() => client.Dispose();

    private static ChaoxingRedirectObservation DescribeRedirect(int status, int hop, Uri target)
    {
        var official = ProbePolicy.IsOfficialHost(target.IdnHost);
        var path = official ? "/" + string.Join("/", target.AbsolutePath.Trim('/').Split('/').Select(segment =>
            segment is "mooc2-ans" or "mooc-ans" or "mooc2"
                || segment.Length is > 0 and <= 64 && segment.All(character => char.IsAsciiLetter(character) || character is '-' or '_' or '.')
                ? segment : "[parameter]")) : "external_path";
        var names = official ? target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)[0]).Where(name => name.Length is > 0 and <= 64
                && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            .Distinct(StringComparer.Ordinal).Take(24).ToArray() : [];
        return new(status, hop, official ? target.IdnHost : "external_domain", path, names);
    }
}
