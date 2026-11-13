using AccessProbe;
using AccessProbe.Linux;
namespace LinuxChecks;
static class SessionChecks
{
    public static Task RunAsync()
    {
        using var root=new TestRoot();
        using(var service=new LinuxAppService(root.Path))
        {
            Check.Equal("not_saved",service.LoadPlatformViews().Single(p=>p.Id=="chaoxing").Session.State);
            var cookies=new[]{new MetadataSessionCookie("_uid","900001","i.chaoxing.com","/",true,true,null),
                new MetadataSessionCookie("outside","excluded","example.invalid","/",true,true,null)};
            service.CaptureBrowserSession(new("chaoxing","https://i.chaoxing.com/",cookies,null,null,null));
            Check.Equal("saved",service.LoadPlatformViews().Single(p=>p.Id=="chaoxing").Session.State);
            Check.Equal(1,service.GetBrowserSession("chaoxing")!.Cookies.Length);
            Check.True(service.GetBrowserSession("chaoxing")!.Cookies[0].HttpOnly);
            using(var protector=new LinuxDataProtector(root.Path))
            {
                var store=new TaskStore(Path.Combine(root.Path,"tasks.db"),protector);
                store.ConfirmPlatformSession("chaoxing",DateTimeOffset.UtcNow);
                Check.Equal("ready",service.LoadPlatformViews().Single(p=>p.Id=="chaoxing").Session.State);
                service.CaptureBrowserSession(new("chaoxing","https://i.chaoxing.com/",cookies,null,null,null));
                Check.Equal("saved",service.LoadPlatformViews().Single(p=>p.Id=="chaoxing").Session.State);
                Check.True(service.LoadPlatformViews().Single(p=>p.Id=="chaoxing").Session.CheckedAtUtc is null);
            }
            Check.Throws<TaskValidationException>(()=>service.CaptureBrowserSession(new("chaoxing","https://i.chaoxing.com/",[],null,null,null)));
            Check.Throws<TaskValidationException>(()=>service.CaptureBrowserSession(new("chaoxing","https://evil.invalid/",cookies,null,null,null)));
            Check.Equal(1,service.GetBrowserSession("chaoxing")!.Cookies.Length);
            Check.True(LinuxPlatformPolicy.Allowed("chaoxing","https://i.chaoxing.com/",false));
            Check.True(!LinuxPlatformPolicy.Allowed("chaoxing","https://evil.invalid/",false));
            Check.True(!LinuxPlatformPolicy.Allowed("chaoxing","https://i.chaoxing.com.evil.invalid/",false));
            Check.True(service.LoadPlatformViews().Single(p=>p.Id=="fif").LoginUri is null);
        }
        using(var restarted=new LinuxAppService(root.Path))
        {
            Check.Equal("900001",restarted.GetBrowserSession("chaoxing")!.Cookies[0].Value);
            restarted.ClearBrowserSession("chaoxing");
            Check.True(restarted.GetBrowserSession("chaoxing") is null);
        }
        return Task.CompletedTask;
    }
}
