using AccessProbe.Linux;
using AccessProbe.LinuxBackend;
using System.Text;
using System.Text.Json;

namespace LinuxChecks;
static class ProtocolChecks
{
    public static async Task RunAsync()
    {
        using var root=new TestRoot(); using var service=new LinuxAppService(root.Path);
        var lines="""
        {"version":1,"id":7,"command":"unknown","args":{}}
        {"version":9,"id":8,"command":"snapshot","args":{}}
        {broken:SYNTHETIC_SECRET}
        {"version":1,"id":9,"command":"snapshot","args":{"state":"all"}}
        """+"\n"+new string('x',2*1024*1024+1)+"\n"+"{\"version\":1,\"id\":10,\"command\":\"snapshot\",\"args\":{}}\n";
        using var input=new MemoryStream(Encoding.UTF8.GetBytes(lines)); using var output=new MemoryStream();
        await BackendHost.ServeAsync(input,output,service,CancellationToken.None);
        var text=Encoding.UTF8.GetString(output.ToArray()); Check.True(!text.Contains("SYNTHETIC_SECRET"));
        var replies=text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonDocument.Parse(s)).ToArray();
        Check.Equal(6,replies.Length);
        Check.Equal("IPC002",replies[0].RootElement.GetProperty("error").GetProperty("code").GetString());
        Check.Equal(7,replies[0].RootElement.GetProperty("id").GetInt32());
        Check.Equal("IPC001",replies[1].RootElement.GetProperty("error").GetProperty("code").GetString());
        Check.Equal("IPC003",replies[2].RootElement.GetProperty("error").GetProperty("code").GetString());
        Check.Equal(0,replies[3].RootElement.GetProperty("result").GetProperty("total").GetInt32());
        Check.Equal("IPC003",replies[4].RootElement.GetProperty("error").GetProperty("code").GetString());
        Check.Equal(10,replies[5].RootElement.GetProperty("id").GetInt32());
        Check.Equal(0,service.QueryTasks(new()).Total);
        foreach(var reply in replies)reply.Dispose();
    }
}
