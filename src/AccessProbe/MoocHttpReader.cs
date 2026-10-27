using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace AccessProbe;

public sealed class MoocHttpReader : IMoocMetadataReader, IDisposable
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Func<Uri, CancellationToken, Task<IReadOnlyList<MoocSessionCookie>>> readCookies;
    private readonly Func<Uri, IReadOnlyList<MoocSessionCookie>, IReadOnlyList<MoocSessionCookie>, CancellationToken, Task>? saveCookies;
    private readonly bool spacing;
    private bool rateLimited;
    private DateTimeOffset nextRequestAt;
    private sealed record Renewal(MoocSessionCookie Cookie, MoocSessionCookie? Previous);
    private readonly Dictionary<string, Renewal> responseCookies = new(StringComparer.Ordinal);

    public MoocHttpReader(Func<Uri, CancellationToken, Task<IReadOnlyList<MoocSessionCookie>>> readCookies,
        HttpMessageHandler? testHandler = null,
        Func<Uri, IReadOnlyList<MoocSessionCookie>, IReadOnlyList<MoocSessionCookie>, CancellationToken, Task>? saveResponseCookies = null)
    {
        this.readCookies = readCookies; saveCookies = saveResponseCookies; spacing = testHandler is null;
        client = new(testHandler ?? new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli })
        { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LearningTaskAggregator/1.2");
        client.DefaultRequestHeaders.Referrer = MoocPlatform.Home;
        client.DefaultRequestHeaders.Add("Origin", "https://" + MoocPlatform.Host);
    }

    public async Task<MoocResponse> ReadAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken cancellation)
    {
        if (!MoocPlatform.IsReadRequest(path, fields)) return new("unsupported_request", null);
        if (rateLimited) return new("rate_limited", 429);
        var baseAddress = new Uri("https://" + MoocPlatform.Host + path);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var observed = await readCookies(baseAddress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var profile = observed.Where(cookie => MoocSessionStore.IsValid(cookie) && cookie.AppliesTo(baseAddress))
                .DistinctBy(cookie => cookie.Key).ToDictionary(cookie => cookie.Key, StringComparer.Ordinal);
            var outgoing = new Dictionary<string, MoocSessionCookie>(profile, StringComparer.Ordinal);
            foreach (var (key, renewal) in responseCookies.ToArray())
            {
                if (!renewal.Cookie.AppliesTo(baseAddress)) continue;
                if (profile.GetValueOrDefault(key) != renewal.Previous) { responseCookies.Remove(key); continue; }
                outgoing[key] = renewal.Cookie;
            }
            // WebView permits comma-containing values (for example Baidu page-view timestamps).
            // CookieContainer rejects those browser cookies. Preserve WebView's values and scope exactly.
            var sending = outgoing.Values.Where(cookie => MoocSessionStore.IsValid(cookie) && cookie.AppliesTo(baseAddress)
                && !cookie.IsExpired(DateTimeOffset.UtcNow)).OrderByDescending(cookie => cookie.Path.Length).ToArray();
            var csrfValues = sending.Where(cookie => cookie.Name == "NTESSTUDYSI")
                .Select(cookie => cookie.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (csrfValues.Length != 1 || string.IsNullOrWhiteSpace(csrfValues[0]) || csrfValues[0].Length > 2048)
                return new("csrf_missing", null);
            var address = new Uri(baseAddress.AbsoluteUri + "?csrfKey=" + Uri.EscapeDataString(csrfValues[0]));
            using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = new FormUrlEncodedContent(fields) };
            var header = string.Join("; ", sending.Select(cookie => cookie.Name + "=" + cookie.Value));
            if (header.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", header);
            request.Headers.TryAddWithoutValidation("edu-script-token", csrfValues[0]);
            if (spacing && nextRequestAt - DateTimeOffset.UtcNow is { } delay && delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(180);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var renewed = response.Headers.TryGetValues("Set-Cookie", out var values) ? MoocResponseCookies.Parse(baseAddress, values, DateTimeOffset.UtcNow) : [];
            if (saveCookies is not null) await saveCookies(baseAddress, observed, renewed, cancellation);
            foreach (var cookie in renewed) responseCookies[cookie.Key] = new(cookie, profile.GetValueOrDefault(cookie.Key));
            var status = (int)response.StatusCode;
            if (status == 401) return new("login_required", status);
            if (status == 403) return new("forbidden", status);
            if (status == 429) { rateLimited = true; return new("rate_limited", status); }
            if (status is >= 300 and < 400)
            {
                if (response.Headers.Location is { } location && Uri.TryCreate(baseAddress, location, out var target)
                    && MoocPlatform.IsLoginNavigation(target.AbsoluteUri)
                    && target.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)) return new("login_required", status);
                return new("unsupported_redirect", status);
            }
            if (!response.IsSuccessStatusCode) return new("http_error", status);
            if (response.Content.Headers.ContentLength > MaximumBytes) return new("response_too_large", status);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var body = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
            {
                if (body.Length + count > MaximumBytes) return new("response_too_large", status);
                body.Write(buffer, 0, count);
            }
            var json = Encoding.UTF8.GetString(body.ToArray()); var outcome = MoocParser.EnvelopeOutcome(json);
            return new(outcome, status, outcome == "ok" ? json : "");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new("timeout", null); }
        catch (HttpRequestException) { return new("network_error", null); }
        catch (CookieException) { return new("session_unavailable", null); }
    }
    public void Dispose() => client.Dispose();
}
