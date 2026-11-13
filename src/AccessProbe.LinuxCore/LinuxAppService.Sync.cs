using AccessProbe.Mobile;
namespace AccessProbe.Linux;
public sealed partial class LinuxAppService
{
    public CourseRecordingSettings GetCourses(string platform)=>store.LoadCourseRecordingOverview(platform);
    private readonly object syncLock=new();
    private MobileSync? sync;
    private readonly SemaphoreSlim syncReportGate=new(1,1);
    public CourseRecordingSettings SelectCourses(string platform,long revision,IReadOnlyList<string> keys,bool hideUnselected)
    {EnsureOpen();return store.SaveCourseRecording(platform,false,keys,hideUnselected,revision,utcNow());}
    public async Task<MobileSyncResult> RunSyncAsync(string platform,bool catalogOnly,IProgress<string>? progress,CancellationToken cancellation)
    {
        EnsureOpen();
        if(!CourseRecordingRules.SupportsAutomatic(platform))throw new TaskValidationException("SYNC003","该平台仅支持手动记录。");
        await syncReportGate.WaitAsync(cancellation);
        var started=utcNow();
        try
        {
            catalogOnly|=!GetCourses(platform).SelectionConfirmed;
            MobileSyncResult result;
            try
            {
                GetBrowserSession(platform);
                lock(syncLock)sync??=new(store,Vault,readerFactory,syntheticMetadataKey);
                result=await sync.RunAsync(platform,catalogOnly,progress,cancellation);
            }
            catch(OperationCanceledException){RecordSyncReport(platform,catalogOnly,started,null,"cancelled",new("SYNC003","读取","本次读取已停止，已有任务保留。"));throw;}
            catch(TaskValidationException error){RecordSyncReport(platform,catalogOnly,started,null,"failed",new(error.Code,"读取",error.Message));throw;}
            catch{RecordSyncReport(platform,catalogOnly,started,null,"failed",new("SYNC004","读取","读取失败，请重试或重新登录。"));throw;}
            RecordSyncReport(platform,catalogOnly,started,result);return result;
        }
        finally{syncReportGate.Release();}
    }
}
