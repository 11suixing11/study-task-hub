using AccessProbe;
using AccessProbe.Linux;
using PlatformMetadataTestData;
namespace LinuxChecks;
static class SyncChecks
{
    public static async Task RunAsync()
    {
        using var root=new TestRoot();
        using var service=new LinuxAppService(root.Path,readerFactory:_=>new PtaFixtures.Handler());
        service.CaptureBrowserSession(new("pta","https://pintia.cn/course-class",[PtaFixtures.Cookie()],null,null,null));
        var catalog=await service.RunSyncAsync("pta",true,null,CancellationToken.None);
        Check.True(catalog.CatalogCovered,"PTA catalog fixture must be covered");
        Check.Equal(0,service.QueryTasks(new()).Total);
        var courses=service.GetCourses("pta");
        Check.Equal(2,courses.Courses.Count);
        var selection=service.SelectCourses("pta",courses.Revision,[courses.Courses.Single(c=>c.Name==PtaFixtures.Course.Name).Key],true);
        Check.True(selection.SelectionConfirmed);
        Check.Equal(1,selection.SelectedCourseKeys.Count);
        Check.Throws<TaskValidationException>(()=>service.SelectCourses("pta",courses.Revision,[],true));
        var result=await service.RunSyncAsync("pta",false,null,CancellationToken.None);
        Check.Equal(5,result.Tasks);
        Check.Equal(5,service.QueryTasks(new()).Total);
        Check.Equal(1,service.QueryTasks(new()).Summary.Completed);
        var next=service.GetCourses("pta");
        service.SelectCourses("pta",next.Revision,[],true);
        Check.Equal(0,service.QueryTasks(new()).Total);
        Check.Equal(5,service.QueryTasks(new(IncludeUnselected:true)).Total);
        Check.Equal("ready",service.LoadPlatformViews().Single(p=>p.Id=="pta").Session.State);
        Check.True(!result.LoginRequired);
        foreach(var (platform,count) in new[]{("chaoxing",1),("yuketang",1),("changjiang",1),("mooc",1),("xuetang",5),("ucampus",7),("zhihuishu",2)})
        {
            using var itemRoot=new TestRoot();
            LinuxSyncFixtures.Handler? lastReader=null;
            using var adapter=new LinuxAppService(itemRoot.Path,readerFactory:p=>lastReader=new LinuxSyncFixtures.Handler(p),syntheticMetadataKey:PlatformMetadataTestData.Fixtures.Key);
            adapter.CaptureBrowserSession(LinuxSyncFixtures.Session(platform));
            var directory=await adapter.RunSyncAsync(platform,true,null,CancellationToken.None);
            Check.True(directory.CatalogCovered,platform+" catalog coverage: "+directory.Message);
            Check.Equal(0,adapter.QueryTasks(new()).Total);
            var choices=adapter.GetCourses(platform);
            Check.Equal(1,choices.Courses.Count);
            adapter.SelectCourses(platform,choices.Revision,[choices.Courses.Single().Key],true);
            var imported=await adapter.RunSyncAsync(platform,false,null,CancellationToken.None);
            Check.True(imported.Tasks==count,platform+" expected "+count+" tasks: "+imported.Message);
            Check.Equal(count,adapter.QueryTasks(new()).Total);
            Check.True(adapter.QueryTasks(new()).Items.All(t=>t.Task.Source!=TaskSource.Manual));
            Check.True(adapter.QueryTasks(new()).Items.Any(t=>t.Task.DeadlineUtc is not null));
            if(platform is "chaoxing" or "ucampus")
            {
                var navigable=adapter.QueryTasks(new()).Items.First(t=>t.Task.CollectedNavigationKey.Length>0||t.Task.Source==TaskSource.Chaoxing);
                var opened=await adapter.ResolveTaskAsync(navigable.Task.Id,CancellationToken.None);
                Check.True(TaskRules.IsSafeLink(opened.Address));
                if(platform=="ucampus")Check.True(opened.Address.Contains("sign=synthetic-desktop-sign",StringComparison.Ordinal));
                else Check.Equal(0,lastReader!.Contents.Last().Reads);
            }
        }
        using var regressionRoot=new TestRoot();using var protector=new LinuxDataProtector(regressionRoot.Path);
        var mobile=await AccessProbe.Mobile.MobileSyntheticChecks.RunAsync(Path.Combine(regressionRoot.Path,"mobile"),protector);
        Check.Equal("passed",mobile.Outcome);Check.Equal(47,mobile.Checks);
        Check.True(!mobile.OfficialNetworkAccessed&&!mobile.ProductionDataAccessed);
        using var cancelRoot=new TestRoot();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellable=new LinuxAppService(cancelRoot.Path,readerFactory:_=>new PtaFixtures.Handler
            {BeforeReply=async(_,token)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);}});
        cancellable.CaptureBrowserSession(new("pta","https://pintia.cn/course-class",[PtaFixtures.Cookie()],null,null,null));
        var finished=new TaskCompletionSource<System.Text.Json.JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs=new AccessProbe.LinuxBackend.SyncJobManager(cancellable,(name,payload)=>
            {if(name=="job-finished")finished.TrySetResult(System.Text.Json.JsonSerializer.SerializeToElement(payload));return Task.CompletedTask;});
        var job=jobs.Start("pta",true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check.Throws<TaskValidationException>(()=>jobs.Start("pta",true));
        jobs.Cancel(job);
        var cancelled=await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check.Equal("SYNC003",cancelled.GetProperty("error").GetProperty("code").GetString());
        Check.Equal(0,cancellable.QueryTasks(new()).Total);
        await jobs.StopAllAsync();
    }
}
