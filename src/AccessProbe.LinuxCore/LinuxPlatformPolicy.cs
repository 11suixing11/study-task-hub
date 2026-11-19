namespace AccessProbe.Linux;
public static class LinuxPlatformPolicy
{
    public static Uri? Login(string platform)=>platform switch
    {
        "chaoxing"=>new("https://passport2.chaoxing.com/login?refer="+Uri.EscapeDataString("https://i.chaoxing.com/")),
        "yuketang"=>RainPlatform.Rain.LoginUri("www.yuketang.cn"),
        "changjiang"=>RainPlatform.Changjiang.LoginUri("changjiang.yuketang.cn"),
        "mooc"=>MoocPlatform.Login,"xuetang"=>XuetangPlatform.Home,
        "ucampus" or "zhihuishu" or "pta"=>MetadataPlatform.Get(platform).Login,
        "fif"=>null,_=>throw new TaskValidationException("SESSION001","平台未接入。")
    };
    public static bool Allowed(string platform,string address,bool frame)=>platform switch
    {
        "chaoxing"=>ProbePolicy.IsAllowedNavigation(address),
        "yuketang" or "changjiang"=>RainAddressPolicy.IsNavigation(RainPlatform.Find(platform),address),
        "mooc"=>MoocPlatform.IsLoginNavigation(address),
        "xuetang"=>frame?XuetangPlatform.IsLoginFrameNavigation(address):XuetangPlatform.IsLoginNavigation(address),
        "ucampus" or "zhihuishu" or "pta"=>frame?MetadataPlatform.Get(platform).IsLoginFrameNavigation(address):MetadataPlatform.Get(platform).IsLoginNavigation(address),
        _=>false
    };
    public static string? TryUpgrade(string platform,string address)
    {
        if(!address.StartsWith("http://",StringComparison.OrdinalIgnoreCase))return null;
        var https="https://"+address[7..];return Allowed(platform,https,false)?https:null;
    }
    public static bool MetadataDocument(string platform,string address)=>platform switch
    {
        "zhihuishu"=>MetadataPlatform.Zhihuishu.IsMetadataDocument(address),
        "mooc"=>TaskRules.IsSafeLink(address)&&Uri.TryCreate(address,UriKind.Absolute,out var uri)
            &&uri.IdnHost is "www.icourse163.org" or "icourse163.org" && uri.AbsolutePath=="/home.htm",
        _=>false
    };
}
