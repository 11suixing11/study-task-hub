using System.Net;
using System.Text;
using AccessProbe;

public static class ChaoxingSessionChecks
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Session check failed: " + name);
            checks++;
        }
        var now = DateTimeOffset.UtcNow;
        var address = new Uri("https://i.chaoxing.com/base");
        var session = new ChaoxingSessionCookie("SYNTHETIC_AUTH", "synthetic-session-only", ".chaoxing.com", "/",
            true, true, null, "Lax");
        var persistent = session with { Name = "SYNTHETIC_PERSIST", ExpiresUtc = now.AddHours(3), SameSite = "Strict" };
        var expired = session with { Name = "SYNTHETIC_EXPIRED", ExpiresUtc = now.AddMinutes(-1) };
        var directory = Path.Combine(AppContext.BaseDirectory, "session-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.bin");
        var store = new ChaoxingSessionStore(path);
        Check(store.Load(now).Count == 0 && store.LastLoadOutcome == "not_saved", "missing archive");
        store.Save([session, persistent, expired, session with { Domain = ".example.com" }], now);
        var saved = File.ReadAllBytes(path);
        Check(!Encoding.UTF8.GetString(saved).Contains(session.Value, StringComparison.Ordinal)
            && !Encoding.UTF8.GetString(saved).Contains(session.Name, StringComparison.Ordinal), "DPAPI archive excludes plaintext");
        var restored = new ChaoxingSessionStore(path).Load(now.AddMinutes(1));
        Check(restored.Count == 2 && restored.Contains(session) && restored.Contains(persistent), "new store reloads flags and expiry");
        Check(restored.Single(cookie => cookie.Name == session.Name).ToCookie().Expires == DateTime.MinValue, "session expiry unchanged");
        Check(restored.Single(cookie => cookie.Name == persistent.Name).ToCookie().Expires == persistent.ExpiresUtc!.Value.UtcDateTime,
            "platform expiry unchanged");
        Check(store.Load(now.AddHours(4)).SequenceEqual(new[] { session }), "expired persistent cookie filtered");
        Check(store.Load(now.AddDays(31)).Count == 0 && store.LastLoadOutcome == "too_old", "archive age bounded");
        Check(store.Load(now.AddMinutes(-6)).Count == 0 && store.LastLoadOutcome == "invalid", "future archive rejected");
        store.Save([session with { Value = "synthetic-renewed" }], now);
        Check(store.Load(now).Single().Value == "synthetic-renewed", "atomic archive replacement");
        Check(!Directory.EnumerateFiles(directory).Any(file => file.EndsWith(".tmp", StringComparison.Ordinal)), "temporary archive removed");
        var damaged = File.ReadAllBytes(path); damaged[damaged.Length / 2] ^= 0x80; File.WriteAllBytes(path, damaged);
        Check(store.Load(now).Count == 0 && store.LastLoadOutcome == "unavailable", "damaged encrypted archive rejected");
        File.WriteAllText(path, "{}", Encoding.UTF8);
        Check(store.Load(now).Count == 0, "plaintext archive rejected");
        File.WriteAllBytes(path, new byte[2 * 1024 * 1024 + 1]);
        Check(store.Load(now).Count == 0 && store.LastLoadOutcome == "invalid", "oversized archive rejected");
        store.Clear();
        Check(!File.Exists(path) && store.Load(now).Count == 0, "explicit clear removes recovery");
        store.Save([expired], now);
        Check(!File.Exists(path), "expired snapshot does not persist");
        Check(ChaoxingSessionStore.MissingForRestore([session, persistent], []).Count == 2, "empty profile restores missing session");
        Check(ChaoxingSessionStore.MissingForRestore([session, persistent], [persistent]).SequenceEqual(new[] { session }), "matching profile restores only missing");
        Check(ChaoxingSessionStore.MissingForRestore([session, persistent], [persistent with { Value = "new-login" }]).Count == 0,
            "new profile login prevents mixed generations");
        Check(ChaoxingSessionStore.MissingForRestore([session, persistent], [session, session]).SequenceEqual(new[] { persistent }), "duplicate observations handled");
        Check(session.ToString() == nameof(ChaoxingSessionCookie), "record display omits credentials");
        foreach (var invalid in new[]
        {
            session with { Domain = ".chaoxing.com.example.org" }, session with { Domain = "..chaoxing.com" },
            session with { Domain = ".chaoxing.com." }, session with { Name = "bad=name" },
            session with { Value = "bad\r\nvalue" }, session with { Path = "relative" }, session with { SameSite = "invalid" },
            session with { Name = "__Secure-auth", Secure = false }, session with { Name = "__Host-auth" }
        }) Check(!ChaoxingSessionStore.IsValid(invalid), "invalid cookie or prefix");
        Check(ChaoxingSessionStore.IsValid(session with { Name = "__Host-auth", Domain = "i.chaoxing.com" }), "valid host prefix");
        Check(session.AppliesTo(address) && !session.AppliesTo(new Uri("https://other.example/base")), "domain cookie scope");
        var host = session with { Domain = "i.chaoxing.com", Path = "/base" };
        Check(host.AppliesTo(address) && host.AppliesTo(new Uri("https://i.chaoxing.com/base/x"))
            && !host.AppliesTo(new Uri("https://i.chaoxing.com/baseball"))
            && !host.AppliesTo(new Uri("https://child.i.chaoxing.com/base")), "host-only and path-boundary scope");

        var parsed = ChaoxingResponseCookies.Parse(address, ["SYNTHETIC_AUTH=renewed; Domain=chaoxing.com; Path=/; Secure; HttpOnly; SameSite=None"], now).Single();
        Check(parsed.Domain == ".chaoxing.com" && parsed.Secure && parsed.HttpOnly && parsed.SameSite == "None" && parsed.ExpiresUtc is null,
            "renewal attributes parsed");
        var defaultScope = ChaoxingResponseCookies.Parse(new Uri("https://i.chaoxing.com/base/page"), ["auth=host-only"], now).Single();
        Check(defaultScope.Domain == "i.chaoxing.com" && defaultScope.Path == "/base", "response default host and directory");
        var expiryHeader = "auth=value; Expires=Wed, 01 Jan 2031 00:00:00 GMT; Path=/";
        Check(ChaoxingResponseCookies.Parse(address, [expiryHeader], now).Single().ExpiresUtc == new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "expires comma preserved");
        var combined = ChaoxingResponseCookies.Parse(address, [expiryHeader + ", other=next; Path=/; Secure"], now);
        Check(combined.Count == 2 && combined.Any(cookie => cookie.Name == "other"), "combined set-cookie header");
        Check(ChaoxingResponseCookies.Parse(address, [expiryHeader + "; Max-Age=120"], now).Single().ExpiresUtc == now.AddSeconds(120),
            "max-age takes precedence");
        foreach (var value in new[] { "0", "-10" })
            Check(ChaoxingResponseCookies.Parse(address, ["auth=deleted; Max-Age=" + value + "; Path=/"], now).Single().IsExpired(now), "deletion retained");
        Check(ChaoxingResponseCookies.Parse(address, ["auth=old; Path=/", "auth=new; Path=/"], now).Single().Value == "new", "last response value wins");
        foreach (var header in new[]
        {
            "auth=bad; Domain=other.example; Path=/", "auth=bad; Domain=mooc1.chaoxing.com; Path=/", "not-a-pair",
            "auth=bad\r\nInjected=yes", "auth=bad; Max-Age=9223372036854775807", new string('x', 32769)
        }) Check(ChaoxingResponseCookies.Parse(address, [header], now).Count == 0, "invalid response cookie rejected");
        Check(ChaoxingResponseCookies.Parse(new Uri("https://other.example/"), ["auth=bad"], now).Count == 0
            && ChaoxingResponseCookies.Parse(new Uri("http://i.chaoxing.com/base"), ["auth=bad"], now).Count == 0,
            "only official HTTPS response cookies");

        IReadOnlyList<Cookie> current = [session.ToCookie()];
        var writes = 0;
        Task<IReadOnlyList<Cookie>> Read(Uri _, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(current); }
        Task Write(Uri _, IReadOnlyList<Cookie> before, IReadOnlyList<ChaoxingSessionCookie> changed, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (changed.Count > 0)
            {
                var map = current.Select(ChaoxingSessionCookie.FromCookie).ToDictionary(cookie => cookie.Key);
                foreach (var cookie in changed)
                { if (cookie.IsExpired(DateTimeOffset.UtcNow)) map.Remove(cookie.Key); else map[cookie.Key] = cookie; }
                current = map.Values.Select(cookie => cookie.ToCookie()).ToArray();
                writes++;
            }
            return Task.CompletedTask;
        }
        using (var reader = new ChaoxingHttpReader(Read, new Handler(request =>
        {
            Check(request.Headers.GetValues("Cookie").Single().Contains("SYNTHETIC_AUTH=synthetic-session-only", StringComparison.Ordinal), "outgoing profile cookie");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<script>var fid='901';</script>") };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "SYNTHETIC_AUTH=synthetic-renewed; Domain=chaoxing.com; Path=/; Secure; HttpOnly");
            return response;
        }), saveResponseCookies: Write)) Check((await reader.ReadAsync(address, CancellationToken.None)).Outcome == "ok", "renewal read succeeds");
        Check(writes == 1 && current.Single().Value == "synthetic-renewed", "server renewal written to shared profile");
        using (var reader = new ChaoxingHttpReader(Read, new Handler(request =>
        {
            Check(request.Headers.GetValues("Cookie").Single().Contains("synthetic-renewed", StringComparison.Ordinal), "new reader uses renewed session");
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.TryAddWithoutValidation("Set-Cookie", "SYNTHETIC_AUTH=; Domain=chaoxing.com; Path=/; Max-Age=0");
            return response;
        }), saveResponseCookies: Write)) Check((await reader.ReadAsync(address, CancellationToken.None)).Outcome == "login_required", "expiry remains a login requirement");
        Check(current.Count == 0 && writes == 2, "server deletion clears shared cookie");
        using (var reader = new ChaoxingHttpReader(Read, new Handler(request =>
        {
            Check(!request.Headers.Contains("Cookie"), "deleted cookie absent next round");
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }), saveResponseCookies: Write)) Check((await reader.ReadAsync(address, CancellationToken.None)).Outcome == "forbidden", "permission error distinct from login");

        current = [session.ToCookie()];
        var requests = 0;
        using (var reader = new ChaoxingHttpReader(Read, new Handler(request =>
        {
            requests++;
            if (requests == 2) Check(request.Headers.GetValues("Cookie").Single().Contains("standalone-renewed", StringComparison.Ordinal), "reader keeps renewal without callback");
            if (requests == 3) Check(request.Headers.GetValues("Cookie").Single().Contains("new-login", StringComparison.Ordinal), "changed profile overrides reader renewal");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<script>var fid='901';</script>") };
            if (requests == 1) response.Headers.TryAddWithoutValidation("Set-Cookie", "SYNTHETIC_AUTH=standalone-renewed; Domain=chaoxing.com; Path=/; Secure; HttpOnly");
            return response;
        })))
        {
            await reader.ReadAsync(address, CancellationToken.None);
            await reader.ReadAsync(address, CancellationToken.None);
            current = [(session with { Value = "new-login" }).ToCookie()];
            await reader.ReadAsync(address, CancellationToken.None);
        }
        current = [host.ToCookie()];
        using (var reader = new ChaoxingHttpReader(Read, new Handler(request =>
        {
            Check(!request.Headers.Contains("Cookie"), "host-only cookie never sent to course host");
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }))) await reader.ReadAsync(new Uri(ChaoxingSyncParser.Catalog), CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        using (var reader = new ChaoxingHttpReader(Read, new Handler(_ =>
        {
            cancelled.Cancel();
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("Set-Cookie", "auth=stale; Path=/");
            return response;
        }), saveResponseCookies: Write))
        {
            var writesBefore = writes;
            try { await reader.ReadAsync(address, cancelled.Token); throw new Exception("Cancellation was lost."); }
            catch (OperationCanceledException) { Check(writes == writesBefore, "cancelled response cannot write session"); }
        }
        Console.WriteLine($"PASS: {checks} encrypted session, cookie renewal and scope checks.");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
}
