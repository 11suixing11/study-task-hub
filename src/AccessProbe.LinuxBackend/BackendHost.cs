using AccessProbe.Linux;
using System.Text.Json;
namespace AccessProbe.LinuxBackend;
public static class BackendHost
{
    public static Task ServeAsync(Stream input,Stream output,LinuxAppService service,CancellationToken cancellation)=>new BackendServer(output,service).RunAsync(input,cancellation);
}

sealed partial class BackendServer(Stream output,LinuxAppService service)
{
    private readonly SemaphoreSlim writeGate=new(1,1);
    private bool stopping;
    private SyncJobManager? jobManager;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,SemaphoreSlim> platformMutations=new();
    private SyncJobManager Jobs=>jobManager??=new(service,(name,payload)=>WriteAsync(new{version=1,@event=name,payload}));
    public async Task RunAsync(Stream input,CancellationToken cancellation)
    {
        var reader=new BoundedLineReader(input); var pending=new List<Task>();
        while(!stopping)
        {
            var (line,overflow)=await reader.ReadAsync(cancellation); if(line is null)break;
            int? id=null;
            try
            {
                if(overflow)throw new ProtocolError("IPC003","请求超过大小限制。");
                using var doc=JsonDocument.Parse(line);var root=doc.RootElement;
                if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("id",out var idValue)||!idValue.TryGetInt32(out var parsedId)||parsedId<1)
                    throw new ProtocolError("IPC003","请求格式无效。");
                id=parsedId;
                if(!root.TryGetProperty("version",out var version)||!version.TryGetInt32(out var v))throw new ProtocolError("IPC003","请求格式无效。");
                if(v!=1)throw new ProtocolError("IPC001","通信版本不匹配。");
                if(!root.TryGetProperty("command",out var command)||command.ValueKind!=JsonValueKind.String
                    ||!root.TryGetProperty("args",out var args)||args.ValueKind!=JsonValueKind.Object)throw new ProtocolError("IPC003","请求格式无效。");
                var operation=HandleAsync(id.Value,command.GetString()!,args.Clone(),cancellation);
                pending.RemoveAll(t=>t.IsCompleted); pending.Add(operation);
            }
            catch(ProtocolError error){await ErrorAsync(id,error.Code,error.Message);}
            catch(JsonException){await ErrorAsync(id,"IPC003","请求 JSON 无效。");}
            catch(InvalidOperationException){await ErrorAsync(id,"IPC003","请求字段无效。");}
        }
        await Task.WhenAll(pending);
        if(jobManager is not null)await jobManager.StopAllAsync();
    }
    private async Task HandleAsync(int id,string command,JsonElement args,CancellationToken cancellation)
    {
        try
        {
            object? result=command switch
            {
                "snapshot"=>service.QueryTasks(Read<TaskQuery>(args)),
                "get-task"=>service.GetTask(Text(args,"id")),
                "create-task"=>service.CreateTask(Read<TaskInput>(args)),
                "edit-task"=>service.EditTask(Text(args,"id"),Integer(args,"revision"),Read<TaskInput>(args.GetProperty("input"))),
                "set-state"=>service.SetTaskState(Text(args,"id"),Integer(args,"revision"),Text(args,"state")),
                "delete-task"=>Delete(args),
                "get-settings"=>service.LoadSettings(),"save-settings"=>SaveSettings(args),
                "shutdown"=>await ShutdownAsync(),
                _=>await DispatchExtensionAsync(command,args,cancellation)
            };
            await WriteAsync(new{version=1,id,ok=true,result});
        }
        catch(AccessProbe.TaskValidationException error){await ErrorAsync(id,error.Code,error.Message);}
        catch(ProtocolError error){await ErrorAsync(id,error.Code,error.Message);}
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {await ErrorAsync(id,"IPC003","请求参数无效。");}
        catch(OperationCanceledException){await ErrorAsync(id,"SYNC003","操作已停止，请刷新后重试。");}
        catch{await ErrorAsync(id,"IPC006","后台操作失败，请重试或重新启动后台。");}
    }
    private async Task<object?> DispatchExtensionAsync(string command,JsonElement args,CancellationToken cancellation)
    {
        switch(command)
        {
            case "platforms":return service.LoadPlatformViews();
            case "get-session":return service.GetBrowserSession(Text(args,"platform"));
            case "capture-session":
                var input=Read<BrowserSessionInput>(args);return await MutatePlatformAsync(input.PlatformId,()=>service.CaptureBrowserSession(input),cancellation);
            case "clear-session":return await MutatePlatformAsync(Text(args,"platform"),()=>ClearSession(args),cancellation);
            case "get-courses":return service.GetCourses(Text(args,"platform"));
            case "resolve-task":return await service.ResolveTaskAsync(Text(args,"id"),cancellation);
            case "take-notifications":return service.TakeDueNotifications();
            case "ack-notifications":service.AcknowledgeNotifications(Read<string[]>(args.GetProperty("ids")),args.GetProperty("success").GetBoolean());return null;
            case "release-notifications":service.ReleaseNotifications(Read<string[]>(args.GetProperty("ids")));return null;
            case "select-courses":
                var platform=Text(args,"platform");return await MutatePlatformAsync(platform,()=>service.SelectCourses(platform,args.GetProperty("revision").GetInt64(),Read<string[]>(args.GetProperty("keys")),args.GetProperty("hideUnselected").GetBoolean()),cancellation);
            case "start-sync":
                var source=Text(args,"platform");var sourceGate=PlatformGate(source);await sourceGate.WaitAsync(cancellation);
                try{return new{jobId=Jobs.Start(source,args.TryGetProperty("catalogOnly",out var catalog)&&catalog.GetBoolean())};}finally{sourceGate.Release();}
            case "cancel-sync":Jobs.Cancel(Text(args,"jobId"));return null;
            case "cookie-fingerprint":return service.CookieFingerprint(Text(args,"platform"),Read<AccessProbe.MetadataSessionCookie[]>(args.GetProperty("cookies")));
            case "platform-policy":return Policy(args);
            default:throw new ProtocolError("IPC002","后台不支持此操作。");
        }
    }
    private SemaphoreSlim PlatformGate(string platform)
    {
        if(!AccessProbe.CourseRecordingRules.SupportsAutomatic(platform))throw new AccessProbe.TaskValidationException("SESSION001","平台未接入。");
        return platformMutations.GetOrAdd(platform,_=>new(1,1));
    }
    private async Task<object?> MutatePlatformAsync(string platform,Func<object?> action,CancellationToken cancellation)
    {
        var gate=PlatformGate(platform);await gate.WaitAsync(cancellation);
        try{await Jobs.StopPlatformAsync(platform);cancellation.ThrowIfCancellationRequested();return action();}finally{gate.Release();}
    }
    private object? ClearSession(JsonElement args){service.ClearBrowserSession(Text(args,"platform"));return null;}
    private object Policy(JsonElement args)
    {
        var platform=Text(args,"platform");var address=Text(args,"address");var frame=args.TryGetProperty("frame",out var f)&&f.GetBoolean();
        return new {allowed=LinuxPlatformPolicy.Allowed(platform,address,frame),upgrade=LinuxPlatformPolicy.TryUpgrade(platform,address),metadata=LinuxPlatformPolicy.MetadataDocument(platform,address)};
    }
    private object? Delete(JsonElement args){service.DeleteTask(Text(args,"id"),Integer(args,"revision"));return null;}
    private object? SaveSettings(JsonElement args){service.SaveSettings(Read<AccessProbe.AppSettings>(args.GetProperty("settings")));return null;}
    private async Task<object?> ShutdownAsync(){stopping=true;if(jobManager is not null)await jobManager.StopAllAsync();return null;}
    private static T Read<T>(JsonElement value)=>value.Deserialize<T>(Protocol.Json)??throw new ProtocolError("IPC003","请求参数无效。");
    private static string Text(JsonElement value,string key)=>value.GetProperty(key).GetString()??throw new ProtocolError("IPC003","请求参数无效。");
    private static int Integer(JsonElement value,string key)=>value.GetProperty(key).GetInt32();
    private Task ErrorAsync(int? id,string code,string message)=>WriteAsync(new{version=1,id,ok=false,error=new{code,message}});
    private async Task WriteAsync(object envelope)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(envelope,Protocol.Json);
        if(bytes.Length>Protocol.ResponseLimit)bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,id=(int?)null,ok=false,error=new{code="IPC005",message="后台响应超过大小限制。"}},Protocol.Json);
        await writeGate.WaitAsync();
        try{await output.WriteAsync(bytes);await output.WriteAsync(new byte[]{10});await output.FlushAsync();}
        finally{writeGate.Release();}
    }
}
