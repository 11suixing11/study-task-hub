using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AccessProbe.Linux;
public sealed class LinuxDataProtector : ILocalLinkProtector, IDisposable
{
    private readonly byte[] key;
    private bool disposed;
    private static readonly object Gate = new();
    private const int NoFollow = 131072, CloseOnExec = 524288, Create = 64, Exclusive = 128;
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int flock(int fd, int operation);
    [DllImport("libc", SetLastError = true)] private static extern int link(string oldPath, string newPath);

    public LinuxDataProtector(string dataRoot)
    {
        LinuxDataPaths.EnsurePrivateDirectory(dataRoot);
        lock (Gate)
        {
            using var guard = OpenPrivate(Path.Combine(dataRoot,".protection.lock"), 2 | Create, FileAccess.ReadWrite);
            if (flock(guard.SafeFileHandle.DangerousGetHandle().ToInt32(), 2) != 0) throw Invalid();
            var keyPath = Path.Combine(dataRoot,"protection.key");
            if (!File.Exists(keyPath) && new FileInfo(keyPath).LinkTarget is null)
            {
                if (File.Exists(Path.Combine(dataRoot,"tasks.db"))) throw new TaskValidationException("KEY002", "已有任务库但保护密钥缺失，请恢复原密钥；原数据未覆盖。");
                var temp = Path.Combine(dataRoot,".key-"+Guid.NewGuid().ToString("N"));
                var bytes = RandomNumberGenerator.GetBytes(32);
                try
                {
                    using (var stream = OpenPrivate(temp, 1 | Create | Exclusive, FileAccess.Write)) { stream.Write(bytes); stream.Flush(true); }
                    if (link(temp,keyPath) != 0) throw Invalid();
                }
                finally { CryptographicOperations.ZeroMemory(bytes); File.Delete(temp); }
            }
            using var saved = OpenPrivate(keyPath,0,FileAccess.Read);
            if (saved.Length != 32) throw Invalid();
            key = new byte[32]; saved.ReadExactly(key);
        }
    }

    private static TaskValidationException Invalid() => new("KEY001", "保护密钥不可用，请检查文件、权限及原始备份；原数据未覆盖。");
    private static FileStream OpenPrivate(string path, int flags, FileAccess access)
    {
        var fd = open(path, flags | NoFollow | CloseOnExec, 384);
        if (fd < 0) throw Invalid();
        var handle = new SafeFileHandle(new IntPtr(fd),true);
        try
        {
            if (File.GetUnixFileMode("/proc/self/fd/"+fd) != PrivateMode) throw Invalid();
            return new FileStream(handle,access);
        }
        catch { handle.Dispose(); throw; }
    }

    public byte[] Protect(string value)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var plain = Encoding.UTF8.GetBytes(value); var result = new byte[29+plain.Length]; result[0]=1;
        try
        {
            RandomNumberGenerator.Fill(result.AsSpan(1,12));
            using var aes = new AesGcm(key,16);
            aes.Encrypt(result.AsSpan(1,12),plain,result.AsSpan(29),result.AsSpan(13,16));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal byte[] ExportBackupKey(){ObjectDisposedException.ThrowIf(disposed,this);return key.ToArray();}

    public string Unprotect(byte[] value)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if (value.Length < 29 || value[0] != 1) throw new CryptographicException("Invalid protected data.");
        var plain = new byte[value.Length-29];
        try
        {
            using var aes = new AesGcm(key,16);
            aes.Decrypt(value.AsSpan(1,12),value.AsSpan(29),value.AsSpan(13,16),plain);
            return Encoding.UTF8.GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Dispose() { disposed=true; CryptographicOperations.ZeroMemory(key); }
}
