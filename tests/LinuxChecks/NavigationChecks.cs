using AccessProbe;
using AccessProbe.Linux;
namespace LinuxChecks;
static class NavigationChecks
{
    public static async Task RunAsync()
    {
        using var root=new TestRoot();using var service=new LinuxAppService(root.Path);
        var input=new TaskInput("链接测试","pta","","unknown",null,null,"https://example.invalid/old",null,"","",null);
        var task=service.CreateTask(input).View.Task;
        service.EditTask(task.Id,1,input with{Link="https://example.invalid/new"});
        Check.Equal("https://example.invalid/new",(await service.ResolveTaskAsync(task.Id,CancellationToken.None)).Address);
        Check.Equal("manual",(await service.ResolveTaskAsync(task.Id,CancellationToken.None)).Destination);
        using var cts=new CancellationTokenSource();cts.Cancel();
        try{await service.ResolveTaskAsync(task.Id,cts.Token);throw new InvalidDataException("Expected cancellation");}
        catch(OperationCanceledException){Check.True(true);}
    }
}
