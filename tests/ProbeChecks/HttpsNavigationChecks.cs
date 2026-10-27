using AccessProbe;

public static class HttpsNavigationChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            count++;
            if (!condition) throw new Exception($"HTTPS navigation check {count} failed: {name}");
        }
        const string http = "http://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&enc=SYNTHETIC%2BONLY%26VALUE#section";
        const string https = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&enc=SYNTHETIC%2BONLY%26VALUE#section";
        Check(ProbePolicy.TryGetHttpsNavigation(http, out var upgraded) && upgraded == https, "path and opaque query remain unchanged");
        Check(!ProbePolicy.IsAllowedNavigation(http) && ProbePolicy.IsAllowedNavigation(upgraded), "HTTP itself is still blocked");
        Check(ProbePolicy.TryGetHttpsNavigation(http.Replace(".com/", ".com:80/", StringComparison.Ordinal), out upgraded) && upgraded == https,
            "explicit default HTTP port becomes default HTTPS port");
        Check(ProbePolicy.TryGetHttpsNavigation("HTTP://i.chaoxing.com?enc=SYNTHETIC%2fVALUE", out upgraded)
            && upgraded == "https://i.chaoxing.com?enc=SYNTHETIC%2fVALUE", "case and query-only path are preserved");
        Check(ProbePolicy.TryGetHttpsNavigation("http://i.xuexitong.com/", out upgraded) && upgraded == "https://i.xuexitong.com/",
            "existing official domain policy is reused");
        foreach (var rejected in new[]
        {
            "https://i.chaoxing.com/", "http://school.example/", "http://chaoxing.com.evil.example/",
            "http://evilchaoxing.com/", "http://user@i.chaoxing.com/", "http://user:pass@i.chaoxing.com/",
            "http://i.chaoxing.com:9443/", "http://i.chaoxing.com:443/", "http://i.chaoxing.com\\@evil.example/",
            "http://i.chaoxing.com/path with spaces", "http://i.chaoxing.com/\n", "http://i.chaoxing.com/\" onclick=\"run()",
            "javascript:location.href='http://i.chaoxing.com/'", "//i.chaoxing.com/", "not a url"
        })
            Check(!ProbePolicy.TryGetHttpsNavigation(rejected, out upgraded) && upgraded.Length == 0, "unsupported target is not upgraded");

        var chain = new HttpsUpgradeChain();
        chain.NavigationStarting(http, false);
        Check(chain.TrySchedule(https), "first upgrade is accepted");
        chain.NavigationStarting(https, false);
        chain.NavigationStarting(http, true);
        Check(!chain.TrySchedule(https), "server downgrade does not loop indefinitely");
        chain.NavigationStarting(http, false);
        Check(chain.TrySchedule(https), "manual retry starts a new chain");
        chain.Reset();
        Check(!chain.TrySchedule(http) && !chain.TrySchedule("https://example.invalid/") && !chain.TrySchedule("about:blank"), "guard only accepts allowed HTTPS targets");
        for (var index = 0; index < HttpsUpgradeChain.MaximumUpgrades; index++)
        {
            var target = "https://i.chaoxing.com/__synthetic__/" + index;
            Check(chain.TrySchedule(target), "finite distinct upgrades are allowed");
            chain.NavigationStarting(target, false);
            chain.NavigationStarting("http://i.chaoxing.com/__synthetic__/next", true);
        }
        Check(!chain.TrySchedule("https://i.chaoxing.com/__synthetic__/overflow"), "chain length is bounded");
        Console.WriteLine($"PASS: {count} HTTPS navigation upgrade and redirect-loop checks; synthetic URLs only.");
    }
}
