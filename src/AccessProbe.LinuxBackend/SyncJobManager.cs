using AccessProbe.Linux;
namespace AccessProbe.LinuxBackend;
public sealed class SyncJobManager(LinuxAppService service,Func<string,object,Task> emit)
{
    private readonly object gate=new();
    private readonly Dictionary<string,Job> jobs=new();
    private bool stopping;
    private sealed class Job(string id,string platform)
    {
        public string Id=id;
        public string Platform=platform;
        public CancellationTokenSource Cancellation=new(TimeSpan.FromMinutes(10));
        public TaskCompletionSource Completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public string Start(string platform,bool catalogOnly)
    {
        if(!AccessProbe.CourseRecordingRules.SupportsAutomatic(platform))throw new AccessProbe.TaskValidationException("SYNC003","该平台仅支持手动记录。");
        Job job;
        lock(gate)
        {
            if(stopping||jobs.Values.Any(j=>j.Platform==platform))throw new AccessProbe.TaskValidationException("SYNC003","该平台正在读取，请先停止当前操作。");
            job=new(Guid.NewGuid().ToString("N"),platform);jobs.Add(job.Id,job);
        }
        _=Task.Run(async()=>
        {
            object? result=null,error=null;
            try
            {
                result=await service.RunSyncAsync(platform,catalogOnly,new JobProgress(message=>
                {
                    if(!job.Cancellation.IsCancellationRequested)_=EmitSafely("progress",new{jobId=job.Id,platform,message});
                }),job.Cancellation.Token);
                job.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch(OperationCanceledException){result=null;error=new{code="SYNC003",message="读取已停止或超时；已保存任务保留。"};}
            catch(AccessProbe.TaskValidationException e){error=new{code=e.Code,message=e.Message};}
            catch{error=new{code="SYNC002",message="读取未完成，请检查网络后重试；已有任务保留。"};}
            await EmitSafely("job-finished",new{jobId=job.Id,platform,result,error});
            lock(gate){jobs.Remove(job.Id);job.Cancellation.Dispose();job.Completed.TrySetResult();}
        });
        return job.Id;
    }
    private async Task EmitSafely(string name,object payload){try{await emit(name,payload);}catch(IOException){}catch(ObjectDisposedException){}}
    private sealed class JobProgress(Action<string> report):IProgress<string>{public void Report(string value)=>report(value);}
    public void Cancel(string id){lock(gate){if(jobs.TryGetValue(id,out var job))job.Cancellation.Cancel();}}
    public async Task StopPlatformAsync(string platform)
    {
        Job[] pending;
        lock(gate){pending=jobs.Values.Where(j=>j.Platform==platform).ToArray();foreach(var job in pending)job.Cancellation.Cancel();}
        await Task.WhenAll(pending.Select(j=>j.Completed.Task));
    }
    public async Task StopAllAsync()
    {
        Job[] pending;
        lock(gate){stopping=true;pending=jobs.Values.ToArray();foreach(var job in pending)job.Cancellation.Cancel();}
        await Task.WhenAll(pending.Select(j=>j.Completed.Task));
    }
}
