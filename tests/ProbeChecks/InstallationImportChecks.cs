using AccessProbe;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

internal static class InstallationImportChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "aggregator-import-check-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "旧版"); var target = Path.Combine(root, "安装数据");
        Directory.CreateDirectory(Path.Combine(source, ".local", "data"));
        var sourceDatabase = Path.Combine(source, ".local", "data", "tasks.db");
        var targetDatabase = Path.Combine(target, ".local", "data", "tasks.db"); var checks = 0;
        void Check(bool value) { checks++; if (!value) throw new Exception("Installer import check failed: " + checks); }
        try
        {
            using var active = new SqliteConnection("Data Source=" + sourceDatabase + ";Pooling=False"); active.Open();
            using var command = active.CreateCommand(); command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE records(id TEXT); INSERT INTO records VALUES('synthetic');"; command.ExecuteNonQuery();
            Directory.CreateDirectory(Path.Combine(source, ".local", "sessions"));
            var archive = Path.Combine(source, ".local", "sessions", "pta.bin"); File.WriteAllBytes(archive, [1, 2, 3]);
            Check(InstallationDataImport.Import(source, target) == "imported");
            Check(File.Exists(targetDatabase));
            using var imported = new SqliteConnection("Data Source=" + targetDatabase + ";Mode=ReadOnly;Pooling=False"); imported.Open();
            using var read = imported.CreateCommand(); read.CommandText = "SELECT count(*) FROM records";
            Check(Convert.ToInt32(read.ExecuteScalar()) == 1);
            Check(File.ReadAllBytes(Path.Combine(target, ".local", "sessions", "pta.bin")).SequenceEqual(new byte[] {1, 2, 3}));
            var originalHash = SHA256.HashData(File.ReadAllBytes(targetDatabase));
            command.CommandText = "INSERT INTO records VALUES('later')"; command.ExecuteNonQuery();
            Check(InstallationDataImport.Import(source, target) == "existing_data_preserved");
            Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(targetDatabase))));
            Check(File.ReadAllBytes(archive).SequenceEqual(new byte[] {1, 2, 3}));
            Check(!Directory.GetDirectories(Path.Combine(target, ".local"), "import-*").Any());
            try { InstallationDataImport.Import(source, source); throw new Exception("Same data source accepted."); } catch (IOException) { checks++; }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {checks} read-only legacy snapshot, WAL import, encrypted archive copy and existing-data preservation checks (synthetic only).");
    }
}
