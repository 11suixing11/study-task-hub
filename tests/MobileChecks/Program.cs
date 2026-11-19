using AccessProbe;
using AccessProbe.Mobile;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

try
{
    if (args.Length != 1) throw new ArgumentException("An isolated test root is required.");
    var notifications = MobileNotificationChecks.Run();
    var notificationReport = JsonSerializer.Serialize(notifications);
    Console.WriteLine(notificationReport);
    if (notifications.Outcome != "passed") throw new InvalidDataException("Android notification checks failed.");
    var backNavigation = MobileBackNavigationChecks.Run();
    Directory.CreateDirectory(args[0]);
    var backNavigationReport = JsonSerializer.Serialize(backNavigation);
    File.WriteAllText(Path.Combine(args[0], "back-navigation-checks.json"), backNavigationReport);
    Console.WriteLine(backNavigationReport);
    if (backNavigation.Outcome != "passed") throw new InvalidDataException("Android back navigation checks failed.");
    using var protector = new SyntheticProtector();
    TaskSummaryChecks.Run(Path.Combine(args[0], "task-summary"));
    var uiData = await MobileUiDataChecks.RunAsync(Path.Combine(args[0], "ui-data", Guid.NewGuid().ToString("N")), protector);
    var uiDataReport = JsonSerializer.Serialize(uiData);
    File.WriteAllText(Path.Combine(args[0], "ui-data-checks.json"), uiDataReport);
    Console.WriteLine(uiDataReport);
    if (uiData.Outcome != "passed") throw new InvalidDataException("Android UI data checks failed.");
    var pages = await MobilePageDataChecks.RunAsync(Path.Combine(args[0], "pages", Guid.NewGuid().ToString("N")), protector);
    var pagesReport = JsonSerializer.Serialize(pages);
    File.WriteAllText(Path.Combine(args[0], "page-data-checks.json"), pagesReport);
    Console.WriteLine(pagesReport);
    if (pages.Outcome != "passed") throw new InvalidDataException("Android page data checks failed.");
    var result = await MobileSyntheticChecks.RunAsync(args[0], protector);
    Console.WriteLine(JsonSerializer.Serialize(result));
    if (result.Outcome != "passed") Environment.ExitCode = 1;
    else
    {
        var improvements = await MobileImprovementChecks.RunAsync(Path.Combine(args[0], "improvements", Guid.NewGuid().ToString("N")), protector);
        var report = JsonSerializer.Serialize(improvements);
        File.WriteAllText(Path.Combine(args[0], "improvement-checks.json"), report);
        Console.WriteLine(report);
        var login = await MobileLoginRecoveryChecks.RunAsync(Path.Combine(args[0], "login-recovery", Guid.NewGuid().ToString("N")), protector);
        var loginReport = JsonSerializer.Serialize(login);
        File.WriteAllText(Path.Combine(args[0], "login-recovery-checks.json"), loginReport);
        Console.WriteLine(loginReport);
        var connection = await MobileConnectionChecks.RunAsync(Path.Combine(args[0], "connection", Guid.NewGuid().ToString("N")), protector);
        var connectionReport = JsonSerializer.Serialize(connection);
        File.WriteAllText(Path.Combine(args[0], "connection-checks.json"), connectionReport);
        Console.WriteLine(connectionReport);
    }
}
catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { outcome = "failed", errorType = error.GetType().Name, message = error.Message }));
    Environment.ExitCode = 1;
}

sealed class SyntheticProtector : ILocalLinkProtector, IDisposable
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    public byte[] Protect(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value); var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag); CryptographicOperations.ZeroMemory(plain);
        return nonce.Concat(tag).Concat(cipher).ToArray();
    }
    public string Unprotect(byte[] value)
    {
        if (value.Length < 28) throw new CryptographicException();
        var plain = new byte[value.Length - 28]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), plain);
        try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
