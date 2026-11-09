using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AccessProbe;

public sealed partial class MetadataHttpReader
{
    private async Task<MetadataResponse> RenewUcampusSessionAsync(MetadataResponse rejected, string expectedAccount, CancellationToken cancellation)
    {
        var sso = new Uri("https://sso.unipus.cn/sso/login");
        var ssoCookies = await readCookies(sso, cancellation);
        if (!ssoCookies.Any(cookie => cookie.Name == "TGC" && MetadataSessionStore.IsValid(platform, cookie)
            && cookie.AppliesTo(sso) && !cookie.IsExpired(DateTimeOffset.UtcNow) && cookie.Value.Length > 0)) return rejected;
        // Use the official login callback instead of the API as a CAS service; the API callback can enter repeatLogin.
        // Only GET navigation hops and their Set-Cookie renewals are used. No forms, password or account operations.
        var target = MetadataPlatform.Ucampus.Login; var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var hop = 0; hop < 8; hop++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!IsUcampusRenewalAddress(target) || !visited.Add(target.AbsoluteUri)) return new("session_check_unavailable", rejected.StatusCode);
            var response = await SendAsync(target, HttpMethod.Get, null, false, "ucampus-session-renewal", cancellation);
            if (response.Outcome == "session_document")
            {
                var restored = await SendAsync(UcampusAddress(new("profile")), HttpMethod.Get, null, true, "profile", cancellation);
                return restored.Outcome == "ok" && restored.AccountId != expectedAccount ? new("account_mismatch", restored.StatusCode) : restored;
            }
            if (response.Outcome != "session_redirect") return response;
            if (response.SessionRedirect is not { } redirect) return new("session_check_unavailable", response.StatusCode);
            if (redirect.IdnHost == "u.unipus.cn" && redirect.AbsolutePath == "/t")
            {
                var bridge = RainAddressPolicy.Query(redirect);
                if (!TaskRules.IsSafeLink(redirect.AbsoluteUri) || redirect.Fragment.Length != 0 || bridge is null || bridge.Count != 1
                    || !bridge.TryGetValue("p", out var nested) || !Uri.TryCreate(nested, UriKind.Absolute, out redirect))
                    return new("session_check_unavailable", response.StatusCode);
            }
            target = redirect;
        }
        return new("session_check_unavailable", rejected.StatusCode);
    }
    private static bool IsUcampusRenewalAddress(Uri address)
    {
        if (!TaskRules.IsSafeLink(address.AbsoluteUri) || address.Fragment.Length != 0 || RainAddressPolicy.Query(address) is not { } fields) return false;
        if (address.IdnHost == "sso.unipus.cn" && address.AbsolutePath == "/sso/login")
            return fields.Count == 1 && fields.GetValueOrDefault("service") == MetadataPlatform.Ucampus.Login.AbsoluteUri;
        if (address.IdnHost != "u.unipus.cn" || address.AbsolutePath is not ("/user/comm/login" or "/user/student" or "/user/student/index")
            || fields.GetValueOrDefault("school_id") != "" || fields.Keys.Any(key => key is not ("school_id" or "ticket"))) return false;
        return !fields.TryGetValue("ticket", out var ticket) || address.AbsolutePath == "/user/comm/login" && ticket is { Length: > 0 and <= 4096 }
            && ticket.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
    private static bool IsUcampusRequest(MetadataRequest request)
    {
        if (request.Bucket != 0 || request.SchoolId != "" || request.ProblemSetId != "") return false;
        if (IsUcampusContentOperation(request.Operation))
            return request.Page == 1 && MetadataPlatformParser.ValidUcampusContentCourse(request.Course)
                && MetadataTaskNavigation.IsId(request.AccountId)
                && (request.Operation == "ucampus-unit-progress" ? MetadataPlatformParser.UcampusContentSegment(request.UnitId) : request.UnitId.Length == 0);
        if (request.AccountId.Length > 0 || request.UnitId.Length > 0) return false;
        if (request.Operation is "profile" or "courses") return request.Page == 1 && request.Course is null;
        if (!MetadataPlatformParser.ValidUcampusCourse(request.Course)) return false;
        if (request.Operation == "ucampus-course") return request.Page == 1;
        var course = request.Course!;
        var exam = request.Operation is "ucampus-exam-entry" or "ucampus-exam";
        if (MetadataPlatformParser.UcampusFields(exam ? course.Ucampus!.ExamAddress : course.Ucampus!.HomeworkAddress, "/user/student/homework/index") is not { } fields
            || fields.Count != (exam ? 6 : 5) || exam && fields.GetValueOrDefault("type") != "2"
            || fields.GetValueOrDefault("courseId") != course.ApiCourseId || fields.GetValueOrDefault("classId") != course.Ucampus.ClassId
            || fields.GetValueOrDefault("eccId") != course.Ucampus.EccId || fields.GetValueOrDefault("school_id") != course.Ucampus.SchoolHint
            || !MetadataPlatformParser.UcampusNumber(fields.GetValueOrDefault("activationStatus"))) return false;
        return request.Operation is "ucampus-homework-entry" or "ucampus-exam-entry" ? request.Page == 1
            : request.Operation is "ucampus-homework" or "ucampus-exam" && MetadataPlatformParser.UcampusNumber(exam ? course.Ucampus.ExamListSchoolId : course.Ucampus.ListSchoolId);
    }
    private static Uri UcampusAddress(MetadataRequest request) => request.Operation switch
    {
        // The official course document uses jQuery GET with cache:false for this account check.
        "profile" => new("https://u.unipus.cn/user/data/getToken?_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        "courses" => new("https://u.unipus.cn/user/student/index"),
        "ucampus-course" => new(MetadataPlatformParser.UcampusCatalogAddress(request.Course!)),
        "ucampus-content-info" => UcampusContentAddress(request, "course"),
        "ucampus-content-catalog" => UcampusContentAddress(request, "course_catalog"),
        "ucampus-content-progress" or "ucampus-unit-progress" => UcampusContentAddress(request, "v2/course_progress"),
        "ucampus-homework-entry" => new(request.Course!.Ucampus!.HomeworkAddress),
        "ucampus-exam-entry" => new(request.Course!.Ucampus!.ExamAddress),
        "ucampus-homework" => new("https://u.unipus.cn/user/student/homework/list?length=10&school_id="
            + request.Course!.Ucampus!.ListSchoolId + "&courseId=" + request.Course.ApiCourseId + "&type=1&start=" + request.Page
            + "&newState=&state=&time=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        "ucampus-exam" => new("https://u.unipus.cn/user/student/homework/list?length=10&school_id="
            + request.Course!.Ucampus!.ExamListSchoolId + "&courseId=" + request.Course.ApiCourseId + "&eccId=" + request.Course.Ucampus.EccId
            + "&classId=" + request.Course.Ucampus.ClassId + "&type=2&start=" + request.Page + "&newState=&state=&time=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        _ => throw new ArgumentException("Unsupported metadata request.")
    };
    private static bool IsUcampusContentOperation(string operation) => operation is "ucampus-content-info" or "ucampus-content-catalog"
        or "ucampus-content-progress" or "ucampus-unit-progress";
    private static Uri UcampusContentAddress(MetadataRequest request, string operation)
    {
        var context = request.Course!.Ucampus!;
        // The official service treats + and : as literal course-id characters. IsUcampusRequest validates this path segment first.
        return new("https://ucontent.unipus.cn/course/api/" + operation + "/" + context.ContentId + "/"
            + (operation == "v2/course_progress" ? (request.UnitId.Length > 0 ? Uri.EscapeDataString(request.UnitId) + "/" : "")
                + Uri.EscapeDataString(request.AccountId) + "/" : "") + Uri.EscapeDataString(context.Version) + "/");
    }
    private static bool IsUcampusLoginRedirect(Uri target, Uri source)
    {
        if (!TaskRules.IsSafeLink(target.AbsoluteUri) || target.IdnHost != "u.unipus.cn"
            || target.AbsolutePath != "/t" || target.Fragment.Length != 0) return false;
        var bridge = RainAddressPolicy.Query(target);
        if (bridge is null || bridge.Count != 1 || !bridge.TryGetValue("p", out var value)
            || !TaskRules.IsSafeLink(value) || !Uri.TryCreate(value, UriKind.Absolute, out var login)
            || login.IdnHost != "sso.unipus.cn" || login.AbsolutePath != "/sso/login" || login.Fragment.Length != 0) return false;
        var fields = RainAddressPolicy.Query(login);
        return fields is not null && fields.TryGetValue("service", out var service) && TaskRules.IsSafeLink(service)
            && Uri.TryCreate(service, UriKind.Absolute, out var callback) && callback.IdnHost == "u.unipus.cn"
            && (callback.AbsolutePath == "/user/comm/login" || callback.AbsolutePath == source.AbsolutePath)
            && callback.Fragment.Length == 0;
    }
    public async Task<TaskLinkResolution> ResolveUcampusAsync(string address, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (platform.Id != "ucampus") return new("invalid_link", "none");
        if (rateLimited) return new("rate_limited", "none");
        try
        {
            if (MetadataTaskNavigation.ParseUcampusUnit(address) is { } unit) return await ResolveUcampusUnitAsync(unit, cancellation);
            if (MetadataTaskNavigation.ParseUcampus(address) is not { } entry) return new("invalid_link", "none");
            var accountCookies = await readCookies(new("https://u.unipus.cn/user/data/getToken"), cancellation);
            if (MetadataPlatformParser.UcampusAccount(accountCookies) != entry.Account) return new("account_mismatch", "none");
            var profile = await ReadAsync(new("profile"), cancellation);
            if (profile.Outcome != "ok") return new(profile.Outcome, "none");
            if (profile.AccountId != entry.Account) return new("account_mismatch", "none");
            var path = entry.Submitted ? "examDetail" : "chk";
            var signatureAddress = new Uri("https://u.unipus.cn/user/student/homework/" + path + "?homeworkId=" + entry.Homework + "&classId=" + entry.ClassId);
            var response = await SendAsync(signatureAddress, HttpMethod.Get, null, true, "ucampus-entry-signature", cancellation);
            response = await RecheckCourseSessionAsync(response, () => SendAsync(signatureAddress, HttpMethod.Get, null, true, "ucampus-entry-signature", cancellation), cancellation);
            if (response.Outcome != "ok") return new(response.Outcome, "none");
            using var document = JsonDocument.Parse(response.Json);
            var root = document.RootElement; var rs = MetadataPlatformParser.Property(root, "rs");
            if (MetadataPlatformParser.Text(root, "code") != "0") return new("entry_unavailable", "none");
            if (!entry.Submitted && MetadataPlatformParser.Text(rs, "code") == "2") return new("official_confirmation_required", "none");
            if (!entry.Submitted && MetadataPlatformParser.Text(rs, "code") is not ("0" or "1")) return new("unrecognized_entry", "none");
            var target = MetadataTaskNavigation.UcampusSignedAddress(entry, MetadataPlatformParser.Text(rs, "sign"), MetadataPlatformParser.Text(rs, "type"));
            return target is not null && MetadataTaskNavigation.UcampusDestinationMatches(address, target)
                ? new("ready", entry.Submitted ? "view" : "answer", target) : new("unrecognized_entry", "none");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new("timeout", "none"); }
        catch (HttpRequestException) { return new("network_error", "none"); }
        catch (Exception error) when (error is CookieException or FormatException or JsonException) { return new("unrecognized_entry", "none"); }
    }
    private async Task<TaskLinkResolution> ResolveUcampusUnitAsync(MetadataTaskNavigation.UcampusUnitLocation entry, CancellationToken cancellation)
    {
        var profile = await ReadAsync(new("profile"), cancellation);
        if (profile.Outcome != "ok") return new(profile.Outcome, "none");
        if (profile.AccountId != entry.Account) return new("account_mismatch", "none");
        var shell = await ReadAsync(new("ucampus-course") { Course = entry.Course }, cancellation);
        if (shell.Outcome != "ok") return new(shell.Outcome, "none");
        var bound = MetadataPlatformParser.UcampusCourse(shell.Json, entry.Course);
        if (bound.Outcome != "ok" || bound.Course!.Ucampus!.ContentId != entry.Course.Ucampus!.ContentId
            || bound.Course.Ucampus.Version != entry.Course.Ucampus.Version) return new("course_binding_mismatch", "none");
        var info = await ReadAsync(new("ucampus-content-info") { Course = bound.Course, AccountId = entry.Account }, cancellation);
        if (info.Outcome != "ok") return new(info.Outcome, "none");
        var catalog = await ReadAsync(new("ucampus-content-catalog") { Course = bound.Course, AccountId = entry.Account }, cancellation);
        if (catalog.Outcome != "ok") return new(catalog.Outcome, "none");
        var progress = await ReadAsync(new("ucampus-content-progress") { Course = bound.Course, AccountId = entry.Account }, cancellation);
        if (progress.Outcome != "ok") return new(progress.Outcome, "none");
        var units = MetadataPlatformParser.UcampusUnitsCatalog(catalog.Json, progress.Json, info.Json, bound.Course, entry.Account);
        if (units.Outcome != "ok" || !units.Units.Any(unit => unit.Id == entry.UnitId && unit.Leafs.Any(leaf => leaf.Route == entry.Route)))
            return new(units.Outcome == "ok" ? "unit_binding_mismatch" : units.Outcome, "none");
        var destination = MetadataTaskNavigation.UcampusUnitDestination(entry);
        return destination is not null ? new("ready", "learning-unit", destination) : new("unrecognized_entry", "none");
    }
}
