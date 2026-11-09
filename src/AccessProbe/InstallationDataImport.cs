using System.IO;
using Microsoft.Data.Sqlite;

namespace AccessProbe;

public static class InstallationDataImport
{
    public static string Import(string sourceRoot, string destinationRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot); destinationRoot = Path.GetFullPath(destinationRoot);
        if (sourceRoot.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("导入来源不能与安装版数据目录相同。");
        var source = Path.Combine(sourceRoot, ".local", "data", "tasks.db");
        var destination = Path.Combine(destinationRoot, ".local", "data", "tasks.db");
        if (File.Exists(destination)) return "existing_data_preserved";
        if (!File.Exists(source)) throw new IOException("所选文件夹中没有旧版 .local/data/tasks.db。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = Path.Combine(destinationRoot, ".local", "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var copy = Path.Combine(staging, "tasks.db");
            using (var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
            {
                input.Open(); output.Open(); input.BackupDatabase(output);
                using var check = output.CreateCommand(); check.CommandText = "PRAGMA quick_check";
                if (check.ExecuteScalar() as string != "ok") throw new IOException("旧版任务库完整性检查未通过，已停止导入。");
            }
            File.Move(copy, destination, false);
            var sourceSessions = Path.Combine(sourceRoot, ".local", "sessions");
            var targetSessions = Path.Combine(destinationRoot, ".local", "sessions");
            foreach (var platform in new[] { "chaoxing", "yuketang", "changjiang", "mooc", "xuetang", "ucampus", "zhihuishu", "pta" })
            {
                var archive = Path.Combine(sourceSessions, platform + ".bin");
                if (!File.Exists(archive) || new FileInfo(archive).Length > 2 * 1024 * 1024 + 4096) continue;
                Directory.CreateDirectory(targetSessions);
                var target = Path.Combine(targetSessions, platform + ".bin");
                if (!File.Exists(target)) File.Copy(archive, target, false);
            }
            return "imported";
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
