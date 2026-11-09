using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AccessProbe;

namespace XuetangTestData;

internal static class Metadata
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(8));
    public static XuetangCourse Course(string id = "101", int status = 1, bool stowed = false)
        => new("合成课程 " + id, id, "product" + id, "course" + id, status, stowed, "501");
    public static string Envelope(object data) => JsonSerializer.Serialize(new { success = true, data });
    public static string Login(bool signedIn = true) => Envelope(new { is_login = signedIn });
    public static string Profile(string account = "91001") => Envelope(new { id = account });
    public static string Catalog(IEnumerable<XuetangCourse> courses, int? total = null)
    {
        var rows = courses.Select(course => new
        { name = course.Name, classroom_id = course.ClassroomId, sign = course.ProductSign, course_sign = course.CourseSign,
            status = course.Status, property = 0, sku_id = course.SkuId }).ToArray();
        return total is { } count ? Envelope(new { product_list = rows, count }) : Envelope(new { product_list = rows });
    }
    public static string Leaf(string id, int type = 6, bool show = true, bool locked = false,
        long? deadline = null, long? begin = null) => JsonSerializer.Serialize(new
    { id, name = "合成任务 " + id, leaf_type = type, is_show = show, is_locked = locked,
        score_deadline = deadline ?? Now.AddHours(4).ToUnixTimeMilliseconds(),
        start_time = begin ?? Now.ToUnixTimeMilliseconds(), end_time = 0 });
    public static string Chapter(params string[] leaves)
        => "{\"success\":true,\"data\":{\"course_chapter\":[{\"id\":1,\"name\":\"示例章节\",\"section_leaf_list\":["
            + string.Join(',', leaves) + "]}]}}";
    public static string Tasks() => Chapter(Leaf("201"), Leaf("202", 5), Leaf("203"), Leaf("204"),
        Leaf("205", 6, locked: true, begin: Now.AddHours(1).ToUnixTimeMilliseconds()),
        Leaf("206", 0), Leaf("207", 3), Leaf("208", show: false));
    public static string Schedule() => Envelope(new { leaf_schedules = new Dictionary<string, decimal>
    { ["201"] = 0, ["202"] = 1, ["203"] = 0.5m, ["205"] = 0 } });
    public static string Resets(bool extended = true) => extended
        ? Envelope(new[] { new { leaf_id = "201", reset_deadline = Now.AddHours(8).ToUnixTimeSeconds() } })
        : Envelope(Array.Empty<object>());
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    public static XuetangResponse Ok(string json) => new("ok", 200, json);
    public static XuetangResponse Respond(Uri address)
    {
        var fields = RainAddressPolicy.Query(address)!;
        return Ok(address.AbsolutePath switch
        {
            XuetangPlatform.LoginCheck => Login(), XuetangPlatform.Profile => Profile(),
            XuetangPlatform.Courses => fields.GetValueOrDefault("status") == "1" && fields.GetValueOrDefault("page") == "1"
                ? Catalog([Course()], 1) : Catalog([], 0),
            XuetangPlatform.Product => Envelope(new { sku_id = "501" }),
            XuetangPlatform.Chapter => Tasks(), XuetangPlatform.Schedule => Schedule(),
            XuetangPlatform.ResetDeadlines => Resets(),
            _ => throw new InvalidOperationException("Unexpected fixture request.")
        });
    }
}
