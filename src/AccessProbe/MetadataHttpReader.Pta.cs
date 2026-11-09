namespace AccessProbe;

public sealed partial class MetadataHttpReader
{
    private static bool IsPtaRequest(MetadataRequest request)
    {
        if (request.Bucket != 0 || request.SchoolId.Length > 0) return false;
        if (request.Operation == "profile") return request.Page == 1 && request.Course is null && request.ProblemSetId.Length == 0;
        if (request.Operation is "pta-courses" or "pta-teaching-sets") return request.Course is null && request.ProblemSetId.Length == 0;
        if (!MetadataPlatformParser.PtaCourseValid(request.Course)) return false;
        return request.Operation == "pta-course-sets" ? request.ProblemSetId.Length == 0
            : request.Operation == "pta-exam" && request.Page == 1 && MetadataPlatformParser.PtaId(request.ProblemSetId);
    }
    private static Uri PtaAddress(MetadataRequest request) => request.Operation switch
    {
        "profile" => new("https://passport.pintia.cn/api/u/current"),
        "pta-courses" => new("https://pintia.cn/api/user-groups/member-visible?page=" + (request.Page - 1)
            + "&limit=" + MetadataPlatformParser.PtaCoursePageSize),
        "pta-teaching-sets" => new("https://pintia.cn/api/problem-sets?filter="
            + Uri.EscapeDataString("{\"userGroupMemberJoinType\":\"CLASS\"}") + "&page=" + (request.Page - 1)
            + "&limit=" + MetadataPlatformParser.PtaTeachingPageSize + "&orderBy=END_AT&asc=false"),
        "pta-course-sets" => new("https://pintia.cn/api/problem-sets/user-groups/" + request.Course!.Id
            + "?page=" + (request.Page - 1) + "&limit=" + MetadataPlatformParser.PtaSetPageSize + "&orderBy=END_AT"),
        "pta-exam" => new("https://pintia.cn/api/problem-sets/" + request.ProblemSetId + "/exams"),
        _ => throw new ArgumentException("Unsupported PTA metadata request.")
    };
}
