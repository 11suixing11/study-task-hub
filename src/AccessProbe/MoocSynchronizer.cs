using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AccessProbe;

public sealed class MoocSynchronizer(TaskStore store, IMoocMetadataReader reader, string accountId,
    Func<CancellationToken, Task>? validateSession = null)
{
    public const int MaximumRequests = 1200;
    public const int MaximumCourses = 800;

    public async Task<MoocSyncSummary> RunAsync(IProgress<string>? progress = null, CancellationToken cancellation = default, bool catalogOnly = false)
    {
        var problems = new List<MoocSyncProblem>(); var courses = new Dictionary<string, MoocCourse>(StringComparer.Ordinal);
        var allTasks = new Dictionary<string, MoocTask>(StringComparer.Ordinal);
        var coursesRead = 0; var added = 0; var updated = 0; var duplicates = 0; var requests = 0; var pagesRead = 0;
        var loginRequired = false; var covered = false; var stopped = false; var rawCourses = 0;
        var pageSignatures = new HashSet<string>(StringComparer.Ordinal); int? totalPages = null, totalCount = null;
        var recordingRevision = store.LoadCourseRecording("mooc").Revision;
        var selectedCourses = 0;
        if (!MoocPlatform.IsId(accountId)) { Problem("", "account_unrecognized", "账号检查"); return Summary(); }
        progress?.Report("中国大学 MOOC：正在读取我的课程…");
        // courseType=1 is the current official My Courses panel; its rows include MOOC and SPOC modes.
        for (var page = 1; page <= 100; page++)
        {
            var response = await Read(MoocPlatform.CourseRpc, new Dictionary<string, string>
            { ["type"] = "30", ["p"] = page.ToString(CultureInfo.InvariantCulture), ["psize"] = "8", ["courseType"] = "1" });
            if (response.Outcome != "ok") { Problem("", response.Outcome, "课程列表"); break; }
            var batch = MoocParser.Catalog(response.Json); pagesRead++;
            if (batch.Outcome is not ("ok" or "partial_catalog")) { Problem("", batch.Outcome, "课程结构"); break; }
            foreach (var issue in batch.Issues) Problem("", issue, "课程列表");
            if (batch.PageIndex is { } observedIndex && observedIndex != page) { Problem("", "pagination_loop", "课程分页"); break; }
            if (page > 1 && (totalPages != batch.TotalPages || totalCount != batch.TotalCount))
            { Problem("", "pagination_changed", "课程分页"); break; }
            totalPages ??= batch.TotalPages; totalCount ??= batch.TotalCount;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",
                batch.Courses.Select(course => course.Id + ":" + course.TermId).Order(StringComparer.Ordinal)))));
            if (batch.RawCount > 0 && !pageSignatures.Add(fingerprint)) { Problem("", "pagination_loop", "课程分页"); break; }
            rawCourses += batch.RawCount; duplicates += batch.DuplicateRows;
            foreach (var course in batch.Courses)
                if (!courses.TryAdd(course.Id + ":" + course.TermId, course)) duplicates++;
            if (courses.Count > MaximumCourses) { Problem("", "course_limit", "课程列表"); stopped = true; break; }
            if (totalPages is null || totalCount is null) { Problem("", "pagination_unrecognized", "课程分页"); break; }
            if (totalPages > 100) { Problem("", "page_limit", "课程分页"); }
            if (page >= totalPages)
            {
                if (rawCourses != totalCount) Problem("", "pagination_inconsistent", "课程总数");
                covered = rawCourses == totalCount && problems.Count == 0;
                break;
            }
            if (batch.RawCount == 0) { Problem("", "pagination_inconsistent", "课程分页提前结束"); break; }
            if (page == 100) Problem("", "page_limit", "课程分页");
        }
        if (validateSession is not null) await validateSession(cancellation);
        var recording = await Task.Run(() => store.ObserveRecordingCourses("mooc",
            courses.Values.Take(MaximumCourses).Select(course => CourseRecordingRules.Mooc(accountId, course)).ToArray(), covered,
            DateTimeOffset.UtcNow, cancellation), cancellation);
        store.CheckCourseRecordingRevision("mooc", recordingRevision, cancellation);
        var selected = courses.Values.Take(MaximumCourses).Where(course => CourseRecordingRules.IsSelected(recording,
            CourseRecordingRules.MoocKey(accountId, course.Id, course.TermId))).ToArray();
        selectedCourses = selected.Length;
        foreach (var course in catalogOnly ? [] : selected)
        {
            cancellation.ThrowIfCancellationRequested();
            if (stopped || loginRequired) break;
            store.CheckCourseRecordingRevision("mooc", recordingRevision, cancellation);
            progress?.Report($"中国大学 MOOC：正在读取 {coursesRead + 1}/{selectedCourses} 门已选课程的任务…");
            // Enrolled students use this endpoint; the public term DTO omits their current submission state.
            var response = await Read(MoocPlatform.LearnedTermRpc, new Dictionary<string, string> { ["termId"] = course.TermId });
            var term = response.Outcome == "ok" ? MoocParser.Term(response.Json, accountId, course) : new MoocTaskBatch(response.Outcome, [], 0, []);
            if (term.Outcome is not ("ok" or "partial_tasks")) Problem(course.Name, term.Outcome, "学期任务结构");
            if (stopped || loginRequired) break;
            var examResponse = await Read(MoocPlatform.NewExamRpc, new Dictionary<string, string> { ["termId"] = course.TermId });
            var newExams = examResponse.Outcome == "ok" ? MoocParser.NewExams(examResponse.Json, accountId, course)
                : new MoocTaskBatch(examResponse.Outcome, [], 0, []);
            if (newExams.Outcome is not ("ok" or "partial_tasks")) Problem(course.Name, newExams.Outcome, "新版考试结构");
            if (loginRequired) break;
            var batch = MoocParser.MergeTaskBatches(term, newExams);
            foreach (var issue in batch.Issues) Problem(course.Name, issue, "任务字段");
            duplicates += batch.DuplicateRows;
            if (validateSession is not null) await validateSession(cancellation);
            cancellation.ThrowIfCancellationRequested();
            var imported = await Task.Run(() => store.ImportMoocTasks(batch.Tasks, DateTimeOffset.UtcNow, cancellation, recordingRevision), cancellation);
            added += imported.Added; updated += imported.Updated; duplicates += imported.DuplicateRows;
            foreach (var task in batch.Tasks) allTasks[TaskStore.MoocTaskKey(task)] = task;
            if (batch.Outcome == "ok") coursesRead++;
        }
        store.CheckCourseRecordingRevision("mooc", recordingRevision, cancellation);
        return Summary();

        async Task<MoocResponse> Read(string path, IReadOnlyDictionary<string, string> fields)
        {
            cancellation.ThrowIfCancellationRequested();
            store.CheckCourseRecordingRevision("mooc", recordingRevision, cancellation);
            if (requests >= MaximumRequests) { stopped = true; return new("request_limit", null); }
            requests++;
            var response = await reader.ReadAsync(path, fields, cancellation);
            if (response.Outcome == "rate_limited") stopped = true;
            return response;
        }
        MoocSyncSummary Summary() => new(courses.Count, coursesRead, allTasks.Count, added, updated, loginRequired, covered, problems)
        {
            TasksWithDeadline = allTasks.Values.Count(task => task.DeadlineUtc is not null),
            TasksWithLink = allTasks.Values.Count(task => task.Link is not null), StatusNeedsReview = allTasks.Values.Count(task => task.State is null),
            DuplicateRows = duplicates, Requests = requests, CatalogPages = pagesRead, SpocCourses = courses.Values.Count(course => course.IsSpoc),
            NewExamTasks = allTasks.Values.Count(task => task.Kind == "exam_new"), SelectedCourses = selectedCourses
        };
        void Problem(string course, string outcome, string context)
        {
            loginRequired |= outcome == "login_required";
            var code = outcome == "login_required" ? "MOOC001"
                : outcome is "timeout" or "network_error" or "http_error" or "rate_limited" or "request_limit" ? "MOOC002" : "MOOC003";
            var message = outcome switch
            {
                "login_required" => "官方会话已失效，请重新连接登录；已有任务保留。",
                "csrf_rejected" => "官方拒绝当前请求的会话校验，请在连接窗口刷新官方登录后重试。",
                "rate_limited" => "官方限制请求频率，本轮已停止，请稍后重试。",
                "network_error" or "timeout" => "网络请求失败或超时，已有任务保留。",
                "pagination_loop" or "pagination_changed" or "pagination_inconsistent" => "课程分页发生重复、变化或数量不一致，本轮覆盖不完整。",
                "page_limit" or "course_limit" or "request_limit" => "达到本轮读取上限，清单可能不完整，请查看官方课程列表。",
                _ => "返回的元数据暂未完整适配（" + outcome + "），已保留可确认的任务。"
            };
            if (problems.Count < 250) problems.Add(new(code, course, context + "：" + message));
        }
    }
}
