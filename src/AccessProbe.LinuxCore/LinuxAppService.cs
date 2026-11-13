namespace AccessProbe.Linux;
public sealed partial class LinuxAppService : IDisposable
{
    private readonly TaskStore store;
    private readonly LinuxDataProtector protector;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly TimeZoneInfo timeZone;
    private readonly Func<string,HttpMessageHandler>? readerFactory;
    private readonly ReadOnlyMemory<byte>? syntheticMetadataKey;
    private long snapshotVersion;
    private bool disposed;
    public string DataRoot { get; }
    public LinuxAppService(string dataRoot, Func<DateTimeOffset>? utcNow = null, TimeZoneInfo? timeZone = null,
        Func<string,HttpMessageHandler>? readerFactory = null, ReadOnlyMemory<byte>? syntheticMetadataKey = null)
    {
        DataRoot=Path.GetFullPath(dataRoot); this.utcNow=utcNow??(()=>DateTimeOffset.UtcNow); this.timeZone=timeZone??TimeZoneInfo.Local;
        this.readerFactory=readerFactory; this.syntheticMetadataKey=syntheticMetadataKey;
        LinuxRuntime.InitializeSqlite(); protector=new(DataRoot);
        try { store=new(Path.Combine(DataRoot,"tasks.db"),protector); }
        catch { protector.Dispose(); throw; }
        store.Changed+=Changed;
    }
    private void Changed() => Interlocked.Increment(ref snapshotVersion);
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed,this);
    public AppSettings LoadSettings() { EnsureOpen(); return store.LoadSettings(); }
    public void SaveSettings(AppSettings settings) { EnsureOpen(); store.SaveLinuxPreferences(settings); }
    public void Dispose()
    {
        if(disposed)return;
        try{ReleaseAllNotificationClaims();}
        finally{disposed=true;store.Changed-=Changed;protector.Dispose();}
    }
}
