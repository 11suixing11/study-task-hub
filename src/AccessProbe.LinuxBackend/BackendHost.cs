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
                "shutdown"=>Shutdown(),
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
    private Task<object?> DispatchExtensionAsync(string command,JsonElement args,CancellationToken cancellation)=>throw new ProtocolError("IPC002","后台不支持此操作。");
    private object? Delete(JsonElement args){service.DeleteTask(Text(args,"id"),Integer(args,"revision"));return null;}
    private object? SaveSettings(JsonElement args){service.SaveSettings(Read<AccessProbe.AppSettings>(args.GetProperty("settings")));return null;}
    private object? Shutdown(){stopping=true;return null;}
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
