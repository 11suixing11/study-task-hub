using AccessProbe.Mobile;
namespace AccessProbe.Linux;
public sealed partial class LinuxAppService
{
    public CourseRecordingSettings GetCourses(string platform)=>store.LoadCourseRecordingOverview(platform);
    private readonly object syncLock=new();
    private MobileSync? sync;
    public CourseRecordingSettings SelectCourses(string platform,long revision,IReadOnlyList<string> keys,bool hideUnselected)
    {EnsureOpen();return store.SaveCourseRecording(platform,false,keys,hideUnselected,revision,utcNow());}
    public Task<MobileSyncResult> RunSyncAsync(string platform,bool catalogOnly,IProgress<string>? progress,CancellationToken cancellation)
    {
        EnsureOpen();
        if(!CourseRecordingRules.SupportsAutomatic(platform))throw new TaskValidationException("SYNC003","该平台仅支持手动记录。");
        GetBrowserSession(platform);
        lock(syncLock)sync??=new(store,Vault,readerFactory,syntheticMetadataKey);
        return sync.RunAsync(platform,catalogOnly,progress,cancellation);
    }
}
