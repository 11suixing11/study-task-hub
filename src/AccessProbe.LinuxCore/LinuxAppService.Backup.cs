using System.Security.Cryptography;
namespace AccessProbe.Linux;
public sealed partial class LinuxAppService
{
    public BackupPreview CreateBackup(string path,string password)
    {
        EnsureOpen();path=Path.GetFullPath(path);var directory=Path.Combine(DataRoot,".backup-write-"+Guid.NewGuid().ToString("N"));LinuxDataPaths.EnsurePrivateDirectory(directory);
        byte[]? key=null;
        try
        {
            var snapshot=Path.Combine(directory,"tasks.db");store.BackupLinuxDatabase(snapshot);key=protector.ExportBackupKey();
            LinuxBackup.Encrypt(path,password,File.ReadAllBytes(snapshot),key,utcNow());
            return InspectBackup(path,password);
        }
        finally{if(key is not null)CryptographicOperations.ZeroMemory(key);Directory.Delete(directory,true);}
    }
    public BackupPreview InspectBackup(string path,string password)
    {
        EnsureOpen();var extracted=LinuxBackup.Extract(Path.GetFullPath(path),password,DataRoot);
        try{return extracted.Preview;}finally{Directory.Delete(extracted.Directory,true);}
    }
    public RestoreResult RestoreBackup(string path,string password,string fingerprint)
    {
        EnsureOpen();if(!syncReportGate.Wait(0))throw new TaskValidationException("BACKUP005","请先停止同步，再恢复备份。");
        string? checkedDirectory=null;
        try
        {
            var extracted=LinuxBackup.Extract(Path.GetFullPath(path),password,DataRoot);checkedDirectory=extracted.Directory;
            if(extracted.Preview.Fingerprint!=fingerprint)throw new TaskValidationException("BACKUP004","备份文件已变化，请重新检查并确认。");
            ReleaseAllNotificationClaims();
            var operation=Path.Combine(DataRoot,".restore-"+Guid.NewGuid().ToString("N"));LinuxDataPaths.EnsurePrivateDirectory(operation);
            var rollback=Path.Combine(operation,"rollback");LinuxDataPaths.EnsurePrivateDirectory(rollback);
            store.BackupLinuxDatabase(Path.Combine(rollback,"tasks.db"));var rollbackKey=protector.ExportBackupKey();
            try{LinuxBackup.WritePrivate(Path.Combine(rollback,"protection.key"),rollbackKey);}finally{CryptographicOperations.ZeroMemory(rollbackKey);}
            var rollbackBackup=Path.Combine(operation,"before-restore.studybackup");CreateBackup(rollbackBackup,password);
            using(var connection=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(checkedDirectory,"tasks.db")};Pooling=False"))
            {
                connection.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE reminders SET state='scheduled',lease_until_utc=NULL,next_attempt_utc=NULL WHERE state='processing';";command.ExecuteNonQuery();
            }
            var journal=Path.Combine(DataRoot,".restore-journal");LinuxBackup.WritePrivate(journal,System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(operation)));
            store.Changed-=Changed;protector.Dispose();
            try
            {
                foreach(var file in new[]{"tasks.db","protection.key"})File.Move(Path.Combine(checkedDirectory,file),Path.Combine(DataRoot,file),true);
                var profiles=Path.Combine(DataRoot,"profiles");LinuxBackup.CheckPath(profiles);
                if(Directory.Exists(profiles))Directory.Move(profiles,Path.Combine(rollback,"profiles"));
                LinuxBackup.ValidateDirectory(DataRoot);ReopenAfterRestore();File.Delete(journal);Changed();
                return new(extracted.Preview,rollbackBackup,rollback);
            }
            catch{LinuxBackup.RecoverPendingRestore(DataRoot);ReopenAfterRestore();throw;}
        }
        finally{if(checkedDirectory is not null&&Directory.Exists(checkedDirectory))Directory.Delete(checkedDirectory,true);syncReportGate.Release();}
    }
    private void ReopenAfterRestore()
    {
        var nextProtector=new LinuxDataProtector(DataRoot);TaskStore nextStore;
        try{nextStore=new(Path.Combine(DataRoot,"tasks.db"),nextProtector);}catch{nextProtector.Dispose();throw;}
        protector.Dispose();protector=nextProtector;store=nextStore;store.Changed+=Changed;
        sessionVault=null;sync=null;
    }
}
