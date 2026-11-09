using System.IO;

namespace AccessProbe;

// The repository stores only DPAPI ciphertext. Legacy archives are imported without exporting cookies.
internal sealed class PlatformSessionArchive(string platformId, string path, TaskStore? repository)
{
    public bool UsesRepository => repository is not null;

    public byte[]? Read(bool includeExpired = false)
    {
        if (repository is not null)
        {
            if (!includeExpired && repository.LoadPlatformSessionInfo(platformId).State == "expired") return null;
            var current = repository.ReadProtectedPlatformSession(platformId);
            if (current.Exists) return current.Data;
        }
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2 * 1024 * 1024 + (UsesRepository ? 4096 : 0)) throw new InvalidDataException("Session size limit exceeded.");
        var bytes = File.ReadAllBytes(path);
        repository?.WriteProtectedPlatformSession(platformId, bytes, DateTimeOffset.UtcNow);
        return bytes;
    }

    public void Write(byte[] bytes, DateTimeOffset now)
    {
        if (repository is not null) { repository.WriteProtectedPlatformSession(platformId, bytes, now); return; }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Clear()
    {
        repository?.ClearPlatformSession(platformId);
        if (File.Exists(path)) File.Delete(path);
    }

    public void Invalidate(DateTimeOffset now, bool preserveForRevalidation = false)
    {
        repository?.ExpirePlatformSession(platformId, now, preserveForRevalidation);
        if (File.Exists(path)) File.Delete(path);
    }
}
