using AccessProbe;
using AccessProbe.Mobile;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

try
{
    if (args.Length != 1) throw new ArgumentException("An isolated test root is required.");
    using var protector = new SyntheticProtector();
    var result = await MobileSyntheticChecks.RunAsync(args[0], protector);
    Console.WriteLine(JsonSerializer.Serialize(result));
    if (result.Outcome != "passed") Environment.ExitCode = 1;
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
