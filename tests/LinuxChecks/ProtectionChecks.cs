using AccessProbe;
using AccessProbe.Linux;
using System.Security.Cryptography;

namespace LinuxChecks;

static class ProtectionChecks
{
    public static async Task RunAsync()
    {
        Check.Equal("/home/test/.local/share/study-task-hub", LinuxDataPaths.Resolve(null, null, "/home/test"));
        Check.Equal("/tmp/xdg/study-task-hub", LinuxDataPaths.Resolve(null, "/tmp/xdg", "/home/test"));
        Check.Equal("/tmp/explicit", LinuxDataPaths.Resolve("/tmp/explicit", "/tmp/xdg", "/home/test"));
        using var root = new TestRoot();
        byte[] encrypted;
        using (var first = new LinuxDataProtector(root.Path)) encrypted = first.Protect("https://example.invalid/课程");
        using (var restarted = new LinuxDataProtector(root.Path)) Check.Equal("https://example.invalid/课程", restarted.Unprotect(encrypted));
        Check.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(System.IO.Path.Combine(root.Path, "protection.key")));
        Check.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root.Path));
        using var other = new TestRoot();
        using (var wrong = new LinuxDataProtector(other.Path)) Check.Throws<CryptographicException>(() => wrong.Unprotect(encrypted));
        encrypted[^1] ^= 1;
        using (var restarted = new LinuxDataProtector(root.Path)) Check.Throws<CryptographicException>(() => restarted.Unprotect(encrypted));
        using var missing = new TestRoot();
        File.WriteAllBytes(System.IO.Path.Combine(missing.Path, "tasks.db"), [1]);
        Check.Equal("KEY002", Check.Throws<TaskValidationException>(() => new LinuxDataProtector(missing.Path)).Code);
        Check.True(!File.Exists(System.IO.Path.Combine(missing.Path, "protection.key")));
        using var invalid = new TestRoot();
        var key = System.IO.Path.Combine(invalid.Path, "protection.key"); File.WriteAllBytes(key, [1]);
        File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Check.Equal("KEY001", Check.Throws<TaskValidationException>(() => new LinuxDataProtector(invalid.Path)).Code);
        Check.Equal(1L, new FileInfo(key).Length);
        File.Delete(key); File.CreateSymbolicLink(key, System.IO.Path.Combine(root.Path, "protection.key"));
        Check.Throws<TaskValidationException>(() => new LinuxDataProtector(invalid.Path));
        using var insecure = new TestRoot();
        using (var first = new LinuxDataProtector(insecure.Path)) { }
        File.SetUnixFileMode(System.IO.Path.Combine(insecure.Path,"protection.key"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Check.Throws<TaskValidationException>(() => new LinuxDataProtector(insecure.Path));
        using var concurrent = new TestRoot();
        var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => { using var p = new LinuxDataProtector(concurrent.Path); return p.Protect("concurrent"); })));
        using (var p = new LinuxDataProtector(concurrent.Path)) foreach (var value in values) Check.Equal("concurrent", p.Unprotect(value));
        LinuxRuntime.InitializeSqlite(); LinuxRuntime.InitializeSqlite();
        using var sqliteRoot = new TestRoot(); using var protector = new LinuxDataProtector(sqliteRoot.Path);
        var store = new TaskStore(System.IO.Path.Combine(sqliteRoot.Path,"tasks.db"),protector);
        var task = store.Create(new("隔离任务","chaoxing","数学",DeadlineKind.Unknown,null,null,null,null,"","",null),DateTimeOffset.UtcNow);
        Check.Equal(task.Id,store.LoadTasks().Single().Id);
    }
}
