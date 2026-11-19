using AccessProbe;

namespace AccessProbe.Mobile;

// CookieManager returns request pairs without Domain, Path, expiry or HttpOnly attributes.
// Each inferred cookie is limited to the host and path where it was actually observed.
public sealed record MobileCookieRead(string Address, string Header, string? VisibleHeader = null)
{
    public override string ToString() => nameof(MobileCookieRead);
}

public static class MobileCookieSnapshot
{
    public static IReadOnlyList<string> Addresses(string platform) => platform switch
    {
        "chaoxing" => ["https://chaoxing.com/", "https://i.chaoxing.com/", "https://i.mooc.chaoxing.com/", "https://passport2.chaoxing.com/", "https://mooc1.chaoxing.com/", "https://mooc2.chaoxing.com/", "https://mooc2-ans.chaoxing.com/", "https://mooc2-ans.chaoxing.com/mooc2-ans/", "https://i.xuexitong.com/", "https://mooc1-api.chaoxing.com/"],
        "yuketang" => ["https://www.yuketang.cn/", "https://yuketang.cn/", "https://pro.yuketang.cn/"],
        "changjiang" => ["https://changjiang.yuketang.cn/", "https://yuketang.cn/"],
        "mooc" => ["https://www.icourse163.org/", "https://icourse163.org/", "https://www.icourse163.org/web/", "https://www.icourse163.org/home.htm"],
        "xuetang" => ["https://www.xuetangx.com/", "https://xuetangx.com/", "https://www.xuetangx.com/my-courses/current",
            "https://www.xuetangx.com/api/", "https://www.xuetangx.com/api/v1/", "https://www.xuetangx.com/api/v1/u/",
            "https://www.xuetangx.com/api/v1/u/login/", "https://www.xuetangx.com" + XuetangPlatform.LoginCheck,
            "https://www.xuetangx.com/api/v1/u/user/", "https://www.xuetangx.com" + XuetangPlatform.Profile,
            "https://www.xuetangx.com/api/v1/lms/", "https://www.xuetangx.com/api/v1/lms/user/", "https://www.xuetangx.com" + XuetangPlatform.Courses,
            "https://www.xuetangx.com/api/v1/lms/learn/", "https://www.xuetangx.com/api/v1/lms/learn/course/",
            "https://www.xuetangx.com" + XuetangPlatform.Chapter, "https://www.xuetangx.com" + XuetangPlatform.Schedule,
            "https://www.xuetangx.com/api/v1/lms/learn/product/", "https://www.xuetangx.com" + XuetangPlatform.Product,
            "https://www.xuetangx.com" + XuetangPlatform.ResetDeadlines],
        "ucampus" => ["https://unipus.cn/", "https://u.unipus.cn/", "https://u.unipus.cn/user/", "https://u.unipus.cn/user/data/",
            "https://u.unipus.cn/user/data/getToken", "https://u.unipus.cn/user/student", "https://u.unipus.cn/user/student/", "https://u.unipus.cn/user/student/index",
            "https://u.unipus.cn/user/student/homework/", "https://u.unipus.cn/user/student/homework/list",
            "https://sso.unipus.cn/", "https://sso.unipus.cn/sso/", "https://sso.unipus.cn/sso/login"],
        "pta" => ["https://pintia.cn/", "https://passport.pintia.cn/"],
        "uai" => ["https://uai.unipus.cn/", "https://sso.unipus.cn/", "https://sso.unipus.cn/sso/", "https://sso.unipus.cn/sso/login"],
        "zhihuishu" => ["https://zhihuishu.com/", "https://login.zhihuishu.com/", "https://user.zhihuishu.com/", "https://user.zhihuishu.com/zhsuser/",
            "https://user.zhihuishu.com/zhsuser/login/", "https://user.zhihuishu.com/zhsuser/login/isLogin", "https://passport.zhihuishu.com/", "https://passport.zhihuishu.com/login",
            "https://onlinestuh5.zhihuishu.com/", "https://onlineweb.zhihuishu.com/", "https://onlineweb.zhihuishu.com/onlinestuh5", "https://onlineweb.zhihuishu.com/onlinestuh5/",
            "https://mycourse.zhihuishu.com/", "https://hiexam.zhihuishu.com/", "https://hiexam.zhihuishu.com/atHomeworkExam/",
            "https://studentexam-api.zhihuishu.com/", "https://studentexam-api.zhihuishu.com/studentExam/gateway/",
            "https://taurusexam-api.zhihuishu.com/", "https://taurusexam-api.zhihuishu.com/taurusExam/gateway/",
            "https://aiplus.zhihuishu.com/", "https://onlineservice-api.zhihuishu.com/", "https://onlineservice-api.zhihuishu.com/gateway/",
            "https://studyservice-api.zhihuishu.com/", "https://hike.zhihuishu.com/", "https://hikeservice.zhihuishu.com/",
            "https://hike-teaching-api.zhihuishu.com/", "https://hike-teaching-api.zhihuishu.com/gateway/",
            "https://hiexam-server.zhihuishu.com/", "https://hiexam-server.zhihuishu.com/zhsathome/",
            "https://hike-examstu.zhihuishu.com/", "https://hike-examstu.zhihuishu.com/zhsathome/",
            "https://kg-run-student.zhihuishu.com/", "https://kg-run-student.zhihuishu.com/student/gateway/"],
        _ => throw new InvalidDataException("Unsupported browser cookie platform.")
    };

    public static MetadataSessionCookie[] Capture(string platform, IEnumerable<MobileCookieRead> reads)
    {
        var allowed = Addresses(platform).ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, MetadataSessionCookie>(StringComparer.Ordinal);
        var snapshots = reads.ToArray();
        foreach (var read in snapshots.OrderBy(read => new Uri(read.Address).IdnHost, StringComparer.Ordinal)
            .ThenBy(read => new Uri(read.Address).AbsolutePath.Length))
        {
            if (!allowed.Contains(read.Address)) throw new InvalidDataException("Unapproved browser cookie address.");
            var address = new Uri(read.Address);
            var visible = Pairs(read.VisibleHeader).ToHashSet();
            foreach (var pair in Pairs(read.Header).Distinct())
            {
                if (values.Values.Any(cookie => cookie.Name == pair.Name && cookie.Value == pair.Value && cookie.AppliesTo(address))) continue;
                var cookie = new MetadataSessionCookie(pair.Name, pair.Value, address.IdnHost, address.AbsolutePath, true,
                    !visible.Contains(pair), null);
                if (!MobileSessionRules.CookieValid(platform, cookie)) continue;
                if (values.TryGetValue(cookie.Key, out var previous) && previous.Value != cookie.Value)
                    throw new InvalidDataException("Ambiguous browser cookie scope.");
                values[cookie.Key] = cookie;
                if (values.Count > 512) throw new InvalidDataException("Browser cookie limit exceeded.");
            }
        }
        foreach (var read in snapshots.Where(read => read.VisibleHeader is not null))
        {
            var address = new Uri(read.Address); var visible = Pairs(read.VisibleHeader).ToHashSet();
            foreach (var cookie in values.Values.Where(cookie => cookie.AppliesTo(address) && visible.Contains((cookie.Name, cookie.Value))).ToArray())
                values[cookie.Key] = cookie with { HttpOnly = false };
        }
        return values.Values.ToArray();
    }

    public static IReadOnlyList<MetadataSessionCookie> MissingForRestore(MobileSession saved, IReadOnlyList<MetadataSessionCookie> current)
    {
        var now = DateTimeOffset.UtcNow;
        var usable = saved.Cookies.Where(cookie => !cookie.IsExpired(now)).ToArray();
        // A changed official cookie can be a new login. Never combine it with the previous session.
        if (current.Any(cookie => MobileSessionRules.IsAccountCookie(saved.PlatformId, cookie)
            && !usable.Any(previous => previous.Name == cookie.Name && previous.Value == cookie.Value
                && previous.AppliesTo(new Uri("https://" + cookie.Domain.TrimStart('.') + cookie.Path))))) return [];
        return usable.Where(cookie => !current.Any(value => value.Name == cookie.Name
            && value.AppliesTo(new Uri("https://" + cookie.Domain.TrimStart('.') + cookie.Path)))).ToArray();
    }

    public static bool CanApplyResponse(string platform, string browserHeader, IReadOnlyList<MetadataSessionCookie> observed)
    {
        var actual = Pairs(browserHeader).Where(pair => MobileSessionRules.IsAccountCookie(platform,
            new(pair.Name, pair.Value, "unused.invalid", "/", true, true, null))).ToHashSet();
        var expected = observed.Where(cookie => MobileSessionRules.IsAccountCookie(platform, cookie))
            .Select(cookie => (cookie.Name, cookie.Value)).ToHashSet();
        return actual.SetEquals(expected);
    }

    public static IReadOnlyList<(string Name, string Value)> Pairs(string? header)
    {
        if (header is null) return [];
        if (header.Length > 2 * 1024 * 1024) throw new InvalidDataException("Browser cookie header limit exceeded.");
        var values = new List<(string, string)>();
        foreach (var pair in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 1) continue;
            values.Add((pair[..separator].Trim(), pair[(separator + 1)..].Trim()));
        }
        return values;
    }
}
