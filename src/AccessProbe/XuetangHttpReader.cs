using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace AccessProbe;

public sealed class XuetangHttpReader : IXuetangMetadataReader, IDisposable
{
    private readonly HttpClient client;
    private readonly Func<Uri, CancellationToken, Task<IReadOnlyList<XuetangSessionCookie>>> readCookies;
    private readonly Func<Uri, IReadOnlyList<XuetangSessionCookie>, IReadOnlyList<XuetangSessionCookie>, CancellationToken, Task>? saveCookies;
    private readonly bool spacing;
    private bool rateLimited;
    private DateTimeOffset nextRequestAt;
    private sealed record Renewal(XuetangSessionCookie Cookie, XuetangSessionCookie? Previous);
    private readonly Dictionary<string, Renewal> responseCookies = new(StringComparer.Ordinal);

    public XuetangHttpReader(Func<Uri, CancellationToken, Task<IReadOnlyList<XuetangSessionCookie>>> readCookies,
        HttpMessageHandler? testHandler = null,
        Func<Uri, IReadOnlyList<XuetangSessionCookie>, IReadOnlyList<XuetangSessionCookie>, CancellationToken, Task>? saveResponseCookies = null)
    {
        this.readCookies = readCookies; saveCookies = saveResponseCookies; spacing = testHandler is null;
        client = new(testHandler ?? new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli })
        { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LearningTaskAggregator/1.3");
        client.DefaultRequestHeaders.Referrer = XuetangPlatform.Home;
        client.DefaultRequestHeaders.Add("Origin", "https://" + XuetangPlatform.Host);
        client.DefaultRequestHeaders.Add("app-name", "xtzx");
        client.DefaultRequestHeaders.Add("terminal-type", "web");
        client.DefaultRequestHeaders.Add("xtbz", "xt");
        client.DefaultRequestHeaders.Add("x-client", "web");
        client.DefaultRequestHeaders.Add("django-language", "zh");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh");
    }
    public async Task<XuetangResponse> ReadAsync(Uri address, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!XuetangPlatform.IsReadAddress(address)) return new("unsupported_request", null);
        if (rateLimited) return new("rate_limited", 429);
        try
        {
            var observed = await readCookies(address, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var profile = observed.Where(cookie => XuetangSessionStore.IsValid(cookie) && cookie.AppliesTo(address))
                .DistinctBy(cookie => cookie.Key).ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
            var outgoing = new Dictionary<string, XuetangSessionCookie>(profile, StringComparer.Ordinal);
            foreach (var (key, renewal) in responseCookies.ToArray())
            {
                if (!renewal.Cookie.AppliesTo(address)) continue;
                if (profile.GetValueOrDefault(key) != renewal.Previous) { responseCookies.Remove(key); continue; }
                outgoing[key] = renewal.Cookie;
            }
            var sending = outgoing.Values.Where(cookie => XuetangSessionStore.IsValid(cookie) && cookie.AppliesTo(address)
                && !cookie.IsExpired(DateTimeOffset.UtcNow)).OrderByDescending(cookie => cookie.Path.Length).ToArray();
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            var header = string.Join("; ", sending.Select(cookie => cookie.Name + "=" + cookie.Value));
            if (header.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", header);
            var csrf = sending.Where(cookie => cookie.Name == "csrftoken").Select(cookie => cookie.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (csrf.Length > 1) return new("session_ambiguous", null);
            if (csrf.Length == 1 && csrf[0].Length > 0) request.Headers.TryAddWithoutValidation("X-CSRFToken", csrf[0]);
            if (spacing && nextRequestAt - DateTimeOffset.UtcNow is { } delay && delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(180);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var status = (int)response.StatusCode;
            // Renewal belongs to the requested official host; unexpected cross-origin responses are never reused.
            if (response.RequestMessage?.RequestUri is { } actual && actual != address) return new("unsupported_redirect", status);
            var renewed = response.Headers.TryGetValues("Set-Cookie", out var values) ? XuetangResponseCookies.Parse(address, values, DateTimeOffset.UtcNow) : [];
            if (saveCookies is not null) await saveCookies(address, observed, renewed, cancellation);
            cancellation.ThrowIfCancellationRequested();
            foreach (var cookie in renewed) responseCookies[cookie.Key] = new(cookie, profile.GetValueOrDefault(cookie.Key));
            if (status == 401) return new("login_required", status);
            if (status == 403) return new("forbidden", status);
            if (status == 429) { rateLimited = true; return new("rate_limited", status); }
            if (status is >= 300 and < 400)
            {
                if (response.Headers.Location is { } location && Uri.TryCreate(address, location, out var target)
                    && XuetangPlatform.IsLoginNavigation(target.AbsoluteUri) && target.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase))
                    return new("login_required", status);
                return new("unsupported_redirect", status);
            }
            if (!response.IsSuccessStatusCode) return new("http_error", status);
            if (response.Content.Headers.ContentLength > XuetangParser.MaximumBytes) return new("response_too_large", status);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var body = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
            {
                if (body.Length + count > XuetangParser.MaximumBytes) return new("response_too_large", status);
                body.Write(buffer, 0, count);
            }
            cancellation.ThrowIfCancellationRequested();
            var json = Encoding.UTF8.GetString(body.ToArray());
            var outcome = address.AbsolutePath == XuetangPlatform.LoginCheck ? XuetangParser.LoginOutcome(json) : XuetangParser.EnvelopeOutcome(json);
            return new(outcome, status, outcome == "ok" ? json : "");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new("timeout", null); }
        catch (HttpRequestException) { return new("network_error", null); }
        catch (CookieException) { return new("session_unavailable", null); }
    }
    public void Dispose() => client.Dispose();
}
