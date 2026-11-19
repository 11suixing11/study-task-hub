namespace AccessProbe.Linux;
public static class LinuxRuntime
{
    private static readonly Lazy<bool> Ready = new(() =>
    {
        // Ubuntu runtime packages expose the versioned SONAME; the unversioned
        // libsqlite3.so is supplied only by the development package.
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
            (name, assembly, searchPath) => name == "sqlite3"
                ? System.Runtime.InteropServices.NativeLibrary.Load("libsqlite3.so.0", assembly, searchPath) : IntPtr.Zero);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        SQLitePCL.raw.FreezeProvider();
        return true;
    });
    public static void InitializeSqlite() => _ = Ready.Value;
}
