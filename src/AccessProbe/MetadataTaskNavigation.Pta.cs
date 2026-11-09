using System.Security.Cryptography;
using System.Text;

namespace AccessProbe;

public static partial class MetadataTaskNavigation
{
    public static string? Pta(string setId) => MetadataPlatformParser.PtaId(setId)
        ? "https://pintia.cn/problem-sets/" + setId + "/exam/overview" : null;
    private static string? PtaIdentity(string? address)
    {
        if (!TaskRules.IsSafeLink(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.IdnHost != "pintia.cn" || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 5 || parts[1] != "problem-sets" || parts[3] != "exam" || parts[4] != "overview"
            || !MetadataPlatformParser.PtaId(parts[2]) || address != Pta(parts[2])) return null;
        return parts[2];
    }
    private static string? PtaKey(string? address) => PtaIdentity(address) is { } id
        ? "pta:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("problem-set\n" + id))) : null;
    private static bool PtaMatches(PlatformCollectedTask task) => task.CourseKind == "class" && task.Kind == "problem-set"
        && MetadataPlatformParser.PtaId(task.AccountId) && MetadataPlatformParser.PtaId(task.CourseId) && PtaIdentity(task.Link) == task.RemoteId;
}
