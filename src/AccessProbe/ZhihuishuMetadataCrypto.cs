using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessProbe;

// Public student home, shared task and smart course list clients use modules 13, 3 and 6.
// No app code is evaluated, and the recovered key lives only for a single sync reader.
public static class ZhihuishuMetadataCrypto
{
    public const string PublicKey = "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCgfZmpLpPEpEFRKBe+ZjWJUjPe+7qg7pGqcfN3j2egJ8H2mrKwaEqZEnPnpi2O3hN8HRyaFozDOp8gwZiYfiIZjWy0Jr/FNAiiKYh5bq0GsEn+ieMmRyJg/+i1rqizhvCXvFdrdGhFTw5EBwTpsGdwe1utdlrvIJUAFWj9Yh4qbQIDAQAB";
    public static string HandshakeParameter(int module = 13)
    {
        ValidateModule(module);
        using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { module })), RSAEncryptionPadding.Pkcs1));
    }
    public static byte[] RecoverKey(string signedPayload, int module = 13)
    {
        ValidateModule(module);
        if (signedPayload.Length > 1024) throw new CryptographicException("Invalid metadata key size.");
        using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
        var parameters = rsa.ExportParameters(false); var cipher = Convert.FromBase64String(signedPayload);
        if (cipher.Length != parameters.Modulus!.Length) throw new CryptographicException("Invalid RSA block length.");
        var n = new BigInteger(parameters.Modulus, true, true); var c = new BigInteger(cipher, true, true);
        if (c >= n) throw new CryptographicException("Invalid RSA representative.");
        var restored = BigInteger.ModPow(c, new(parameters.Exponent!, true, true), n).ToByteArray(true, true);
        var block = new byte[parameters.Modulus.Length]; restored.CopyTo(block.AsSpan(block.Length - restored.Length));
        try { return KeyFromBlock(block, module); }
        finally { CryptographicOperations.ZeroMemory(block); CryptographicOperations.ZeroMemory(restored); }
    }
    public static byte[] KeyFromBlock(ReadOnlySpan<byte> block, int expectedModule = 13)
    {
        ValidateModule(expectedModule);
        if (block.Length != 128 || block[0] != 0 || block[1] != 1) throw new CryptographicException("Invalid RSA padding.");
        var i = 2; while (i < block.Length && block[i] == 255) i++;
        if (i < 10 || i >= block.Length - 1 || block[i] != 0) throw new CryptographicException("Invalid RSA padding.");
        try
        {
            using var document = JsonDocument.Parse(block[(i + 1)..].ToArray()); var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("module", out var module)
                || module.ValueKind != JsonValueKind.Number || !module.TryGetInt32(out var number) || number != expectedModule
                || !value.TryGetProperty("cKey", out var key) || key.ValueKind != JsonValueKind.String)
                throw new CryptographicException("Unexpected metadata key contract.");
            var bytes = Encoding.UTF8.GetBytes(key.GetString()!);
            if (bytes.Length != 16) { CryptographicOperations.ZeroMemory(bytes); throw new CryptographicException("Invalid AES key length."); }
            return bytes;
        }
        catch (JsonException) { throw new CryptographicException("Unexpected metadata key contract."); }
    }
    private static void ValidateModule(int module)
    { if (module is not (3 or 6 or 13)) throw new CryptographicException("Unsupported metadata module."); }
    public static string Encrypt(string json, ReadOnlySpan<byte> key)
    {
        if (key.Length != 16 || Encoding.UTF8.GetByteCount(json) > 65536) throw new CryptographicException("Invalid metadata parameters.");
        var plain = Encoding.UTF8.GetBytes(json);
        try
        {
            using var aes = Aes.Create(); aes.Key = key.ToArray();
            return Convert.ToBase64String(aes.EncryptCbc(plain, Encoding.UTF8.GetBytes("1g3qqdh4jvbskb9x"), PaddingMode.PKCS7));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
