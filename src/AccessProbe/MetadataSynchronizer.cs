namespace AccessProbe;

public sealed partial class MetadataSynchronizer(MetadataPlatform platform, TaskStore store, IPlatformMetadataReader reader,
    Func<CancellationToken, Task>? validateSession = null)
{
    public const int MaximumPages = 100;
    public const int MaximumRequests = 600;
    public async Task<MetadataSyncSummary> RunAsync(IProgress<string>? progress = null, CancellationToken cancellation = default, bool catalogOnly = false)
    {
        catalogOnly |= !store.LoadCourseRecording(platform.Id).SelectionConfirmed;
        if (platform.Id == "ucampus") return await RunUcampusAsync(progress, cancellation, catalogOnly);
        if (platform.Id == "pta") return await RunPtaAsync(progress, cancellation, catalogOnly);
        var problems = new List<MetadataSyncProblem>(); var courses = new Dictionary<string, MetadataCourse>(StringComparer.Ordinal);
        var courseConflicts = new HashSet<string>(StringComparer.Ordinal); var tasks = new Dictionary<string, PlatformCollectedTask>(StringComparer.Ordinal);
        var requests = 0; var pages = 0; var duplicates = 0; var unsupported = 0; var shared = 0; var unidentifiedExams = 0; var smartOtherTasks = 0;
        var selectedCount = 0; var readCount = 0; var added = 0; var updated = 0; var loginRequired = false; var stopped = false;
        var covered = false; var remindersCovered = false; var taskListsCovered = false; var accountVerified = false; var revision = store.LoadCourseRecording(platform.Id).Revision;
        MetadataResponse? lastResponse = null;
        if (!platform.AdaptationEnabled) { Problem("adaptation_disabled", "平台适配"); return Summary(); }
        var profile = await Read(new("profile"));
        if (profile.Outcome != "ok") { Problem(profile.Outcome, "登录检查"); return Summary(); }
        if (platform.Id != "zhihuishu") { Problem("task_contract_unverified", "旧版 U 校园课程与任务"); return Summary(); }
        var account = profile.AccountId; var profileOutcome = MetadataPlatformParser.ZhihuishuProfile(profile.Json, account);
        if (profileOutcome != "ok") { Problem(profileOutcome, "账号归属检查"); return Summary(); }
        accountVerified = true;
        progress?.Report("正在读取智慧树翻转课、共享课、智慧课程和 AI 智课目录…");
        var flipCovered = await ReadCatalog("courses", 0, "翻转课");
        var activeSharedCovered = await ReadCatalog("shared-courses", 0, "在学共享课");
        var completedSharedCovered = await ReadCatalog("shared-courses", 1, "已完成共享课");
        var activeSmartCovered = await ReadCatalog("smart-courses", 3, "进行中智慧课程");
        var completedSmartCovered = await ReadCatalog("smart-courses", 4, "已完成智慧课程");
        var agentCovered = await ReadCatalog("agent-courses", 0, "AI 智课／智能体课");
        covered = flipCovered && activeSharedCovered && completedSharedCovered && activeSmartCovered && completedSmartCovered
            && agentCovered && !stopped && !loginRequired;
        if (loginRequired || stopped) return Summary();
        await Validate(); var recording = store.ObserveRecordingCourses(platform.Id,
            courses.Values.Select(course => CourseRecordingRules.Metadata(platform.Id, account!, course)).ToArray(), covered, DateTimeOffset.UtcNow, cancellation);
        await Validate();
        var selected = courses.Values.Where(course => CourseRecordingRules.IsSelected(recording,
            CourseRecordingRules.MetadataKey(platform.Id, account!, course.Id, course.Kind))).ToArray(); selectedCount = selected.Length;
        if (catalogOnly) return Summary();
        foreach (var course in selected.Where(course => course.Kind == "smart"))
        {
            if (loginRequired || stopped) break;
            if (!MetadataTaskNavigation.IsSmartCourse(course)) { Problem("smart_version_unverified", "旧版智慧课程任务"); continue; }
            progress?.Report("正在读取智慧课程“" + course.Name + "”的未完成和已完成作业、考试…");
            if (await ReadSmartTasks(course)) readCount++;
        }
        if (selected.Any(course => course.Kind == "agent")) Problem("agent_tasks_unverified", "AI 智课／智能体课任务");

        // Reminders supplement course lists. Their disappearance never changes saved task states.
        if (courses.Values.Any(course => course.Kind == "flip"))
        {
            var reminders = await Read(new("reminders"));
            if (reminders.Outcome != "ok") Problem(reminders.Outcome, "翻转课首页提醒");
            else
            {
                var flips = courses.Values.Where(course => course.Kind == "flip").ToDictionary(course => course.Id, StringComparer.Ordinal);
                var batch = MetadataPlatformParser.ZhihuishuReminders(reminders.Json, account!, flips, DateTimeOffset.UtcNow);
                unsupported += batch.UnsupportedRows; duplicates += batch.DuplicateRows;
                if (batch.Outcome is "ok" or "partial_tasks")
                {
                    remindersCovered = batch.UnsupportedRows == 0;
                    if (batch.Outcome != "ok") Problem(batch.Outcome, "首页提醒部分字段");
                    await Import(batch.Tasks.Where(task => CourseRecordingRules.IsSelected(recording,
                        CourseRecordingRules.MetadataKey(platform.Id, account!, task.CourseId, task.CourseKind))).ToArray());
                }
                else Problem(batch.Outcome, "首页提醒结构");
            }
        }
        if (loginRequired || stopped) return Summary();
        foreach (var course in selected.Where(course => course.Kind == "flip"))
        {
            if (loginRequired || stopped) break;
            progress?.Report("正在读取翻转课“" + course.Name + "”的全部作业、考试列表…");
            var homeworkComplete = await ReadFlipTasks(course, "homework");
            var examComplete = await ReadFlipTasks(course, "exam");
            if (homeworkComplete && examComplete) readCount++;
        }
        var sharedSelected = selected.Where(course => course.Kind == "shared").ToArray();
        if (sharedSelected.Length > 0 && !loginRequired && !stopped)
        {
            var taskProfile = await Read(new("shared-profile"));
            var taskProfileOutcome = taskProfile.Outcome == "ok" ? MetadataPlatformParser.ZhihuishuSharedProfile(taskProfile.Json, account!) : taskProfile.Outcome;
            var school = ""; var examProfileValid = false;
            if (taskProfileOutcome != "ok") Problem(taskProfileOutcome, "共享课作业登录检查");
            else
            {
                var schoolResponse = await Read(new("shared-school"));
                school = schoolResponse.Outcome == "ok" ? MetadataPlatformParser.ZhihuishuSharedSchool(schoolResponse.Json, "shared-school") ?? "" : "";
                if (school.Length == 0) Problem(schoolResponse.Outcome == "ok" ? "unrecognized_school" : schoolResponse.Outcome, "共享课学校信息");
                if (!loginRequired && !stopped)
                {
                    var examProfile = await Read(new("shared-exam-profile"));
                    var examOutcome = examProfile.Outcome == "ok" ? MetadataPlatformParser.ZhihuishuSharedProfile(examProfile.Json, account!) : examProfile.Outcome;
                    examProfileValid = examOutcome == "ok";
                    if (!examProfileValid) Problem(examOutcome, "共享课考试登录检查");
                }
                foreach (var course in sharedSelected)
                {
                    if (loginRequired || stopped) break;
                    progress?.Report("正在读取共享课“" + course.Name + "”的未提交、已提交任务…");
                    var homeworkComplete = await ReadSharedTasks(course, "shared-homework", school);
                    var examComplete = false;
                    if (examProfileValid && school.Length > 0 && !loginRequired && !stopped)
                    {
                        examComplete = await ReadSharedExams(course, school);
                    }
                    if (homeworkComplete && examComplete) readCount++;
                }
            }
        }
        if (!loginRequired && !stopped && courses.Values.Any(course => course.Kind == "shared"))
        {
            var sharedResponse = await Read(new("shared-reminders"));
            var count = sharedResponse.Outcome == "ok" ? MetadataPlatformParser.SharedReminderCount(sharedResponse.Json) : null;
            if (count is { } value) shared = value;
            else Problem(sharedResponse.Outcome == "ok" ? "unrecognized_shared_reminders" : sharedResponse.Outcome, "共享课首页聚合提醒");
        }
        taskListsCovered = covered && readCount == selectedCount && !stopped && !loginRequired;
        await Validate(); return Summary();

        async Task<bool> ReadCatalog(string operation, int bucket, string context)
        {
            int? expectedPages = null; int? expectedRows = null; var seenPages = new HashSet<string>(StringComparer.Ordinal); var complete = false; var valid = true; var pageCount = 0;
            for (var page = 1; page <= MaximumPages && !stopped && !loginRequired; page++)
            {
                var response = await Read(new(operation, page, bucket)); pages++; pageCount++;
                if (response.Outcome != "ok") { Problem(response.Outcome, context + "目录"); return false; }
                var batch = operation switch
                {
                    "courses" => MetadataPlatformParser.ZhihuishuCatalog(response.Json, page),
                    "shared-courses" => MetadataPlatformParser.ZhihuishuSharedCatalog(response.Json, page, bucket),
                    "smart-courses" => MetadataPlatformParser.ZhihuishuSmartCatalog(response.Json, bucket),
                    _ => MetadataPlatformParser.ZhihuishuAgentCatalog(response.Json, page)
                };
                unsupported += batch.UnsupportedRows; duplicates += batch.DuplicateRows;
                if (batch.Outcome is not ("ok" or "partial_catalog")) { Problem(batch.Outcome, context + "结构"); return false; }
                if (batch.Outcome != "ok") { Problem(batch.Outcome, context + "部分课程字段"); valid = false; }
                expectedPages ??= batch.TotalPages;
                expectedRows ??= batch.TotalRows;
                if (expectedPages != batch.TotalPages || expectedRows != batch.TotalRows) { Problem("catalog_changed", context + "页数或总数变化"); return false; }
                var signature = string.Join('\n', batch.Courses.Select(CourseKey).Order(StringComparer.Ordinal));
                if (batch.Rows == 0 && batch.TotalPages > 0 && (page > 1 || batch.TotalPages > 1)) { Problem("premature_empty_page", context + "提前返回空页"); return false; }
                if (batch.Rows > 0 && !seenPages.Add(signature)) { Problem("repeated_page", context + "重复分页"); return false; }
                foreach (var course in batch.Courses)
                {
                    var key = CourseKey(course); if (courseConflicts.Contains(key)) continue;
                    if (courses.TryGetValue(key, out var previous))
                    {
                        duplicates++; valid = false;
                        if (previous != course) { courses.Remove(key); courseConflicts.Add(key); Problem("conflicting_course", context + "课程归属冲突"); }
                        else Problem("overlapping_page", context + "分页重叠");
                    }
                    else courses[key] = course;
                }
                if (batch.TotalPages == 0 || page == batch.TotalPages) { complete = true; break; }
            }
            if (!complete && pageCount == MaximumPages) Problem("page_limit", context + "分页上限");
            return complete && valid;
        }
        async Task<bool> ReadSmartTasks(MetadataCourse course)
        {
            var response = await Read(new("smart-task-types") { Course = course });
            var types = response.Outcome == "ok" ? MetadataPlatformParser.ZhihuishuSmartTypes(response.Json) : new(response.Outcome, []);
            if (types.Outcome != "ok") { Problem(types.Outcome, "智慧课程任务类别"); return false; }
            var other = types.Types.Where(type => type.Type is not (1 or 2)).Sum(type => type.Count);
            smartOtherTasks += other;
            if (other > 0) Problem("smart_other_tasks_unverified", "智慧课程其他学习任务（" + other + " 条）");
            var complete = true;
            foreach (var type in types.Types.Where(type => type.Type is 1 or 2))
            {
                if (loginRequired || stopped) return false;
                var kind = type.Type == 1 ? "homework" : "exam";
                var staged = new Dictionary<string, PlatformCollectedTask>(StringComparer.Ordinal); var valid = true; var rows = 0;
                for (var bucket = 0; bucket <= 1; bucket++)
                {
                    var listResponse = await Read(new("smart-" + kind, Bucket: bucket) { Course = course });
                    if (listResponse.Outcome != "ok") { Problem(listResponse.Outcome, "智慧课程" + kind + "列表"); return false; }
                    var batch = MetadataPlatformParser.ZhihuishuSmartTasks(listResponse.Json, account!, course, kind, bucket);
                    if (!Accept(batch, "智慧课程" + kind + "元数据")) return false;
                    var expected = bucket == 0 ? type.Unfinished : type.Count - type.Unfinished;
                    if (batch.Rows != expected) { Problem("task_count_changed", "智慧课程任务类别与提交列表数量不一致"); return false; }
                    rows += batch.Rows; valid &= batch.UnsupportedRows == 0 && batch.DuplicateRows == 0;
                    foreach (var task in batch.Tasks)
                        if (!staged.TryAdd(TaskKey(task), task)) { Problem("conflicting_task", "智慧课程任务在未完成和已完成列表冲突"); return false; }
                }
                if (rows != type.Count || valid && staged.Count != type.Count)
                { Problem("task_count_changed", "智慧课程任务数量发生变化"); return false; }
                await Import(staged.Values.ToArray()); complete &= valid;
            }
            return complete && !loginRequired && !stopped;
        }
        async Task<bool> ReadFlipTasks(MetadataCourse course, string kind)
        {
            var staged = new Dictionary<string, PlatformCollectedTask>(StringComparer.Ordinal); int? expectedTotal = null;
            var seenPages = new HashSet<string>(StringComparer.Ordinal); var valid = true; var finishedList = false; var pageCount = 0;
            for (var page = 1; page <= MaximumPages && !stopped && !loginRequired; page++)
            {
                var response = await Read(new("flip-" + kind, page) { Course = course }); pageCount++;
                if (response.Outcome != "ok") { Problem(response.Outcome, "翻转课" + kind + "列表"); return false; }
                var batch = MetadataPlatformParser.ZhihuishuFlipTasks(response.Json, account!, course, kind, page);
                if (!Accept(batch, "翻转课" + kind + "元数据")) return false;
                expectedTotal ??= batch.TotalRows;
                if (expectedTotal != batch.TotalRows) { Problem("task_list_changed", "翻转课任务总数变化"); return false; }
                var signature = string.Join('\n', batch.Tasks.Select(TaskKey).Order(StringComparer.Ordinal));
                if (batch.Rows > 0 && !seenPages.Add(signature) || batch.Rows == 0 && batch.TotalRows > 0)
                { Problem("repeated_page", "翻转课任务重复分页或提前空页"); return false; }
                foreach (var task in batch.Tasks)
                    if (!staged.TryAdd(TaskKey(task), task)) { Problem("conflicting_task", "翻转课任务分页重叠"); return false; }
                valid &= batch.UnsupportedRows == 0;
                if (batch.ListComplete) { finishedList = true; break; }
            }
            if (!finishedList && pageCount == MaximumPages) Problem("page_limit", "翻转课任务分页上限");
            if (!finishedList || loginRequired || stopped) return false;
            if (valid && expectedTotal != staged.Count) { Problem("task_count_changed", "翻转课任务数量不一致"); return false; }
            await Import(staged.Values.ToArray()); return valid;
        }
        async Task<bool> ReadSharedTasks(MetadataCourse course, string operation, string school)
        {
            var result = await ReadSharedBuckets(course, operation, school);
            if (!result.Read) return false;
            await Import(result.Tasks); return result.Complete;
        }
        async Task<bool> ReadSharedExams(MetadataCourse course, string school)
        {
            var response = await Read(new("shared-exam-type") { Course = course, SchoolId = school });
            var type = response.Outcome == "ok" ? MetadataPlatformParser.ZhihuishuSharedExamType(response.Json) : null;
            if (type is null) { Problem(response.Outcome == "ok" ? "unrecognized_exam_type" : response.Outcome, "共享课考试形式"); return false; }
            if (type == 1) return await ReadSharedTasks(course, "shared-exams", school);
            var result = await ReadSharedBuckets(course, type == 2 ? "shared-room-exam" : "shared-offline-exam", school);
            if (!result.Read) return false;
            var staged = result.Tasks.ToDictionary(TaskKey, StringComparer.Ordinal); var complete = result.Complete;
            if (type == 2 && !loginRequired && !stopped)
            {
                var makeupResponse = await Read(new("shared-makeup") { Course = course });
                var hasMakeup = makeupResponse.Outcome == "ok" ? MetadataPlatformParser.ZhihuishuHasMakeupExam(makeupResponse.Json) : null;
                if (hasMakeup is null)
                {
                    complete = false;
                    Problem(makeupResponse.Outcome == "ok" ? "unrecognized_makeup" : makeupResponse.Outcome, "机房课程在线补考标识");
                }
                else if (hasMakeup.Value)
                {
                    var makeup = await ReadSharedBuckets(course, "shared-exams", school);
                    complete &= makeup.Read && makeup.Complete;
                    if (makeup.Read) foreach (var row in makeup.Tasks)
                    {
                        var task = row with { ExamMode = "makeup", StatusExplanation = "官网机房课程在线补考分支返回的考试。" + row.StatusExplanation };
                        if (!staged.TryAdd(TaskKey(task), task))
                        { Problem("conflicting_task", "机房考试与在线补考的任务标识冲突"); return false; }
                    }
                }
            }
            if (loginRequired || stopped) return false;
            await Import(staged.Values.ToArray()); return complete;
        }
        async Task<(PlatformCollectedTask[] Tasks, bool Read, bool Complete)> ReadSharedBuckets(MetadataCourse course, string operation, string school)
        {
            var staged = new Dictionary<string, PlatformCollectedTask>(StringComparer.Ordinal); var complete = true;
            for (var bucket = 1; bucket <= 2 && !stopped && !loginRequired; bucket++)
            {
                var response = await Read(new(operation, Bucket: bucket) { Course = course });
                if (response.Outcome != "ok") { Problem(response.Outcome, "共享课提交状态列表"); return ([], false, false); }
                var batch = operation is "shared-room-exam" or "shared-offline-exam"
                    ? MetadataPlatformParser.ZhihuishuSchoolExam(response.Json, account!, course, operation, bucket)
                    : MetadataPlatformParser.ZhihuishuSharedTasks(response.Json, account!, course, operation, bucket, school);
                if (!Accept(batch, "共享课任务字段")) return ([], false, false);
                complete &= batch.ListComplete && batch.UnsupportedRows == 0;
                foreach (var task in batch.Tasks)
                    if (!staged.TryAdd(TaskKey(task), task)) { Problem("conflicting_task", "共享课任务在未提交和已提交列表冲突"); return ([], false, false); }
            }
            return loginRequired || stopped ? ([], false, false) : (staged.Values.ToArray(), true, complete);
        }
        bool Accept(MetadataTaskBatch batch, string context)
        {
            unsupported += batch.UnsupportedRows; duplicates += batch.DuplicateRows;
            unidentifiedExams += batch.UnidentifiedExamRows;
            if (batch.Outcome is not ("ok" or "partial_tasks")) { Problem(batch.Outcome, context); return false; }
            if (batch.Outcome != "ok") Problem(batch.Issues.Contains("shared_homework_more_pages") ? "shared_homework_more_pages"
                : batch.Issues.Contains("exam_identity_unverified") ? "exam_identity_unverified" : "partial_tasks", context);
            return true;
        }
        async Task Import(IReadOnlyList<PlatformCollectedTask> batch)
        {
            await Validate(); var imported = store.ImportMetadataTasks(platform.Id, batch, DateTimeOffset.UtcNow, cancellation, revision);
            added += imported.Added; updated += imported.Updated; duplicates += imported.DuplicateRows;
            foreach (var task in batch) tasks[TaskKey(task)] = task;
        }
        async Task Validate()
        {
            cancellation.ThrowIfCancellationRequested(); store.CheckCourseRecordingRevision(platform.Id, revision, cancellation);
            if (validateSession is not null) await validateSession(cancellation); cancellation.ThrowIfCancellationRequested();
        }
        async Task<MetadataResponse> Read(MetadataRequest request)
        {
            await Validate(); if (stopped || loginRequired) return new(loginRequired ? "login_required" : "request_limit", null);
            if (requests >= MaximumRequests) { stopped = true; Problem("request_limit", "任务读取请求上限"); return new("request_limit", null); }
            requests++; var value = await reader.ReadAsync(request, cancellation); requests += value.AdditionalRequests; lastResponse = value; await Validate();
            if (value.Outcome == "login_required") loginRequired = true; if (value.Outcome == "rate_limited") stopped = true; return value;
        }
        void Problem(string outcome, string context)
        {
            if (outcome == "login_required") loginRequired = true; if (problems.Count >= 50) return;
            var suffix = outcome == "login_required" ? "001" : outcome is "rate_limited" or "request_limit" or "page_limit" or "repeated_page" ? "005"
                : outcome is "task_contract_unverified" or "shared_homework_more_pages" or "exam_identity_unverified" or "adaptation_disabled"
                    or "smart_version_unverified" or "agent_tasks_unverified" or "smart_other_tasks_unverified" ? "006"
                : outcome.StartsWith("unrecognized", StringComparison.Ordinal) || outcome.StartsWith("partial", StringComparison.Ordinal)
                    || outcome is "conflicting_course" or "conflicting_task" or "account_mismatch" ? "003" : "002";
            var detail = outcome == "login_required" ? "登录已失效，请在官网重新登录，已有任务保留。"
                : outcome == "task_contract_unverified" ? "旧版课程、作业接口仍待核对，当前未自动导入；登录页或宣传页不作为任务数据。"
                : outcome == "adaptation_disabled" ? "此平台暂不适配，已有任务保留，可继续手动记录。"
                : outcome == "shared_homework_more_pages" ? "接口提示还有更多作业，每种提交状态目前只覆盖官网首批最多 100 条，请在官网核对剩余任务。"
                : outcome == "exam_identity_unverified" ? "官网返回考试安排，但缺少可确认的学生考试标识或 examId，未生成任务；请在官网核对学校安排。"
                : outcome == "unrecognized_makeup" ? "在线补考标识未确认，不能视为没有补考；已确认的机房考试保留，请在官网核对补考列表。"
                : outcome == "smart_other_tasks_unverified" ? "目前读取作业和考试，官网还有资源学习或其他任务未覆盖，请在课程任务列表核对。"
                : outcome is "smart_version_unverified" or "agent_tasks_unverified" ? "课程已列出，但此版本的任务接口尚未确认，当前不导入任务；请在官网核对。"
                : outcome == "course_credentials_unavailable" ? "当前账号已通过登录检查，但此栏目的凭据尚未取得或存在歧义；请打开官网课程主页后刷新。"
                : outcome is "network_error" or "timeout" or "http_error" or "unsupported_redirect" or "course_access_denied" or "unrecognized_response" or "unrecognized_user"
                    or "course_session_expired" or "session_check_unavailable"
                    ? MetadataFailureText.Describe(outcome, lastResponse?.Outcome == outcome ? lastResponse.StatusCode : null) + "；已有任务保留。"
                : "结构、分页或部分字段未完整确认；已有任务保留，请在官网核对。";
            problems.Add(new(platform.CodePrefix + suffix, "", context + "：" + detail));
        }
        MetadataSyncSummary Summary() => new(platform.Id, courses.Count, readCount, selectedCount, tasks.Count, added, updated,
            loginRequired, covered, problems.ToArray())
        {
            CoverageScope = platform.Id == "zhihuishu" ? "flip-shared-and-smart-homework-exam-lists" : platform.Id == "ucampus" ? "legacy-official-login-only" : "adaptation-disabled",
            TasksWithDeadline = tasks.Values.Count(task => task.DeadlineUtc is not null), TasksWithLink = tasks.Values.Count(task => task.Link is not null),
            StatusNeedsReview = tasks.Values.Count(task => task.State is null), UnsupportedRows = unsupported, DuplicateRows = duplicates,
            Requests = requests, CatalogPages = pages, SharedCourseReminders = shared, ReminderListCovered = remindersCovered,
            FlipCourses = courses.Values.Count(course => course.Kind == "flip"), SharedCourses = courses.Values.Count(course => course.Kind == "shared"), FullTaskListsCovered = taskListsCovered,
            SmartCourses = courses.Values.Count(course => course.Kind == "smart"), AgentCourses = courses.Values.Count(course => course.Kind == "agent"),
            SmartOtherTasks = smartOtherTasks, AccountVerified = accountVerified && !loginRequired,
            RoomExamTasks = tasks.Values.Count(task => task.ExamMode == "room"), OfflineExamTasks = tasks.Values.Count(task => task.ExamMode == "offline"),
            MakeupExamTasks = tasks.Values.Count(task => task.ExamMode == "makeup"), UnidentifiedExamRows = unidentifiedExams
        };
    }
    private static string CourseKey(MetadataCourse course) => course.Kind + "\n" + course.Id;
    private static string TaskKey(PlatformCollectedTask task) => task.CourseKind + "\n" + task.CourseId + "\n" + task.Kind + "\n" + task.RemoteId;
}
