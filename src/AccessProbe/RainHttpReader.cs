using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace AccessProbe;

public sealed class RainHttpReader : IRainMetadataReader, IDisposable
{
    private readonly RainPlatform platform;
    private readonly string host;
    private readonly HttpClient client;
    private readonly CookieContainer cookies = new();
    private readonly Func<Uri, CancellationToken, Task<IReadOnlyList<Cookie>>> readCookies;
    private readonly bool applyRequestSpacing;
    private DateTimeOffset nextRequestAt;
    private bool rateLimited;

    public RainHttpReader(RainPlatform platform, string host,
        Func<Uri, CancellationToken, Task<IReadOnlyList<Cookie>>> readCookies, HttpMessageHandler? testHandler = null)
    {
        this.platform = platform; this.host = platform.CheckHost(host); this.readCookies = readCookies;
        applyRequestSpacing = testHandler is null;
        var handler = testHandler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        client = new(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LearningTaskAggregator/1.1");
        // These static headers are the official web client's GET contract, verified from the public client bundle.
        client.DefaultRequestHeaders.Add("XTBZ", "ykt");
        client.DefaultRequestHeaders.Add("Xt-Agent", "web");
        client.DefaultRequestHeaders.Referrer = platform.IndexUri(this.host);
    }

    public async Task<RainResponse> ReadAsync(Uri address, CancellationToken cancellation)
    {
        if (!RainAddressPolicy.IsReadAddress(platform, host, address)) return new("unsupported_address", null);
        if (rateLimited) return new("rate_limited", 429);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var applicable = await readCookies(address, cancellation);
            foreach (var cookie in applicable)
                if (!cookie.Expired && !cookies.GetCookies(address).Cast<Cookie>().Any(existing => existing.Name == cookie.Name
                    && existing.Domain == cookie.Domain && existing.Path == cookie.Path)) cookies.Add(address, cookie);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            // University and CSRF values are copied only from the selected profile, never fabricated or logged.
            var currentCookies = cookies.GetCookies(address).Cast<Cookie>().ToArray();
            var university = new[] { "uv-id", "uv_id", "university-id", "university_id" }
                .Select(name => currentCookies.FirstOrDefault(cookie => cookie.Name == name && !cookie.Expired)?.Value)
                .FirstOrDefault(RainAddressPolicy.IsId);
            request.Headers.Add("university-id", university ?? "0");
            request.Headers.Add("uv-id", university ?? "0");
            var csrf = currentCookies.FirstOrDefault(cookie => cookie.Name == "csrftoken" && !cookie.Expired)?.Value;
            if (RainAddressPolicy.IsToken(csrf)) request.Headers.Add("X-CSRFToken", csrf);
            if (applyRequestSpacing && nextRequestAt - DateTimeOffset.UtcNow is { } delay && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellation);
            nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(150);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            var status = (int)response.StatusCode;
            if (status == 401) return new("login_required", status);
            if (status == 403) return new("forbidden", status);
            if (status == 429) { rateLimited = true; return new("rate_limited", status); }
            if (status is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is not null && Uri.TryCreate(address, location, out var next)
                    && RainAddressPolicy.IsNavigation(platform, next.OriginalString) && RainAddressPolicy.IsAuthentication(next))
                    return new("login_required", status);
                return new("unsupported_redirect", status);
            }
            if (!response.IsSuccessStatusCode) return new("http_error", status);
            if (response.Content.Headers.ContentLength > RainAddressPolicy.MaximumBytes) return new("response_too_large", status);
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !media.Contains("json", StringComparison.OrdinalIgnoreCase)) return new("unexpected_content", status);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var body = new MemoryStream();
            var buffer = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
            {
                if (body.Length + count > RainAddressPolicy.MaximumBytes) return new("response_too_large", status);
                body.Write(buffer, 0, count);
            }
            var json = Encoding.UTF8.GetString(body.ToArray());
            var outcome = RainClassroomParser.EnvelopeOutcome(json);
            return new(outcome, status, outcome == "ok" ? json : "");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new("timeout", null); }
        catch (HttpRequestException) { return new("network_error", null); }
        catch (CookieException) { return new("session_unavailable", null); }
    }

    public void Dispose() => client.Dispose();
}
