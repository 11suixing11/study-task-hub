using System.Text;
using AccessProbe;
using Microsoft.Data.Sqlite;

internal static class PlatformSessionChecks
{
    private static int checks;
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "platform-session-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new TaskStore(Path.Combine(root, "tasks.db"));
        var now = DateTimeOffset.UtcNow;
        foreach (var site in PlatformCatalog.Sites.Where(site => CourseRecordingRules.SupportsAutomatic(site.Id)))
        {
            var path = Path.Combine(root, site.Id + ".bin");
            var adapter = Adapter(site.Id, path, store);
            Check(!store.LoadPlatformSessionInfo(site.Id).CanReuse, "fresh platform has no saved session");
            adapter.Save("synthetic-cookie-first-" + site.Id, now);
            store.ConfirmPlatformSession(site.Id, now);
            Check(store.LoadPlatformSessionInfo(site.Id).State == "ready" && adapter.Load(now).Single() == "synthetic-cookie-first-" + site.Id,
                "cookie round trip in repository for " + site.Id);
            Check(!File.Exists(path), "repository mode does not create a second cookie file");
            Check(adapter.Load(now.AddDays(45)).Count == 1, "session cookies follow official expiry instead of archive age");
            adapter.Save("synthetic-cookie-current-" + site.Id, now.AddSeconds(1));
            Check(adapter.Load(now.AddSeconds(2)).Single() == "synthetic-cookie-current-" + site.Id, "latest cookie replaces previous value");
            using (var db = new SqliteConnection("Data Source=" + store.DatabasePath))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT protected_cookies FROM platform_sessions WHERE platform_id=$id";
                cmd.Parameters.AddWithValue("$id", site.Id);
                var bytes = (byte[])cmd.ExecuteScalar()!;
                Check(bytes.Length > 32 && !Encoding.UTF8.GetString(bytes).Contains("synthetic-cookie", StringComparison.Ordinal), "cookie value is encrypted");
                File.WriteAllBytes(path, bytes);
            }
            adapter.Invalidate(now.AddSeconds(3));
            Check(store.LoadPlatformSessionInfo(site.Id).State == "expired" && adapter.Load(now.AddSeconds(4)).Count == 0,
                "rejected cookie cannot be restored");
            // A stale archive appearing after invalidation must not bypass the repository tombstone.
            File.WriteAllBytes(path, [1, 2, 3]);
            Check(adapter.Load(now.AddSeconds(4)).Count == 0 && store.LoadPlatformSessionInfo(site.Id).State == "expired", "stale archive cannot resurrect expired login");
            adapter.Save("synthetic-cookie-new-login-" + site.Id, now.AddSeconds(5));
            store.ConfirmPlatformSession(site.Id, now.AddSeconds(5));
            Check(adapter.Load(now.AddSeconds(6)).Single() == "synthetic-cookie-new-login-" + site.Id,
                "relogin updates one current platform record");
        }
        var reopened = new TaskStore(store.DatabasePath);
        Check(PlatformCatalog.Sites.Where(site => CourseRecordingRules.SupportsAutomatic(site.Id))
            .All(site => reopened.LoadPlatformSessionInfo(site.Id).State == "ready"), "all enabled platform records survive reopen independently");
        using (var db = new SqliteConnection("Data Source=" + store.DatabasePath))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM platform_sessions";
            Check(Convert.ToInt32(cmd.ExecuteScalar()) == PlatformCatalog.Sites.Count(site => CourseRecordingRules.SupportsAutomatic(site.Id)), "one session record per enabled platform");
        }
        var legacyPath = Path.Combine(root, "legacy.bin");
        var legacy = new ChaoxingSessionStore(legacyPath);
        legacy.Save([new("legacy-cookie", "synthetic-legacy-login", ".chaoxing.com", "/", true, true, null, "Strict")], now);
        var migrationStore = new TaskStore(Path.Combine(root, "migration.db"));
        var migrated = new ChaoxingSessionStore(legacyPath, migrationStore);
        Check(migrated.Load(now).Single().Value == "synthetic-legacy-login" && migrationStore.LoadPlatformSessionInfo("chaoxing").CanReuse,
            "existing protected archives import into repository");
        Console.WriteLine($"PASS: {checks} all-platform encrypted cookie persistence, replacement, expiry, isolation and legacy migration checks (synthetic only).");
    }

    private sealed record SessionAdapter(Action<string, DateTimeOffset> Save, Func<DateTimeOffset, IReadOnlyList<string>> Load, Action<DateTimeOffset> Invalidate);
    private static SessionAdapter Adapter(string id, string path, TaskStore repository)
    {
        if (id == "chaoxing")
        {
            var session = new ChaoxingSessionStore(path, repository);
            return new((value, now) => session.Save([new("session", value, ".chaoxing.com", "/", true, true, null, "Strict")], now),
                now => session.Load(now).Select(cookie => cookie.Value).ToArray(), session.Invalidate);
        }
        if (id is "yuketang" or "changjiang")
        {
            var session = new RainSessionStore(RainPlatform.Find(id), path, repository);
            return new((value, now) => session.Save([new("sessionid", value, ".yuketang.cn", "/", true, true, null, "Strict")], now),
                now => session.Load(now).Select(cookie => cookie.Value).ToArray(), session.Invalidate);
        }
        if (id == "mooc")
        {
            var session = new MoocSessionStore(path, repository);
            return new((value, now) => session.Save([new("session", value, ".icourse163.org", "/", true, true, null, "Strict")], now),
                now => session.Load(now).Select(cookie => cookie.Value).ToArray(), session.Invalidate);
        }
        if (id == "xuetang")
        {
            var session = new XuetangSessionStore(path, repository);
            return new((value, now) => session.Save([new("sessionid", value, ".xuetangx.com", "/", true, true, null, "Strict")], now),
                now => session.Load(now).Select(cookie => cookie.Value).ToArray(), session.Invalidate);
        }
        var metadata = new MetadataSessionStore(MetadataPlatform.Get(id), path, repository);
        var domain = id switch { "ucampus" => ".unipus.cn", "pta" => ".pintia.cn", _ => ".zhihuishu.com" };
        return new((value, now) => metadata.Save([new("session", value, domain, "/", true, true, null, "Strict")], now),
            now => metadata.Load(now).Select(cookie => cookie.Value).ToArray(), metadata.Invalidate);
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("Session repository check failed: " + label); checks++; }
}
