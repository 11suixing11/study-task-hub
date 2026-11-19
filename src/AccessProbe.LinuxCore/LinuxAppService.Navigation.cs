namespace AccessProbe.Linux;
public sealed partial class LinuxAppService
{
    public async Task<NavigationView> ResolveTaskAsync(string id,CancellationToken cancellation)
    {
        EnsureOpen();string? opened=null;
        var navigation=new TaskNavigationService(store,ResolvePlatformAddressAsync,address=>opened=address);
        var result=await navigation.OpenAsync(id,cancellation);
        return new(opened!,result.Destination,TaskNavigationRules.OpenMessage(result));
    }
    private async Task<TaskLinkResolution> ResolvePlatformAddressAsync(Uri address,CancellationToken cancellation)
    {
        var platform=MetadataTaskNavigation.ParseUcampus(address.AbsoluteUri) is not null
            ||MetadataTaskNavigation.ParseUcampusUnit(address.AbsoluteUri) is not null?"ucampus":"chaoxing";
        GetBrowserSession(platform);
        var session=Vault.LoadForRevalidation(platform);
        if(session is null)return new("login_required","login_required");
        Task<IReadOnlyList<MetadataSessionCookie>> Cookies(Uri uri,CancellationToken token)
        {
            session=Vault.Validate(session,token);
            return Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(session.Cookies.Where(c=>!c.IsExpired(utcNow())&&c.AppliesTo(uri)).ToArray());
        }
        Task Save(Uri uri,IReadOnlyList<MetadataSessionCookie> observed,IReadOnlyList<MetadataSessionCookie> changed,CancellationToken token)
        {session=Vault.Renew(session,observed,changed,token);return Task.CompletedTask;}
        if(platform=="ucampus")
        {
            using var reader=new MetadataHttpReader(MetadataPlatform.Ucampus,Cookies,readerFactory?.Invoke(platform),saveResponseCookies:Save);
            var result=await reader.ResolveUcampusAsync(address.AbsoluteUri,cancellation);
            Vault.Validate(session,cancellation);return result;
        }
        async Task<IReadOnlyList<System.Net.Cookie>> NetCookies(Uri uri,CancellationToken token)=>(await Cookies(uri,token)).Select(c=>c.ToCookie()).ToArray();
        Task SaveNet(Uri uri,IReadOnlyList<System.Net.Cookie> observed,IReadOnlyList<ChaoxingSessionCookie> changed,CancellationToken token)=>Save(uri,
            observed.Select(c=>new MetadataSessionCookie(c.Name,c.Value,c.Domain,c.Path,c.Secure,c.HttpOnly,c.Expires==DateTime.MinValue?null:new DateTimeOffset(c.Expires.ToUniversalTime()))).ToArray(),
            changed.Select(c=>new MetadataSessionCookie(c.Name,c.Value,c.Domain,c.Path,c.Secure,c.HttpOnly,c.ExpiresUtc,c.SameSite)).ToArray(),token);
        using var chaoxing=new ChaoxingHttpReader(NetCookies,readerFactory?.Invoke(platform),saveResponseCookies:SaveNet);
        var resolved=await chaoxing.ResolveTaskAddressAsync(address,cancellation);
        Vault.Validate(session,cancellation);return resolved;
    }
}
