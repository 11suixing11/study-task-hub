using System.Security.Cryptography;
using System.Text;

namespace AccessProbe;

public interface ILocalLinkProtector
{
    byte[] Protect(string link);
    string Unprotect(byte[] protectedLink);
}

public sealed class WindowsLinkProtector : ILocalLinkProtector
{
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("LearningTaskAggregator.links.v1"));

    public byte[] Protect(string link)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows local protection is required.");
        var plain = Encoding.UTF8.GetBytes(link);
        try { return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public string Unprotect(byte[] protectedLink)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows local protection is required.");
        var plain = ProtectedData.Unprotect(protectedLink, Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
