using AccessProbe;

var allowed = new[] { "https://i.chaoxing.com/", "https://passport2.chaoxing.com/login?token=test", "https://i.xuexitong.com/", "about:blank" };
var blocked = new[] { "http://i.chaoxing.com/", "https://chaoxing.com.evil.example/", "https://evilchaoxing.com/", "https://i.chaoxing.com@evil.example/", "https://user@i.chaoxing.com/", "file:///C:/Windows/", "javascript:alert(1)", "https://i.chaoxing.com:9443/", "not-a-url" };
foreach (var address in allowed)
    if (!ProbePolicy.IsAllowedNavigation(address)) throw new Exception("Allowed navigation rejected.");
foreach (var address in blocked)
    if (ProbePolicy.IsAllowedNavigation(address)) throw new Exception("Unsafe navigation accepted.");
var category = ProbePolicy.SafeResponseCategory("https://i.chaoxing.com/private-student-id?token=secret", 200);
if (category != "official-http-2xx") throw new Exception("Metadata must not contain URL data.");
if (ProbePolicy.SafeResponseCategory("https://external.example/private", 200) != "other") throw new Exception("External host details leaked.");
Console.WriteLine($"PASS: {allowed.Length + blocked.Length + 2} navigation and metadata policy checks.");
await NoticeChecks.RunAsync();
WorkListChecks.Run();
WorkDetailChecks.Run();
WorkPageSnapshotChecks.Run();
HttpsNavigationChecks.Run();
var diagnosticCases = new (string Address, string Expected)[]
{
    ("https://passport2.chaoxing.com/login?token=secret", "official_https"),
    ("http://i.chaoxing.com/?token=secret", "insecure_http"),
    ("https://school.example/sso?token=secret", "external_domain"),
    ("custom-app://secret", "external_protocol"),
    ("https://user:secret@i.chaoxing.com/", "embedded_credentials"),
    ("https://i.chaoxing.com:9443/", "non_default_port"),
    ("about:blank", "blank"),
    ("not a url", "invalid_address")
};
foreach (var item in diagnosticCases)
    if (ProbePolicy.NavigationCategory(item.Address) != item.Expected)
        throw new Exception("Navigation diagnostic classification failed.");
Console.WriteLine($"PASS: {diagnosticCases.Length} secret-free navigation classification checks.");

TaskStoreChecks.Run();
TaskMetadataChecks.Run();
await TaskNavigationChecks.RunAsync();
await ChaoxingSyncChecks.RunAsync();
await ChaoxingSessionChecks.RunAsync();
await RainClassroomChecks.RunAsync();
await MoocChecks.RunAsync();
await CourseRecordingChecks.RunAsync();
