using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace AccessProbe.Linux;

public sealed record BackupPreview(string Path,string Fingerprint,int FormatVersion,DateTimeOffset CreatedAtUtc,int TaskCount,int SessionCount);
public sealed record RestoreResult(BackupPreview Backup,string RollbackBackupPath,string RollbackDirectory);
internal sealed record BackupManifest(int Version,DateTimeOffset CreatedAtUtc,string DatabaseHash,string KeyHash);
internal static class LinuxBackup
{
    internal const long MaximumBytes=128*1024*1024;
    private static readonly byte[] Magic="STUBK001"u8.ToArray();
    internal static void CheckPath(string path)
    {
        if(!System.IO.Path.IsPathFullyQualified(path))throw new TaskValidationException("BACKUP001","请选择完整备份文件路径。");
        for(FileSystemInfo? info=new FileInfo(path);info is not null;info=info is FileInfo file?file.Directory:((DirectoryInfo)info).Parent)
            if(info.LinkTarget is not null)throw new TaskValidationException("BACKUP001","备份和恢复路径不能使用符号链接。");
    }
    internal static void WritePrivate(string path,byte[] bytes)
    {
        using var stream=new FileStream(path,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});
        stream.Write(bytes);stream.Flush(true);
    }
    private static void Password(string password)
    {if(password is null||password.Length is <8 or >1024)throw new TaskValidationException("BACKUP001","备份密码需为 8–1024 字符。");}
    internal static void Encrypt(string path,string password,byte[] database,byte[] key,DateTimeOffset now)
    {
        Password(password);CheckPath(path);
        if(database.LongLength>MaximumBytes)throw new TaskValidationException("BACKUP001","任务库超过本版备份大小上限。");
        using var zipped=new MemoryStream();
        using(var archive=new ZipArchive(zipped,ZipArchiveMode.Create,true))
        {
            void Entry(string name,byte[] bytes){using var output=archive.CreateEntry(name,CompressionLevel.Optimal).Open();output.Write(bytes);}
            Entry("manifest.json",JsonSerializer.SerializeToUtf8Bytes(new BackupManifest(1,now,Convert.ToHexString(SHA256.HashData(database)),Convert.ToHexString(SHA256.HashData(key)))));
            Entry("tasks.db",database);Entry("protection.key",key);
        }
        var plain=zipped.ToArray();var salt=RandomNumberGenerator.GetBytes(16);var nonce=RandomNumberGenerator.GetBytes(12);
        var derived=Rfc2898DeriveBytes.Pbkdf2(password,salt,300000,HashAlgorithmName.SHA256,32);var encrypted=new byte[52+plain.Length];
        try
        {
            Magic.CopyTo(encrypted,0);salt.CopyTo(encrypted,8);nonce.CopyTo(encrypted,24);
            using var aes=new AesGcm(derived,16);aes.Encrypt(nonce,plain,encrypted.AsSpan(52),encrypted.AsSpan(36,16),Magic);
            WritePrivate(path,encrypted);
        }
        catch(IOException){throw new TaskValidationException("BACKUP001","备份文件无法写入或已存在，请选择新文件。");}
        finally{CryptographicOperations.ZeroMemory(plain);CryptographicOperations.ZeroMemory(derived);}
    }
    internal static (BackupPreview Preview,string Directory) Extract(string path,string password,string dataRoot)
    {
        Password(password);CheckPath(path);byte[] bytes;
        try
        {
            var info=new FileInfo(path);if(!info.Exists||info.Length is <52 or >MaximumBytes)throw new TaskValidationException("BACKUP002","备份格式或大小不正确。");
            bytes=File.ReadAllBytes(path);
        }
        catch(IOException){throw new TaskValidationException("BACKUP001","无法读取备份文件。");}
        if(!bytes.AsSpan(0,8).SequenceEqual(Magic))throw new TaskValidationException("BACKUP002","备份版本不支持，请使用对应版本的程序。");
        var plain=new byte[bytes.Length-52];var derived=Rfc2898DeriveBytes.Pbkdf2(password,bytes.AsSpan(8,16),300000,HashAlgorithmName.SHA256,32);
        var staging=System.IO.Path.Combine(dataRoot,".backup-check-"+Guid.NewGuid().ToString("N"));LinuxDataPaths.EnsurePrivateDirectory(staging);
        try
        {
            using(var aes=new AesGcm(derived,16))aes.Decrypt(bytes.AsSpan(24,12),bytes.AsSpan(52),bytes.AsSpan(36,16),plain,Magic);
            using var stream=new MemoryStream(plain);using var archive=new ZipArchive(stream,ZipArchiveMode.Read);
            if(archive.Entries.Count!=3||archive.Entries.Select(e=>e.FullName).Distinct().Count()!=3||archive.Entries.Any(e=>e.FullName is not("manifest.json" or "tasks.db" or "protection.key"))||archive.Entries.Sum(e=>e.Length)>MaximumBytes)throw new InvalidDataException();
            byte[] Read(string name,long limit)
            {
                var entry=archive.GetEntry(name)!;if(entry.Length>limit)throw new InvalidDataException();
                using var input=entry.Open();using var output=new MemoryStream();input.CopyTo(output);
                if(output.Length!=entry.Length)throw new InvalidDataException();return output.ToArray();
            }
            var manifest=JsonSerializer.Deserialize<BackupManifest>(Read("manifest.json",8192))??throw new InvalidDataException();
            if(manifest.Version!=1)throw new TaskValidationException("BACKUP002","备份版本不支持。");
            var db=Read("tasks.db",MaximumBytes);var key=Read("protection.key",32);
            try
            {
                if(key.Length!=32||manifest.DatabaseHash!=Convert.ToHexString(SHA256.HashData(db))||manifest.KeyHash!=Convert.ToHexString(SHA256.HashData(key)))throw new InvalidDataException();
                WritePrivate(System.IO.Path.Combine(staging,"tasks.db"),db);WritePrivate(System.IO.Path.Combine(staging,"protection.key"),key);
            }
            finally{CryptographicOperations.ZeroMemory(key);}
            var (tasks,sessions)=ValidateDirectory(staging);
            return(new(path,Convert.ToHexString(SHA256.HashData(bytes)),1,manifest.CreatedAtUtc,tasks,sessions),staging);
        }
        catch(Exception error)when(error is CryptographicException or InvalidDataException or JsonException or SqliteException)
        {Directory.Delete(staging,true);throw new TaskValidationException("BACKUP003","密码不正确或备份损坏，当前数据未替换。");}
        catch{Directory.Delete(staging,true);throw;}
        finally{CryptographicOperations.ZeroMemory(plain);CryptographicOperations.ZeroMemory(derived);}
    }
    internal static (int Tasks,int Sessions) ValidateDirectory(string root)
    {
        using var protector=new LinuxDataProtector(root);
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=System.IO.Path.Combine(root,"tasks.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();
        using var command=connection.CreateCommand();command.CommandText="PRAGMA user_version;";
        if(Convert.ToInt32(command.ExecuteScalar())!=1)throw new TaskValidationException("BACKUP002","备份数据库版本不支持。");
        command.CommandText="PRAGMA integrity_check;";if(command.ExecuteScalar() as string!="ok")throw new InvalidDataException();
        command.CommandText="PRAGMA foreign_key_check;";using(var reader=command.ExecuteReader())if(reader.Read())throw new InvalidDataException();
        command.CommandText="SELECT protected_link FROM tasks WHERE protected_link IS NOT NULL UNION ALL SELECT protected_cookies FROM platform_sessions WHERE protected_cookies IS NOT NULL;";
        using(var reader=command.ExecuteReader())while(reader.Read())protector.Unprotect((byte[])reader.GetValue(0));
        var store=new TaskStore(System.IO.Path.Combine(root,"tasks.db"),protector);var tasks=store.LoadTasks();
        if(tasks.Any(t=>t.LinkIssue is not null))throw new InvalidDataException();
        command.CommandText="SELECT count(*) FROM platform_sessions WHERE protected_cookies IS NOT NULL;";
        return(tasks.Count,Convert.ToInt32(command.ExecuteScalar()));
    }
    internal static void RecoverPendingRestore(string root)
    {
        var journal=System.IO.Path.Combine(root,".restore-journal");if(!File.Exists(journal))return;CheckPath(journal);
        var name=File.ReadAllText(journal);
        if(name.Length!=41||!name.StartsWith(".restore-",StringComparison.Ordinal)||!name[9..].All(char.IsAsciiHexDigit))throw new TaskValidationException("BACKUP005","恢复日志无效，请检查原回退目录。");
        var operation=System.IO.Path.Combine(root,name);var rollback=System.IO.Path.Combine(operation,"rollback");
        CheckPath(rollback);ValidateDirectory(rollback);
        foreach(var file in new[]{"tasks.db","protection.key"})
        {
            var target=System.IO.Path.Combine(root,file);CheckPath(target);
            var temp=System.IO.Path.Combine(root,".recover-"+Guid.NewGuid().ToString("N"));WritePrivate(temp,File.ReadAllBytes(System.IO.Path.Combine(rollback,file)));File.Move(temp,target,true);
        }
        foreach(var suffix in new[]{"-wal","-shm","-journal"})File.Delete(System.IO.Path.Combine(root,"tasks.db"+suffix));
        var oldProfiles=System.IO.Path.Combine(rollback,"profiles");
        if(Directory.Exists(oldProfiles))
        {
            var profiles=System.IO.Path.Combine(root,"profiles");CheckPath(profiles);
            if(Directory.Exists(profiles))Directory.Move(profiles,System.IO.Path.Combine(operation,"profiles-after-failure"));
            Directory.Move(oldProfiles,profiles);
        }
        File.Delete(journal);
    }
}
