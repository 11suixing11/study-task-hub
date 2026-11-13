using AccessProbe.Linux;
using Microsoft.Data.Sqlite;
namespace AccessProbe;
public sealed partial class TaskStore
{
    internal void BackupLinuxDatabase(string path)
    {
        LinuxBackup.CheckPath(path);LinuxBackup.WritePrivate(path,[]);
        lock(gate)
        {
            using var source=Open();using var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());destination.Open();source.BackupDatabase(destination);
        }
    }
}
