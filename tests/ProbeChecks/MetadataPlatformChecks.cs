using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessProbe;
using PlatformMetadataTestData;

internal static partial class MetadataPlatformChecks
{
    private static int checks;
    private static MetadataPlatform Zhs => MetadataPlatform.Zhihuishu;
    public static async Task RunAsync()
    {
        Crypto(); Parser(); LoginNavigation(); ListParser(); SmartParser(); SmartLoginDiagnostics(); Session(); SessionAuxiliaryCookies(); await StorageAndSync(); await ListSync(); await SmartSync(); await Http(); await SmartHttp(); await UcampusChecks(); await UcampusUnitChecksAsync(); await LoginFailureChecksAsync(); await CourseSessionRecheckAsync();
        Console.WriteLine($"PASS: {checks} Zhihuishu shared/flip/smart lists and AI catalogs, legacy Unipus homework and exams, login diagnostics, FiF disabled, encryption, account isolation, paging, manual preservation and read-only HTTP checks (synthetic only).");
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception("Metadata platform check: " + name); checks++; }
    private static void Reject(Action action, string name)
    { try { action(); } catch (Exception error) when (error is CryptographicException or FormatException or InvalidDataException or TaskValidationException or CourseRecordingChangedException) { checks++; return; } throw new Exception("Expected rejection: " + name); }
    private static byte[] Block(string json, int padding = -1)
    {
        var bytes = Encoding.UTF8.GetBytes(json); var block = new byte[128]; block[1] = 1;
        var count = padding < 0 ? 125 - bytes.Length : padding; Array.Fill(block, (byte)255, 2, count); bytes.CopyTo(block, 3 + count); return block;
    }
    private static void Crypto()
    {
        var block = Block("{\"module\":13,\"cKey\":\"0123456789abcdef\"}");
        Check(ZhihuishuMetadataCrypto.KeyFromBlock(block).SequenceEqual(Fixtures.Key), "public type-1 block restores exact UTF8 key");
        foreach (var json in new[] { "{}", "[]", "{\"module\":3,\"cKey\":\"0123456789abcdef\"}", "{\"module\":\"13\",\"cKey\":\"0123456789abcdef\"}",
            "{\"module\":13,\"cKey\":\"short\"}", "{\"module\":13,\"cKey\":null}", "{\"module\":13,\"cKey\":\"汉字长度不是十六字节\"}" })
            Reject(() => ZhihuishuMetadataCrypto.KeyFromBlock(Block(json)), "unknown key module, value or length");
        for (var position = 0; position < 10; position++)
        {
            var bad = block.ToArray(); bad[position] = position == 0 ? (byte)1 : (byte)0;
            Reject(() => ZhihuishuMetadataCrypto.KeyFromBlock(bad), "short padding or invalid RSA type");
        }
        foreach (var size in new[] { 0, 127, 129 }) Reject(() => ZhihuishuMetadataCrypto.KeyFromBlock(new byte[size]), "RSA block size");
        foreach (var payload in new[] { "invalid base64", "", Convert.ToBase64String(new byte[127]), Convert.ToBase64String(Enumerable.Repeat((byte)255, 128).ToArray()) })
            Reject(() => ZhihuishuMetadataCrypto.RecoverKey(payload), "invalid signed payload");
        Check(Convert.FromBase64String(ZhihuishuMetadataCrypto.HandshakeParameter()).Length == 128, "official RSA module request is 1024 bit");
        Check(Convert.FromBase64String(ZhihuishuMetadataCrypto.HandshakeParameter(3)).Length == 128, "shared list uses separate official RSA module 3");
        Check(ZhihuishuMetadataCrypto.KeyFromBlock(Block("{\"module\":3,\"cKey\":\"0123456789abcdef\"}"), 3).SequenceEqual(Fixtures.Key), "shared module restores key");
        Reject(() => ZhihuishuMetadataCrypto.KeyFromBlock(block, 3), "homepage key cannot satisfy shared module");
        Reject(() => ZhihuishuMetadataCrypto.HandshakeParameter(11), "unverified module unavailable");
        // Expected ciphertext is generated independently with Python cryptography, AES-CBC and PKCS7.
        Check(ZhihuishuMetadataCrypto.Encrypt("{\"pageNum\":1,\"pageSize\":20}", Fixtures.Key) == "48ustLBtVRRg6wYdtUHBecsjUji54JNZQcrDiGnlYoQ=", "official AES-CBC fixed vector");
        Reject(() => ZhihuishuMetadataCrypto.Encrypt("{}", new byte[15]), "bad AES key size");
    }
    private static void Parser()
    {
        foreach (var platform in MetadataPlatform.All)
        {
            Check(platform.IsLoginNavigation(platform.Login.AbsoluteUri), "official login allowed");
            foreach (var suffix in new[] { ".evil.example", "@evil.example" }) Check(!platform.IsLoginNavigation("https://" + platform.Host + suffix), "spoofed login rejected");
            Check(!platform.IsLoginNavigation("http://" + platform.Host + "/"), "HTTP login rejected");
            Check(MetadataPlatformParser.EnvelopeOutcome(platform, "profile", "<script>location='login'</script>")
                == (platform.Id == "fif" ? "adaptation_disabled" : "login_required"), "HTML login cannot be empty success");
            foreach (var json in new[] { "{}", "[]", "null", "{\"success\":true}" }) Check(MetadataPlatformParser.EnvelopeOutcome(platform, "profile", json) != "ok", "unknown envelope rejected");
        }
        Check(MetadataPlatformParser.ZhihuishuAccount([Fixtures.Cookie()]) == Fixtures.Account, "official CASLOGC uuid bound");
        Check(MetadataPlatformParser.ZhihuishuAccount([Fixtures.Cookie(), Fixtures.Cookie("other")]) is null, "ambiguous account rejected");
        Check(MetadataPlatformParser.ZhihuishuAccount([Fixtures.Cookie() with { Value = "invalid" }]) is null, "unknown account cookie rejected");
        Check(MetadataPlatformParser.ZhihuishuProfile(Fixtures.Profile(), Fixtures.Account) == "ok", "profile and cookie agree");
        Check(MetadataPlatformParser.ZhihuishuProfile(Fixtures.Profile("other"), Fixtures.Account) == "account_mismatch", "cross-account profile refused");
        Check(MetadataPlatformParser.ZhihuishuProfile("{\"code\":200,\"result\":[]}", Fixtures.Account) != "ok", "invalid profile payload");
        var catalog = MetadataPlatformParser.ZhihuishuCatalog(Fixtures.Catalog(), 1); var courses = catalog.Courses.ToDictionary(course => course.Id);
        Check(catalog.Outcome == "ok" && catalog.Courses.Count == 1, "official flip course page");
        Check(MetadataPlatformParser.ZhihuishuCatalog(Fixtures.Catalog(1, 0, []), 1) is { Outcome: "ok", Courses.Count: 0 }, "explicit empty course list");
        Check(MetadataPlatformParser.ZhihuishuCatalog(Fixtures.Catalog(1, 1, [Fixtures.Course(), Fixtures.Course()]), 1).DuplicateRows == 1, "duplicate course row");
        Check(MetadataPlatformParser.ZhihuishuCatalog(Fixtures.Catalog(1, 1, [Fixtures.Course(), Fixtures.Course(name: "冲突课程")]), 1).Courses.Count == 0, "conflicting course not used");
        foreach (var json in new[] { "{}", "{\"status\":200,\"rt\":{}}", Fixtures.Catalog(2), Fixtures.Catalog(1, 0), Fixtures.Catalog(1, -1) })
            Check(MetadataPlatformParser.ZhihuishuCatalog(json, 1).Outcome != "ok", "unknown or inconsistent catalog");
        var tasks = MetadataPlatformParser.ZhihuishuReminders(Fixtures.DefaultReminders(), Fixtures.Account, courses, Fixtures.Now);
        Check(tasks.Tasks.Count == 3 && tasks.Tasks[0].State == TaskState.Returned && tasks.Tasks[1].State == TaskState.Pending && tasks.Tasks[2].State is null, "returned, in-progress and unknown separated");
        Check(tasks.Tasks.All(task => task.State != TaskState.Completed), "recent reminders never infer submitted state");
        Check(tasks.Tasks.All(task => task.CourseName == "合成翻转课"), "course belongs to verified catalog");
        Check(tasks.Tasks[0].DeadlineUtc == Fixtures.Now.AddHours(8).ToUniversalTime(), "official milliseconds converted exactly");
        for (var type = 1; type <= 2; type++) for (var paper = 0; paper <= 1; paper++) for (var status = 1; status <= 4; status++)
        {
            var task = MetadataPlatformParser.ZhihuishuReminders(Fixtures.Reminders(Fixtures.Reminder(type: type, paper: paper, status: status)), Fixtures.Account, courses, Fixtures.Now).Tasks.Single();
            var supported = status != 1 && (type == 1 || status is 2 or 4);
            Check((task.Link is not null) == supported && (task.Link is null || MetadataTaskNavigation.Matches(task)), "official specific task routes");
        }
        foreach (var bad in new[] { Fixtures.Reminder(type: 3), Fixtures.Reminder(course: "999"), Fixtures.Reminder(id: "bad/path"), Fixtures.Reminder(title: "") })
            Check(MetadataPlatformParser.ZhihuishuReminders(Fixtures.Reminders(bad), Fixtures.Account, courses, Fixtures.Now).UnsupportedRows == 1, "unverified task excluded");
        foreach (var time in new long[] { 0, -1, 1700000000, long.MaxValue })
            Check(MetadataPlatformParser.ZhihuishuReminders(Fixtures.Reminders(Fixtures.Reminder(end: time)), Fixtures.Account, courses, Fixtures.Now).Tasks.Single().DeadlineUtc is null, "bad deadline needs review");
        Check(MetadataPlatformParser.ZhihuishuReminders(Fixtures.Reminders(Fixtures.Reminder(start: Fixtures.Now.AddDays(3).ToUnixTimeMilliseconds())), Fixtures.Account, courses, Fixtures.Now).Tasks.Single().BeginUtc is null, "inverted time interval");
        Check(MetadataPlatformParser.ZhihuishuReminders(Fixtures.Reminders(Fixtures.Reminder(type: 2, examType: 2)), Fixtures.Account, courses, Fixtures.Now).Tasks.Single().Link is null, "offline exam has no online action");
        Check(MetadataPlatformParser.ZhihuishuReminders(Fixtures.Reminders(Fixtures.Reminder(), Fixtures.Reminder(title: "冲突任务")), Fixtures.Account, courses, Fixtures.Now).Tasks.Count == 0, "conflicting task rejected");
        Check(MetadataPlatformParser.ZhihuishuReminders("{\"status\":200,\"rt\":{}}", Fixtures.Account, courses, Fixtures.Now).Outcome == "unrecognized_tasks", "unknown task shape cannot claim empty");
        Check(MetadataPlatformParser.SharedReminderCount(Fixtures.Shared(new { courseId = "101" })) == 1, "shared reminder is count only");
        Check(MetadataPlatformParser.Discovery(MetadataPlatform.Ucampus, Fixtures.Ucampus()) is { Applications: 0, Outcome: "unrecognized_catalog" }, "unrecognized legacy HTML is not empty course discovery");
        Check(MetadataPlatformParser.Discovery(MetadataPlatform.Fif, "{\"status\":\"SUCCESS\"}") is { Applications: 0, Outcome: "adaptation_disabled" }, "FiF adaptation stays disabled");
        Check(MetadataPlatform.Ucampus.Home.AbsoluteUri == "https://u.unipus.cn/index.html/"
            && !MetadataPlatform.Ucampus.IsCookieDomain("uai.unipus.cn"), "confirmed legacy Ucampus version");
        Check(!CourseRecordingRules.SupportsAutomatic("fif") && MetadataPlatform.Enabled.All(platform => platform.Id != "fif"), "FiF has no automatic course picker");
        Check(!JsonSerializer.Serialize(tasks).Contains("synthetic-answer-not-read", StringComparison.Ordinal) && !JsonSerializer.Serialize(tasks).Contains(Fixtures.Account, StringComparison.Ordinal), "metadata excludes answers and identity");
    }
    private static void Session()
    {
        var root = Path.Combine(Path.GetTempPath(), "metadata-session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "zhs.bin"); var archive = new MetadataSessionStore(Zhs, path);
        archive.Save([Fixtures.Cookie()], Fixtures.Now, "synthetic-token", new string('A', 64));
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("synthetic-token", StringComparison.Ordinal), "session token DPAPI protected");
        var restored = archive.Load(Fixtures.Now); Check(restored.Count == 1 && archive.Token == "synthetic-token", "encrypted session restored");
        Check(new MetadataSessionStore(MetadataPlatform.Fif, path).Load(Fixtures.Now).Count == 0, "platform-specific archive entropy");
        Check(archive.Load(Fixtures.Now.AddDays(31)).Count == 0, "archive age limited");
        Check(MetadataSessionStore.MissingForRestore(Zhs, restored, [Fixtures.Cookie("new-login")]).Count == 0, "new login wins over old archive");
        Check(MetadataSessionStore.MissingForRestore(Zhs, restored, [Fixtures.Cookie() with { Name = "new-auth", Value = "new-login" }]).Count == 0, "different auth cookie prevents mixed session");
        Check(MetadataSessionStore.MissingForRestore(Zhs, restored, []).Count == 1, "empty profile can recover archive");
        foreach (var cookie in new[] { Fixtures.Cookie() with { Domain = ".evil.example" }, Fixtures.Cookie() with { Name = "bad;name" }, Fixtures.Cookie() with { Value = "bad\r\nvalue" } })
            Check(!MetadataSessionStore.IsValid(Zhs, cookie), "invalid cookie scope or value");
        var renewed = MetadataResponseCookies.Parse(Zhs, new("https://onlineservice-api.zhihuishu.com/a/b"),
            ["CASLOGC=renewed; Domain=.zhihuishu.com; Path=/; Secure; HttpOnly", "foreign=secret; Domain=.fifedu.com", "expired=x; Path=/; Max-Age=0"], Fixtures.Now);
        Check(renewed.Count == 2 && renewed.Any(cookie => cookie.Name == "expired" && cookie.IsExpired(Fixtures.Now)), "response cookie renewal and deletion scope");
        Reject(() => archive.Save(restored, Fixtures.Now, "bad\r\ntoken", new string('A', 64)), "token header injection");
        archive.Clear(); Check(!File.Exists(path), "explicit session clear removes archive"); Directory.Delete(root, true);
    }
    private static async Task StorageAndSync()
    {
        var root = Path.Combine(Path.GetTempPath(), "metadata-store-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new TaskStore(Path.Combine(root, "tasks.db")); var reader = new Fixtures.Reader();
        var pending = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(pending.Courses > 0 && pending.Tasks == 0 && reader.Requests.All(request => request.Operation is "profile" or "courses" or "shared-courses" or "smart-courses" or "agent-courses"),
            "unconfirmed Zhihuishu catalog excludes all course task requests");
        var initial = store.LoadCourseRecording(Zhs.Id);
        store.SaveCourseRecording(Zhs.Id, true, [], true, initial.Revision, Fixtures.Now);
        reader.Requests.Clear();
        var summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(summary.Tasks == 3 && summary.Added == 3 && summary.CourseListCovered && summary.ReminderListCovered, "task metadata imported to unified store");
        var original = store.LoadTasks().Single(task => task.PlatformTitle == "合成作业");
        Check(original.Source == TaskSource.Zhihuishu && original.State == TaskState.Returned && original.Link is not null, "source state and official link persisted");
        Check(!File.ReadAllText(store.DatabasePath).Contains("synthetic-answer-not-read", StringComparison.Ordinal), "answer text never persisted");
        store.RecordMetadataSyncResult(summary, "partial", Fixtures.Now);
        Check(store.LoadMetadataSettings(Zhs.Id).LastSummary!.Tasks == 3 && !store.LoadMetadataSettings("fif").Connected, "platform settings isolated");
        var edited = store.Edit(original.Id, original.Revision, new("人工标题", Zhs.Id, "人工课程", DeadlineKind.Known, null,
            Fixtures.Now.AddDays(2), original.Link, null, "人工成绩", "人工备注", 2), Fixtures.Now);
        store.SetState(edited.Id, edited.Revision, TaskState.Completed, Fixtures.Now);
        summary = await new MetadataSynchronizer(Zhs, store, reader).RunAsync(); var latest = store.Get(original.Id)!;
        Check(summary.Added == 0 && store.LoadTasks().Count == 3, "resync deduplicates exact task key");
        Check(latest.Title == "人工标题" && latest.CourseName == "人工课程" && latest.State == TaskState.Completed
            && latest.DeadlineUtc == Fixtures.Now.AddDays(2) && latest.ScoreText == "人工成绩", "manual fields retain priority");
        reader.Response = request => request.Operation == "profile" ? new("login_required", 401) : new("ok", 200, "{}");
        Check((await new MetadataSynchronizer(Zhs, store, reader).RunAsync()).LoginRequired && store.LoadTasks().Count == 3, "login expiry preserves tasks");
        reader.Response = request => request.Operation switch
        {
            "profile" => new("ok", 200, Fixtures.Profile()) { AccountId = Fixtures.Account },
            "courses" => new("ok", 200, Fixtures.Catalog(request.Page, 2)),
            "reminders" => new("ok", 200, Fixtures.Reminders()),
            _ => null
        };
        var repeated = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(!repeated.CourseListCovered && repeated.Problems.Any(problem => problem.Code == "ZHS005"), "repeat-page coverage refused");
        Check(store.LoadTasks().Count == 3 && store.Get(original.Id)!.State == TaskState.Completed, "disappearing reminder cannot mutate previous task");
        reader.Response = null; reader.AccountId = "synthetic-account-02"; await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(store.LoadTasks().Count == 6 && store.LoadTasks().Select(task => task.SourceKey).Distinct().Count() == 6, "same remote ids isolated by account");
        var recording = store.LoadCourseRecording(Zhs.Id); store.SaveCourseRecording(Zhs.Id, false, [], true, recording.Revision, Fixtures.Now);
        var deselected = await new MetadataSynchronizer(Zhs, store, reader).RunAsync();
        Check(deselected.Tasks == 0 && store.LoadTasks().Count == 6 && store.LoadRecordedTasks().Count == 0, "deselected courses stop import and retain history");
        var revision = store.LoadCourseRecording(Zhs.Id).Revision;
        Reject(() => store.ImportMetadataTasks(Zhs.Id, [], Fixtures.Now, recordingRevision: revision - 1), "stale course revision");
        var nav = new TaskNavigationService(store, null, _ => checks++); await nav.OpenAsync(original.Id, CancellationToken.None);
        Check(MetadataTaskNavigation.Key(Zhs.Id, original.Link) is not null, "saved exact task opens without background answering read");
        foreach (var platform in new[] { MetadataPlatform.Ucampus, MetadataPlatform.Fif })
        {
            var discoveryReader = new Fixtures.Reader { Response = request => new("unrecognized_response", 200, Fixtures.Ucampus()) };
            var discovery = await new MetadataSynchronizer(platform, store, discoveryReader).RunAsync();
            Check(discovery.Tasks == 0 && !discovery.CourseListCovered && discovery.ApplicationsDiscovered == 0
                && discovery.Problems.Any(problem => problem.Code == platform.CodePrefix + (platform.Id == "fif" ? "006" : "003")), "unsupported scope never claims task coverage");
            Check(discoveryReader.Requests.Count == (platform.Id == "fif" ? 0 : 1), "FiF disabled and unknown legacy account stop before task reads");
        }
        var cts = new CancellationTokenSource(); cts.Cancel();
        try { await new MetadataSynchronizer(Zhs, store, reader).RunAsync(cancellation: cts.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { checks++; }
        Check(store.LoadTasks().Count == 6, "cancelled run preserves database");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private static async Task Http()
    {
        var observed = new[] { Fixtures.Cookie(), Fixtures.Cookie() with { Name = "other", Domain = ".evil.example" },
            Fixtures.Cookie() with { Name = "jt-cas", Value = "synthetic-flip%2Btoken", Domain = "hiexam.zhihuishu.com", Path = "/atHomeworkExam/" } };
        var sharedKey = Encoding.UTF8.GetBytes("fedcba9876543210");
        var requests = 0; var writes = 0;
        var fixture = new Fixtures.Reader { IncludeCourseLists = true };
        var flip = MetadataPlatformParser.ZhihuishuCatalog(Fixtures.Catalog(), 1).Courses.Single(); var shared = Fixtures.SharedCourseModel();
        using var handler = new Handler(async (request, token) =>
        {
            requests++;
            var uri = request.RequestUri!;
            Check(uri.Scheme == "https" && uri.IsDefaultPort && uri.IdnHost != "hiexam.zhihuishu.com", "reader never visits answer documents");
            Check(request.Headers.GetValues("Cookie").Single().Contains("CASLOGC=", StringComparison.Ordinal)
                && !request.Headers.GetValues("Cookie").Single().Contains("other=", StringComparison.Ordinal), "host-scoped outgoing cookies");
            var homepage = uri.IdnHost is "onlineservice-api.zhihuishu.com" or "hike-teaching-api.zhihuishu.com";
            var flipRequest = uri.IdnHost is "hiexam-server.zhihuishu.com" or "hike-examstu.zhihuishu.com" && uri.AbsolutePath.EndsWith("/List", StringComparison.Ordinal);
            Check(homepage || flipRequest ? request.Headers.GetValues("Authorization").Single() == (homepage ? "synthetic-token" : "synthetic-flip+token")
                : !request.Headers.Contains("Authorization"), "homepage localStorage and exam cookie credentials stay in correct origins");
            var form = request.Method == HttpMethod.Post ? Form(await request.Content!.ReadAsStringAsync(token)) : Form(uri.Query);
            MetadataRequest metadata;
            if (uri.AbsolutePath.EndsWith("getCourseList", StringComparison.Ordinal) || uri.AbsolutePath.EndsWith("queryShareCourseInfo", StringComparison.Ordinal))
            {
                using var decoded = Decode(form["secretStr"], Fixtures.Key); var parameters = decoded.RootElement;
                var shareCatalog = uri.AbsolutePath.EndsWith("queryShareCourseInfo", StringComparison.Ordinal);
                Check(form.Count == 2 && form.ContainsKey("date") && parameters.GetProperty("pageSize").GetInt32() == (shareCatalog ? 5 : 20), "homepage module thirteen and official page sizes");
                Check(request.Method == (shareCatalog ? HttpMethod.Post : HttpMethod.Get), "shared catalog POST and flip catalog GET");
                metadata = new(shareCatalog ? "shared-courses" : "courses", parameters.GetProperty(shareCatalog ? "pageNo" : "pageNum").GetInt32(), shareCatalog ? parameters.GetProperty("status").GetInt32() : 0);
            }
            else if (uri.IdnHost is "studentexam-api.zhihuishu.com" or "taurusexam-api.zhihuishu.com")
            {
                Check(request.Method == HttpMethod.Post && uri.Query.Length == 0 && form.Count == 1 && form.ContainsKey("secretStr")
                    && request.Content!.Headers.ContentType!.MediaType == "application/x-www-form-urlencoded", "shared module-three metadata only POST form without date or ev");
                Check(request.Headers.Referrer!.AbsoluteUri == "https://onlineexamh5new.zhihuishu.com/stuExamWeb.html", "official shared exam referrer");
                using var decoded = Decode(form["secretStr"], sharedKey); var parameters = decoded.RootElement;
                var operation = uri.AbsolutePath.EndsWith("getLoginUserInfo", StringComparison.Ordinal) ? uri.IdnHost.StartsWith("taurus", StringComparison.Ordinal) ? "shared-exam-profile" : "shared-profile"
                    : uri.AbsolutePath.EndsWith("getLoginSchoolInfo", StringComparison.Ordinal) ? "shared-school"
                    : uri.AbsolutePath.EndsWith("getStudentHomework", StringComparison.Ordinal) ? "shared-homework"
                    : uri.AbsolutePath.EndsWith("getExamType", StringComparison.Ordinal) ? "shared-exam-type"
                    : uri.AbsolutePath.EndsWith("getStudentFinalExam", StringComparison.Ordinal) ? "shared-exams"
                    : uri.AbsolutePath.EndsWith("getStudentRoomExam", StringComparison.Ordinal) ? "shared-room-exam"
                    : uri.AbsolutePath.EndsWith("getStudentOfflineExam", StringComparison.Ordinal) ? "shared-offline-exam"
                    : uri.AbsolutePath.EndsWith("hasMakeupExam", StringComparison.Ordinal) ? "shared-makeup" : throw new Exception("Unexpected shared read path");
                var isTaurus = operation is "shared-exam-profile" or "shared-exam-type" or "shared-exams" or "shared-room-exam" or "shared-offline-exam" or "shared-makeup";
                Check(uri.IdnHost == (isTaurus ? "taurusexam-api.zhihuishu.com" : "studentexam-api.zhihuishu.com"), "official distinct homework and final exam origins");
                var bucket = operation is "shared-homework" or "shared-exams" or "shared-room-exam" or "shared-offline-exam" ? parameters.GetProperty("flag").GetInt32() : 0;
                if (operation is "shared-homework" or "shared-exams" or "shared-exam-type" or "shared-room-exam" or "shared-offline-exam" or "shared-makeup")
                    Check(parameters.GetProperty("recruitId").GetString() == "501", "shared requests use actual recruit id rather than hashed storage id");
                if (operation is "shared-homework" or "shared-exams" or "shared-room-exam" or "shared-offline-exam") Check(parameters.GetProperty("courseId").GetString() == "101" && bucket is 1 or 2, "shared official course and submission flag");
                if (operation == "shared-homework") Check(parameters.GetProperty("pageSize").GetInt32() == 100 && parameters.GetProperty("pageNum").GetInt32() == 0, "shared verified first batch only");
                if (operation == "shared-exam-type") Check(parameters.GetProperty("schoolId").GetString() == Fixtures.SchoolId, "shared online exam discriminator school parameter");
                if (operation is "shared-room-exam" or "shared-offline-exam" or "shared-exams") Check(parameters.EnumerateObject().Count() == 3, "exam list uses only official course recruit and flag parameters");
                if (operation == "shared-makeup") Check(parameters.EnumerateObject().Count() == 1 && !parameters.TryGetProperty("flag", out _), "makeup availability only reads recruit id");
                metadata = new(operation, Bucket: bucket)
                { Course = operation is "shared-homework" or "shared-exams" or "shared-exam-type" or "shared-room-exam" or "shared-offline-exam" or "shared-makeup" ? shared : null,
                    SchoolId = operation == "shared-exam-type" ? Fixtures.SchoolId : "" };
            }
            else
            {
                Check(request.Method == HttpMethod.Get && request.Content is null, "unencrypted metadata endpoints only GET");
                var operation = uri.AbsolutePath.EndsWith("getLoginUserInfo", StringComparison.Ordinal) ? "profile"
                    : uri.AbsolutePath.EndsWith("findImportantReminderList", StringComparison.Ordinal) ? "reminders"
                    : uri.AbsolutePath == "/zhsathome/homeworkUser/List" ? "flip-homework"
                    : uri.AbsolutePath == "/zhsathome/examStu/List" ? "flip-exam" : throw new Exception("Unexpected GET read path");
                if (operation is "flip-homework" or "flip-exam")
                {
                    Check(form["courseId"] == "101" && form["key"] == "" && form["pageIndex"] == "1" && form["pageSize"] == "20" && form["completeTab"] == "0", "flip full-list metadata parameters");
                    Check(uri.IdnHost == (operation == "flip-homework" ? "hiexam-server.zhihuishu.com" : "hike-examstu.zhihuishu.com"), "flip distinct homework and exam origins");
                    if (operation == "flip-exam") Check(form["uuid"] == Fixtures.Account, "exam uuid verified from CASLOGC");
                }
                metadata = new(operation) { Course = operation is "flip-homework" or "flip-exam" ? flip : null };
            }
            await Task.Yield(); token.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture.DefaultResponse(metadata).Json), RequestMessage = request };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "SESSION_TEST=renewed; Domain=.zhihuishu.com; Path=/; Secure"); return response;
        });
        using var reader = new MetadataHttpReader(Zhs, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(observed), handler,
            (_, _, renewed, _) => { writes += renewed.Count; return Task.CompletedTask; }, _ => Task.FromResult<string?>("synthetic-token"), Fixtures.Key, sharedKey);
        var profile = await reader.ReadAsync(new("profile"), CancellationToken.None);
        Check(profile.Outcome == "ok" && profile.AccountId == Fixtures.Account, "transport profile and account binding");
        Check((await reader.ReadAsync(new("courses"), CancellationToken.None)).Outcome == "ok", "official encrypted catalog request");
        Check((await reader.ReadAsync(new("reminders"), CancellationToken.None)).Outcome == "ok" && writes == 3, "metadata and renewed cookies acknowledged");
        foreach (var request in new MetadataRequest[] { new("shared-courses"), new("shared-courses", Bucket: 1),
            new("flip-homework") { Course = flip }, new("flip-exam") { Course = flip }, new("shared-profile"), new("shared-exam-profile"), new("shared-school"),
            new("shared-homework", Bucket: 1) { Course = shared }, new("shared-homework", Bucket: 2) { Course = shared },
            new("shared-exam-type") { Course = shared, SchoolId = Fixtures.SchoolId }, new("shared-exams", Bucket: 1) { Course = shared }, new("shared-exams", Bucket: 2) { Course = shared },
            new("shared-room-exam", Bucket: 1) { Course = shared }, new("shared-room-exam", Bucket: 2) { Course = shared },
            new("shared-offline-exam", Bucket: 1) { Course = shared }, new("shared-offline-exam", Bucket: 2) { Course = shared }, new("shared-makeup") { Course = shared } })
            Check((await reader.ReadAsync(request, CancellationToken.None)).Outcome == "ok", "confirmed metadata operation succeeds with isolated transport");
        Check(writes == requests, "all metadata response cookies processed without exposing their values");
        var before = requests;
        foreach (var operation in new[] { "answer", "submit", "paper", "examDetail", "unknown" })
            Check((await reader.ReadAsync(new(operation), CancellationToken.None)).Outcome == "unsupported_request", "answering operation cannot be sent");
        Check(requests == before, "unsupported requests never reach HTTP");
        foreach (var size in new[] { 0, -1, 101 }) Check(!MetadataHttpReader.IsSupported(Zhs.Id, new("courses", size)), "page limit before network");
        foreach (var request in new MetadataRequest[] { new("shared-homework", 2, 1) { Course = shared }, new("shared-homework") { Course = shared },
            new("flip-homework") { Course = shared }, new("shared-exam-type") { Course = shared }, new("shared-homework", Bucket: 1) { Course = shared with { RecruitId = "502" } },
            new("shared-room-exam", 2, 1) { Course = shared }, new("shared-offline-exam", Bucket: 1) { Course = flip },
            new("shared-room-exam") { Course = shared }, new("shared-offline-exam", Bucket: 3) { Course = shared },
            new("shared-makeup", Bucket: 1) { Course = shared }, new("shared-makeup") { Course = shared, SchoolId = Fixtures.SchoolId },
            new("shared-makeup") { Course = shared with { RecruitId = "502" } } })
            Check(!MetadataHttpReader.IsSupported(Zhs.Id, request), "unknown course shape bucket or shared page refused before network");
        foreach (var body in new[] { "<script>location='login'</script>", "{}", "{\"status\":200,\"rt\":null}" })
        {
            using var unknown = new MetadataHttpReader(Zhs, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(observed),
                new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request })),
                testMetadataKey: Fixtures.Key);
            var value = await unknown.ReadAsync(new("courses"), CancellationToken.None);
            Check(body.Contains("login", StringComparison.Ordinal) ? value.Outcome == "login_required" : value.Outcome != "ok"
                || MetadataPlatformParser.ZhihuishuCatalog(value.Json, 1).Outcome != "ok", "HTML and unknown response not empty success");
        }
        var disabledRequests = 0;
        using (var disabled = new MetadataHttpReader(MetadataPlatform.Fif, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([]),
            new Handler((_, _) => { disabledRequests++; throw new Exception("FiF must not reach network"); })))
        {
            Check((await disabled.ReadAsync(new("profile"), CancellationToken.None)).Outcome == "unsupported_request" && disabledRequests == 0, "FiF transport disabled with zero network requests");
        }
        using (var legacy = new MetadataHttpReader(MetadataPlatform.Ucampus, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>([UcampusFixtures.Cookie()]),
            new Handler((request, _) =>
            {
                Check(request.RequestUri!.GetLeftPart(UriPartial.Path) == "https://u.unipus.cn/user/data/getToken" && request.Method == HttpMethod.Get && !request.Headers.Contains("Authorization"), "legacy Ucampus account endpoint without AI token contract");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UcampusFixtures.Profile()), RequestMessage = request });
            })))
            Check((await legacy.ReadAsync(new("profile"), CancellationToken.None)).Outcome == "ok", "legacy account binds getToken openId to official cookie");
        using (var expired = new MetadataHttpReader(Zhs, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(observed), new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixtures.FlipList()), RequestMessage = request };
            response.Headers.TryAddWithoutValidation("loginstatus", "timeout"); return Task.FromResult(response);
        }), testMetadataKey: Fixtures.Key))
            Check((await expired.ReadAsync(new("flip-homework") { Course = flip }, CancellationToken.None)).Outcome == "login_required", "official flip login timeout header invalidates session");
        var ambiguous = observed.Concat(new[] { observed[2] with { Domain = ".zhihuishu.com", Value = "different" } }).ToArray();
        using (var refused = new MetadataHttpReader(Zhs, (_, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(ambiguous),
            new Handler((_, _) => throw new Exception("Ambiguous credential must not be sent")), testMetadataKey: Fixtures.Key))
            Check((await refused.ReadAsync(new("flip-homework") { Course = flip }, CancellationToken.None)).Outcome == "unrecognized_authorization", "ambiguous official document cookie rejected");
    }
}
