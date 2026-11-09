using System.Security.Cryptography;
using System.Text;

namespace AccessProbe;

public sealed partial class MetadataSynchronizer
{
    private async Task<MetadataSyncSummary> RunPtaAsync(IProgress<string>? progress, CancellationToken cancellation, bool catalogOnly)
    {
        var problems = new List<MetadataSyncProblem>(); var courses = new Dictionary<string, MetadataCourse>(StringComparer.Ordinal);
        var hiddenSets = new Dictionary<string, Dictionary<string, PtaProblemSet>>(StringComparer.Ordinal);
        var taskResults = new Dictionary<string, PlatformCollectedTask>(StringComparer.Ordinal);
        var requests = 0; var catalogPages = 0; var selected = 0; var readCount = 0; var added = 0; var updated = 0; var invalid = 0;
        var listReadCount = 0; var fallbackCourses = 0;
        var fallbackCourseIds = new HashSet<string>(StringComparer.Ordinal);
        var loginRequired = false; var stopped = false; var accountVerified = false; var covered = false; var listsCovered = false;
        MetadataResponse? lastResponse = null;
        var revision = store.LoadCourseRecording(platform.Id).Revision;
        var profile = await Read(new("profile"));
        if (profile.Outcome != "ok") { Problem(profile.Outcome, "PTA 登录检查"); return Summary(); }
        var account = MetadataPlatformParser.PtaAccount(profile.Json);
        if (account is null || profile.AccountId is not null && profile.AccountId != account)
        { Problem("account_mismatch", "PTA 账号归属"); return Summary(); }
        accountVerified = true;
        progress?.Report("正在读取 PTA 教学班级和教学题目集关联课程…");
        var visibleIds = new HashSet<string>(StringComparer.Ordinal);
        var visibleComplete = await ReadClasses();
        if (stopped || loginRequired) return Summary();
        var teachingComplete = await ReadTeachingDirectory();
        if (stopped || loginRequired) return Summary();
        covered = visibleComplete && teachingComplete;
        if (!await RecheckAccount()) return Summary();
        await Validate();
        var recording = store.ObserveRecordingCourses(platform.Id,
            courses.Values.Select(course => CourseRecordingRules.Metadata(platform.Id, account, course)).ToArray(), covered, DateTimeOffset.UtcNow, cancellation);
        await Validate();
        var selectedCourses = courses.Values.Where(course => CourseRecordingRules.IsSelected(recording,
            CourseRecordingRules.MetadataKey(platform.Id, account, course.Id, course.Kind))).ToArray(); selected = selectedCourses.Length;
        if (catalogOnly) return Summary();
        foreach (var course in selectedCourses)
        {
            if (loginRequired || stopped) break;
            progress?.Report("正在读取 PTA“" + course.Name + "”的题目集和个人答卷状态…");
            var sets = new Dictionary<string, PtaProblemSet>(StringComparer.Ordinal);
            var listComplete = await ReadCourseSets(course, sets);
            if (!listComplete || loginRequired || stopped) continue;
            if (hiddenSets.TryGetValue(course.Id, out var observed) && observed.Keys.Any(id => !sets.ContainsKey(id)))
            { Problem("conflicting_task", "课程“" + course.Name + "”的教学题目集与班级列表不一致"); continue; }
            var staged = new List<PlatformCollectedTask>(); var detailsComplete = true; var rejectCourse = false;
            foreach (var set in sets.Values)
            {
                if (loginRequired || stopped) break;
                var response = await Read(new("pta-exam") { Course = course, ProblemSetId = set.Id });
                if (response.Outcome == "login_required" || stopped)
                { Problem(response.Outcome, "课程“" + course.Name + "”的个人答卷"); break; }
                var result = response.Outcome == "ok" ? MetadataPlatformParser.PtaTask(response.Json, account, course, set)
                    : new PtaTaskResult(response.Outcome, MetadataPlatformParser.PtaUnverifiedTask(account, course, set));
                if (result.Outcome is "account_mismatch" or "task_mismatch")
                { accountVerified = false; rejectCourse = true; stopped = true; Problem(result.Outcome, "课程“" + course.Name + "”的个人答卷归属"); break; }
                if (result.Outcome != "ok") { detailsComplete = false; Problem(result.Outcome, "课程“" + course.Name + "”的个人答卷状态、时间或特殊题目集"); }
                if (result.Task is not null) staged.Add(result.Task);
            }
            if (loginRequired || stopped || rejectCourse) continue;
            if (!await RecheckAccount()) break;
            await Validate();
            var imported = store.ImportMetadataTasks(platform.Id, staged, DateTimeOffset.UtcNow, cancellation, revision);
            added += imported.Added; updated += imported.Updated;
            foreach (var task in staged) taskResults[course.Id + "\n" + task.RemoteId] = task;
            listReadCount++;
            if (fallbackCourseIds.Contains(course.Id)) fallbackCourses++;
            if (detailsComplete) readCount++;
        }
        listsCovered = covered && readCount == selected && !loginRequired && !stopped && accountVerified;
        await Validate(); return Summary();

        async Task<bool> ReadClasses()
        {
            int? expected = null; var rows = 0; var complete = false; var valid = true;
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 1; page <= MaximumPages && !stopped && !loginRequired; page++)
            {
                var response = await Read(new("pta-courses", page)); catalogPages++;
                if (response.Outcome != "ok") { Problem(response.Outcome, "教学班级分页"); break; }
                var batch = MetadataPlatformParser.PtaCatalog(response.Json, page); invalid += batch.UnsupportedRows;
                if (batch.Outcome is not ("ok" or "partial_catalog") || batch.TotalRows is null)
                { Problem(batch.Outcome, "教学班级结构"); break; }
                expected ??= batch.TotalRows;
                if (expected != batch.TotalRows || !PageUnique(response.Json, signatures)) { Problem("repeated_page", "教学班级总数或重复分页"); break; }
                foreach (var course in batch.Courses)
                {
                    if (!visibleIds.Add(course.Id)) { valid = false; Problem("conflicting_course", "教学班级重复"); }
                    if (!AddCourse(course)) valid = false;
                }
                rows += batch.Rows;
                if (batch.Outcome != "ok") { valid = false; Problem(batch.Outcome, "未识别的教学班级"); }
                if (page >= batch.TotalPages) { complete = rows == expected; break; }
                if (page == MaximumPages) Problem("page_limit", "教学班级分页上限");
            }
            return complete && valid && !stopped && !loginRequired;
        }
        async Task<bool> ReadTeachingDirectory()
        {
            int? expected = null; var rows = 0; var complete = false; var valid = true;
            var signatures = new HashSet<string>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 1; page <= MaximumPages && !stopped && !loginRequired; page++)
            {
                var response = await Read(new("pta-teaching-sets", page)); catalogPages++;
                if (response.Outcome != "ok") { Problem(response.Outcome, "教学题目集关联课程分页"); break; }
                var batch = MetadataPlatformParser.PtaSets(response.Json, page, true); invalid += batch.UnsupportedRows;
                if (batch.Outcome is not ("ok" or "partial_tasks")) { Problem(batch.Outcome, "教学题目集关联结构"); break; }
                expected ??= batch.TotalRows;
                if (expected != batch.TotalRows || !PageUnique(response.Json, signatures)) { Problem("repeated_page", "关联课程总数或重复分页"); break; }
                foreach (var set in batch.Items)
                {
                    if (!ids.Add(set.Id)) { valid = false; Problem("conflicting_task", "教学题目集分页重叠"); continue; }
                    foreach (var course in batch.CoursesBySet.GetValueOrDefault(set.Id) ?? [])
                    {
                        if (!AddCourse(course)) valid = false;
                        if (!hiddenSets.TryGetValue(course.Id, out var sets)) hiddenSets[course.Id] = sets = new(StringComparer.Ordinal);
                        sets[set.Id] = set;
                    }
                }
                rows += batch.Rows;
                if (batch.Outcome != "ok") { valid = false; Problem("partial_catalog", "部分教学题目集课程归属待核对"); }
                if (rows == expected) { complete = true; break; }
                if (page == MaximumPages) Problem("page_limit", "教学题目集关联分页上限");
            }
            return complete && valid && !stopped && !loginRequired;
        }
        async Task<bool> ReadCourseSets(MetadataCourse course, Dictionary<string, PtaProblemSet> sets)
        {
            int? expected = null; var rows = 0; var complete = false; var valid = true; var signatures = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 1; page <= MaximumPages && !stopped && !loginRequired; page++)
            {
                var response = await Read(new("pta-course-sets", page) { Course = course });
                if (response.Outcome != "ok")
                {
                    // Only a confirmed hidden class's first-page permission denial can use the complete official directory.
                    // Network, pagination, schema, account and later-page failures remain unresolved.
                    if (page == 1 && response.Outcome == "course_access_denied" && !stopped && !loginRequired
                        && visibleComplete && teachingComplete && !visibleIds.Contains(course.Id)
                        && hiddenSets.TryGetValue(course.Id, out var fallback) && fallback.Count > 0)
                    {
                        foreach (var item in fallback) sets.Add(item.Key, item.Value);
                        fallbackCourseIds.Add(course.Id); return true;
                    }
                    Problem(response.Outcome, "课程“" + course.Name + "”的题目集分页"); break;
                }
                var batch = MetadataPlatformParser.PtaSets(response.Json, page); invalid += batch.UnsupportedRows;
                if (batch.Outcome is not ("ok" or "partial_tasks")) { Problem(batch.Outcome, "课程“" + course.Name + "”的题目集列表结构"); break; }
                expected ??= batch.TotalRows;
                if (expected != batch.TotalRows || !PageUnique(response.Json, signatures)) { Problem("repeated_page", "课程“" + course.Name + "”的题目集总数或重复分页"); break; }
                foreach (var item in batch.Items) if (!sets.TryAdd(item.Id, item)) { valid = false; Problem("conflicting_task", "课程“" + course.Name + "”的题目集分页重叠"); }
                rows += batch.Rows;
                if (batch.Outcome != "ok") { valid = false; Problem(batch.Outcome, "课程“" + course.Name + "”的部分题目集字段待核对"); }
                if (rows == expected) { complete = true; break; }
                if (page == MaximumPages) Problem("page_limit", "课程“" + course.Name + "”的题目集分页上限");
            }
            return complete && valid && !stopped && !loginRequired;
        }
        bool AddCourse(MetadataCourse course)
        {
            if (courses.TryGetValue(course.Id, out var previous) && previous.Name != course.Name)
            { covered = false; Problem("conflicting_course", "教学课程名称发生变化"); return false; }
            courses[course.Id] = course; return true;
        }
        async Task<bool> RecheckAccount()
        {
            if (stopped || loginRequired) return false;
            var result = await Read(new("profile"));
            if (result.Outcome != "ok" || MetadataPlatformParser.PtaAccount(result.Json) != account)
            {
                accountVerified = false; stopped = true;
                Problem(result.Outcome == "ok" ? "account_mismatch" : result.Outcome, "保存前账号复核"); return false;
            }
            return true;
        }
        async Task Validate()
        {
            cancellation.ThrowIfCancellationRequested(); store.CheckCourseRecordingRevision(platform.Id, revision, cancellation);
            if (validateSession is not null) await validateSession(cancellation); cancellation.ThrowIfCancellationRequested();
        }
        async Task<MetadataResponse> Read(MetadataRequest request)
        {
            await Validate(); if (stopped || loginRequired) return new(loginRequired ? "login_required" : "request_limit", null);
            if (requests >= MaximumRequests) { stopped = true; Problem("request_limit", "本轮请求上限"); return new("request_limit", null); }
            requests++; var result = await reader.ReadAsync(request, cancellation); lastResponse = result; await Validate();
            if (result.Outcome == "login_required") { loginRequired = true; accountVerified = false; }
            if (result.Outcome == "rate_limited") stopped = true;
            return result;
        }
        void Problem(string outcome, string context)
        {
            if (outcome == "login_required") { loginRequired = true; accountVerified = false; }
            var code = outcome == "login_required" ? "PTA001" : outcome is "page_limit" or "request_limit" or "rate_limited" or "repeated_page"
                ? "PTA005" : outcome is "course_access_denied" or "entry_unavailable" ? "PTA006"
                : outcome.StartsWith("partial", StringComparison.Ordinal) || outcome.StartsWith("unrecognized", StringComparison.Ordinal)
                    || outcome.Contains("mismatch", StringComparison.Ordinal) || outcome.StartsWith("conflicting", StringComparison.Ordinal) ? "PTA003" : "PTA002";
            if (problems.Count >= 50 || problems.Any(problem => problem.Code == code && problem.Course == context)) return;
            problems.Add(new(code, context, context + "：" + (loginRequired ? MetadataFailureText.Describe("login_required", lastResponse?.StatusCode) + "，已有任务保留。"
                : outcome == "partial_tasks" ? "已确认的题目集保留；完成状态、个人截止时间或特殊流程需在官网核对。"
                : MetadataFailureText.Describe(outcome, lastResponse?.Outcome == outcome ? lastResponse.StatusCode : null)
                    + "；不能视为没有课程或任务，已有记录保留。")));
        }
        MetadataSyncSummary Summary() => new(platform.Id, courses.Count, readCount, selected, taskResults.Count, added, updated, loginRequired, covered, problems.ToArray())
        {
            CoverageScope = "teaching-class-problem-set-metadata", AccountVerified = accountVerified && !loginRequired,
            Requests = requests, CatalogPages = catalogPages, FullTaskListsCovered = listsCovered, UnsupportedRows = invalid,
            TasksWithDeadline = taskResults.Values.Count(task => task.DeadlineUtc is not null), TasksWithLink = taskResults.Values.Count(task => task.Link is not null),
            StatusNeedsReview = taskResults.Values.Count(task => task.State is null), TaskListCoursesRead = listReadCount,
            TeachingDirectoryFallbackCourses = fallbackCourses
        };
    }
    private static bool PageUnique(string json, HashSet<string> signatures)
        => signatures.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
}
