using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Security.Cryptography;
using System.Text;
using AccessProbe;
using CipherMode = Javax.Crypto.CipherMode;

namespace AccessProbe.Mobile;

public sealed class AndroidDataProtector(string keyAlias = "LearningTaskAggregator.Data.v1") : ILocalLinkProtector
{
    private static readonly object Gate = new();
    private IKey GetKey()
    {
        using var keys = KeyStore.GetInstance("AndroidKeyStore")!; keys.Load(null);
        var existing = keys.GetKey(keyAlias, null); if (existing is not null) return existing;
        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes!, "AndroidKeyStore")!;
        using var spec = new KeyGenParameterSpec.Builder(keyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm!)!.SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone!)!
            .SetRandomizedEncryptionRequired(true)!.Build()!;
        generator.Init(spec); return generator.GenerateKey()!;
    }
    public byte[] Protect(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            lock (Gate)
            {
                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!; cipher.Init(CipherMode.EncryptMode, GetKey());
                var iv = cipher.GetIV()!; var encrypted = cipher.DoFinal(plain)!;
                return new byte[] { 1, (byte)iv.Length }.Concat(iv).Concat(encrypted).ToArray();
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public string Unprotect(byte[] value)
    {
        if (value.Length < 30 || value[0] != 1 || value[1] != 12) throw new CryptographicException("Invalid protected data.");
        try
        {
            lock (Gate)
            {
                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
                using var parameters = new GCMParameterSpec(128, value.AsSpan(2, value[1]).ToArray());
                cipher.Init(CipherMode.DecryptMode, GetKey(), parameters);
                var plain = cipher.DoFinal(value.AsSpan(2 + value[1]).ToArray())!;
                try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        catch (Java.Security.GeneralSecurityException error) { throw new CryptographicException("Cannot read protected data.", error); }
    }
}

public static class MobileData
{
    public static string Root(Context context) => context.FilesDir!.AbsolutePath;
    public static TaskStore Open(Context context, AndroidDataProtector? protector = null) => new(System.IO.Path.Combine(Root(context), "tasks.db"), protector ?? new());
}
