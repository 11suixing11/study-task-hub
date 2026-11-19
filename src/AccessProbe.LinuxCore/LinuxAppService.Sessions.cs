using AccessProbe.Mobile;
using System.Security.Cryptography;
using System.Text.Json;
namespace AccessProbe.Linux;
public sealed partial class LinuxAppService
{
    private MobileVault? sessionVault;
    private MobileVault Vault=>sessionVault??=new(store,protector);
    public IReadOnlyList<PlatformView> LoadPlatformViews()
    {
        EnsureOpen();
        return PlatformCatalog.Sites.Select(p=>
        {
            if(!CourseRecordingRules.SupportsAutomatic(p.Id))return new PlatformView(p.Id,p.Name,null,new(p.Id,"not_saved",null,null),new(),"manual",null);
            var(outcome,at)=SyncOverview(p.Id);
            return new PlatformView(p.Id,p.Name,LinuxPlatformPolicy.Login(p.Id)?.AbsoluteUri,store.LoadPlatformSessionInfo(p.Id),store.LoadCourseRecordingOverview(p.Id),outcome,at?.ToString("O"));
        }).ToArray();
    }
    private (string,DateTimeOffset?) SyncOverview(string platform)
    {
        if(platform=="chaoxing"){var s=store.LoadSettings();return(s.LastSyncOutcome,s.LastSyncAtUtc);}
        if(platform=="mooc"){var s=store.LoadMoocSettings();return(s.LastSyncOutcome,s.LastSyncAtUtc);}
        if(platform=="xuetang"){var s=store.LoadXuetangSettings();return(s.LastSyncOutcome,s.LastSyncAtUtc);}
        if(platform is "yuketang" or "changjiang"){var s=store.LoadPlatformSettings(platform);return(s.LastSyncOutcome,s.LastSyncAtUtc);}
        var m=store.LoadMetadataSettings(platform);return(m.LastSyncOutcome,m.LastSyncAtUtc);
    }
    public PlatformSessionInfo CaptureBrowserSession(BrowserSessionInput input)
    {
        EnsureOpen();
        if(input is null || input.Cookies is null || !CourseRecordingRules.SupportsAutomatic(input.PlatformId)
            || !LinuxPlatformPolicy.Allowed(input.PlatformId,input.PageUri,false))throw new TaskValidationException("SESSION001","请在对应官网登录页面完成登录。");
        var cookies=input.Cookies.Where(c=>MobileSessionRules.CookieValid(input.PlatformId,c)&&!c.IsExpired(utcNow()))
            .GroupBy(c=>c.Key,StringComparer.Ordinal).Select(g=>g.Last()).ToArray();
        if(cookies.Length==0)throw new TaskValidationException("SESSION001","尚未取得有效登录，已有会话保留。");
        if(input.Token is not null && !LinuxPlatformPolicy.MetadataDocument(input.PlatformId,input.PageUri))
            throw new TaskValidationException("SESSION001","请返回官网课程主页后保存登录。");
        var session=new MobileSession(Guid.NewGuid().ToString("N"),input.PlatformId,utcNow(),cookies,input.AccountId,input.Token,input.TokenBinding);
        if(!MobileSessionRules.IsValid(session,input.PlatformId,utcNow()))throw new TaskValidationException("SESSION001","会话字段或绑定未确认，请重新读取登录。");
        store.WriteNewLinuxSession(input.PlatformId,protector.Protect(JsonSerializer.Serialize(session)),utcNow());
        return store.LoadPlatformSessionInfo(input.PlatformId);
    }
    public MobileSession? GetBrowserSession(string platform)
    {
        EnsureOpen();
        var info=store.LoadPlatformSessionInfo(platform);
        if(!info.CanReuse)return null;
        var data=store.ReadProtectedPlatformSession(platform).Data;
        if(data is null)return null;
        try
        {
            var session=JsonSerializer.Deserialize<MobileSession>(protector.Unprotect(data));
            if(session is null||!MobileSessionRules.IsValid(session,platform,utcNow()))throw new JsonException();
            return session;
        }
        catch(Exception error)when(error is CryptographicException or JsonException)
        {throw new TaskValidationException("SESSION001","已保存登录无法读取；请重新登录或明确清除该平台登录。");}
    }
    public void ClearBrowserSession(string platform){EnsureOpen();store.ClearPlatformSession(platform);}
    public string CookieFingerprint(string platform,MetadataSessionCookie[] cookies)
    {
        if(!CourseRecordingRules.SupportsAutomatic(platform)||cookies is null)throw new TaskValidationException("SESSION001","平台会话无效。");
        return MobileSessionRules.CookieFingerprint(platform,cookies.Where(c=>MobileSessionRules.CookieValid(platform,c)&&!c.IsExpired(utcNow())));
    }
}
