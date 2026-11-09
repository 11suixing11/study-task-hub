namespace AccessProbe;

public sealed partial class MetadataSynchronizer
{
    private async Task<MetadataSyncSummary> RunUcampusAsync(IProgress<string>? progress, CancellationToken cancellation, bool catalogOnly)
    {
        var problems = new List<MetadataSyncProblem>(); var courses = new List<MetadataCourse>(); var tasks = new List<PlatformCollectedTask>();
        var requests = 0; var read = 0; var selected = 0; var added = 0; var updated = 0; var invalid = 0; var duplicate = 0;
        var loginRequired = false; var catalogCovered = false; var taskListsCovered = false; var stopped = false; var accountVerified = false;
        var learningCoursesRead = 0; var homeworkExamCoursesRead = 0;
        var revision = store.LoadCourseRecording(platform.Id).Revision;
        MetadataResponse? lastResponse = null;
        var profile = await Read(new("profile"));
        if (profile.Outcome != "ok") { Problem(profile.Outcome, "旧版 U 校园账号检查"); return Summary(); }
        var account = profile.AccountId;
        var profileOutcome = MetadataPlatformParser.UcampusProfile(profile.Json, account);
        if (profileOutcome != "ok") { Problem(profileOutcome, "旧版 U 校园账号归属"); return Summary(); }
        accountVerified = true;
        progress?.Report("正在读取旧版 U 校园的班课目录…");
        var response = await Read(new("courses"));
        if (response.Outcome != "ok") { Problem(response.Outcome, "旧版学生课程页"); return Summary(); }
        var catalog = MetadataPlatformParser.UcampusCatalog(response.Json);
        invalid += catalog.UnsupportedRows; duplicate += catalog.DuplicateRows;
        if (catalog.Outcome is not ("ok" or "partial_catalog")) { Problem(catalog.Outcome, "旧版课程页结构"); return Summary(); }
        catalogCovered = catalog.Outcome == "ok"; courses.AddRange(catalog.Courses);
        if (!catalogCovered) Problem(catalog.Outcome, "部分课程未识别");
        await Validate();
        var recording = store.ObserveRecordingCourses(platform.Id, courses.Select(course => CourseRecordingRules.Metadata(platform.Id, account!, course)).ToArray(),
            catalogCovered, DateTimeOffset.UtcNow, cancellation);
        await Validate();
        var selectedCourses = courses.Where(course => CourseRecordingRules.IsSelected(recording,
            CourseRecordingRules.MetadataKey(platform.Id, account!, course.Id, course.Kind))).ToArray(); selected = selectedCourses.Length;
        if (catalogOnly) return Summary();
        foreach (var course in selectedCourses)
        {
            if (loginRequired || stopped) break;
            progress?.Report("正在读取 U 校园“" + course.Name + "”的单元期限、必学进度与作业、测试…");
            var coursePage = await Read(new("ucampus-course") { Course = course });
            if (coursePage.Outcome != "ok") { Problem(coursePage.Outcome, "课程作业、测试入口"); continue; }
            var bound = MetadataPlatformParser.UcampusCourse(coursePage.Json, course);
            if (bound.Outcome != "ok") { Problem(bound.Outcome, "课程与班级归属"); continue; }
            var homeworkComplete = await ReadTasks(bound.Course!, "homework");
            var examComplete = await ReadTasks(bound.Course!, "exam");
            if (homeworkComplete && examComplete) homeworkExamCoursesRead++;
            var unitsComplete = await ReadUnits(bound.Course!);
            if (unitsComplete) learningCoursesRead++;
            if (homeworkComplete && examComplete && unitsComplete) read++;
        }
        taskListsCovered = catalogCovered && homeworkExamCoursesRead == selected && !loginRequired && !stopped;
        await Validate(); return Summary();

        async Task<bool> ReadTasks(MetadataCourse course, string kind)
        {
            if (loginRequired || stopped) return false;
            var exam = kind == "exam";
            var label = exam ? "测试" : "作业";
            var shell = await Read(new("ucampus-" + kind + "-entry") { Course = course });
            if (shell.Outcome != "ok") { Problem(shell.Outcome, "官网" + label + "列表页面"); return false; }
            var contract = exam ? MetadataPlatformParser.UcampusExamEntry(shell.Json, course) : MetadataPlatformParser.UcampusHomeworkEntry(shell.Json, course);
            if (contract.Outcome != "ok") { Problem(contract.Outcome, "官网" + label + "列表参数"); return false; }
            var staged = new Dictionary<string, PlatformCollectedTask>(StringComparer.Ordinal); var seenPages = new HashSet<string>();
            int? expectedRows = null; int? expectedPages = null; var rowsRead = 0; var complete = false; var listValid = true; var pagesRead = 0;
            for (var page = 1; page <= MaximumPages && !loginRequired && !stopped; page++)
            {
                var pageResponse = await Read(new("ucampus-" + kind, page) { Course = contract.Course }); pagesRead++;
                if (pageResponse.Outcome != "ok") { Problem(pageResponse.Outcome, label + "列表分页"); break; }
                var batch = exam ? MetadataPlatformParser.UcampusExam(pageResponse.Json, account!, contract.Course!, page)
                    : MetadataPlatformParser.UcampusHomework(pageResponse.Json, account!, contract.Course!, page);
                invalid += batch.UnsupportedRows; duplicate += batch.DuplicateRows;
                if (batch.Outcome is not ("ok" or "partial_tasks")) { Problem(batch.Outcome, label + "字段或分页结构"); break; }
                expectedRows ??= batch.TotalRows; expectedPages ??= batch.TotalPages;
                if (expectedRows != batch.TotalRows || expectedPages != batch.TotalPages) { Problem("task_list_changed", label + "列表总数变化"); break; }
                var signature = string.Join('\n', batch.Tasks.Select(task => task.RemoteId).Order(StringComparer.Ordinal));
                if (batch.Rows > 0 && !seenPages.Add(signature)) { Problem("repeated_page", label + "列表重复分页"); break; }
                var overlap = false;
                foreach (var task in batch.Tasks) if (!staged.TryAdd(task.RemoteId, task)) { overlap = true; break; }
                if (overlap) { Problem("conflicting_task", label + "列表分页重叠"); break; }
                rowsRead += batch.Rows;
                if (batch.Outcome != "ok") { listValid = false; Problem("partial_tasks", "部分" + label + "状态、时间或入口待核对"); }
                if (batch.ListComplete) { complete = true; break; }
            }
            if (!complete && pagesRead == MaximumPages) Problem("page_limit", label + "读取上限");
            if (!complete || loginRequired || stopped) return false;
            if (expectedRows != rowsRead) { Problem("task_count_changed", "任务分页总数不一致"); return false; }
            await Validate();
            var imported = store.ImportMetadataTasks(platform.Id, staged.Values.ToArray(), DateTimeOffset.UtcNow, cancellation, revision);
            added += imported.Added; updated += imported.Updated; duplicate += imported.DuplicateRows; tasks.AddRange(staged.Values);
            return listValid;
        }

        async Task<bool> ReadUnits(MetadataCourse course)
        {
            if (loginRequired || stopped) return false;
            if (!MetadataPlatformParser.ValidUcampusContentCourse(course)) { Problem("unsupported_content_scope", "单元课件版本未确认"); return false; }
            var info = await Read(new("ucampus-content-info") { Course = course, AccountId = account! });
            if (info.Outcome != "ok") { Problem(info.Outcome, "单元课件类型"); return false; }
            var catalogResponse = await Read(new("ucampus-content-catalog") { Course = course, AccountId = account! });
            if (catalogResponse.Outcome != "ok") { Problem(catalogResponse.Outcome, "单元与必学目录"); return false; }
            var progressResponse = await Read(new("ucampus-content-progress") { Course = course, AccountId = account! });
            if (progressResponse.Outcome != "ok") { Problem(progressResponse.Outcome, "单元时间限制"); return false; }
            var units = MetadataPlatformParser.UcampusUnitsCatalog(catalogResponse.Json, progressResponse.Json, info.Json, course, account!);
            if (units.Outcome != "ok") { Problem(units.Outcome, "单元目录版本与账号核对"); return false; }
            var complete = true;
            foreach (var unit in units.Units)
            {
                if (loginRequired || stopped) return false;
                var response = await Read(new("ucampus-unit-progress") { Course = course, AccountId = account!, UnitId = unit.Id });
                if (response.Outcome != "ok") { complete = false; Problem(response.Outcome, "单元必学进度"); continue; }
                var batch = MetadataPlatformParser.UcampusUnitProgress(response.Json, account!, course, units, unit);
                invalid += batch.UnsupportedRows;
                if (batch.Outcome is not ("ok" or "partial_learning_unit")) { complete = false; Problem(batch.Outcome, "单元必学项核对"); continue; }
                if (batch.Outcome != "ok") { complete = false; Problem(batch.Outcome, "单元学习时间或状态待核对"); }
                await Validate();
                var imported = store.ImportMetadataTasks(platform.Id, batch.Tasks, DateTimeOffset.UtcNow, cancellation, revision);
                added += imported.Added; updated += imported.Updated; duplicate += imported.DuplicateRows; tasks.AddRange(batch.Tasks);
            }
            return complete;
        }

        async Task Validate()
        {
            cancellation.ThrowIfCancellationRequested(); store.CheckCourseRecordingRevision(platform.Id, revision, cancellation);
            if (validateSession is not null) await validateSession(cancellation); cancellation.ThrowIfCancellationRequested();
        }
        async Task<MetadataResponse> Read(MetadataRequest request)
        {
            await Validate(); if (loginRequired || stopped) return new(loginRequired ? "login_required" : "request_limit", null);
            if (requests >= MaximumRequests) { stopped = true; Problem("request_limit", "本轮请求上限"); return new("request_limit", null); }
            requests++; var result = await reader.ReadAsync(request, cancellation); requests += result.AdditionalRequests; lastResponse = result; await Validate();
            if (result.Outcome == "login_required") loginRequired = true; if (result.Outcome == "rate_limited") stopped = true; return result;
        }
        void Problem(string outcome, string context)
        {
            if (outcome == "login_required") loginRequired = true;
            if (problems.Count >= 50) return;
            var suffix = outcome == "login_required" ? "001" : outcome is "page_limit" or "request_limit" or "rate_limited" or "repeated_page" or "catalog_limit" ? "005"
                : outcome is "unsupported_scope" or "unsupported_content_scope" ? "006" : outcome.StartsWith("unrecognized", StringComparison.Ordinal) || outcome.StartsWith("partial", StringComparison.Ordinal)
                    || outcome.Contains("mismatch", StringComparison.Ordinal) || outcome == "conflicting_task" ? "003" : "002";
            var text = outcome == "login_required" ? MetadataFailureText.Describe(outcome, lastResponse?.StatusCode) + "，已有任务保留。"
                : outcome == "partial_tasks" ? "已保存确认的任务；互评、小组或未知状态和入口需在官网核对。"
                : MetadataFailureText.Describe(outcome, lastResponse?.Outcome == outcome ? lastResponse.StatusCode : null)
                    + "；不能视为没有任务，已有任务和课程选择保留。";
            problems.Add(new("UC" + suffix, "", context + "：" + text));
        }
        MetadataSyncSummary Summary() => new(platform.Id, courses.Count, read, selected, tasks.Count, added, updated, loginRequired, catalogCovered, problems.ToArray())
        {
            CoverageScope = "legacy-class-homework-exam-and-learning-units", Requests = requests, CatalogPages = courses.Count > 0 || catalogCovered ? 1 : 0,
            AccountVerified = accountVerified && !loginRequired,
            TasksWithDeadline = tasks.Count(task => task.DeadlineUtc is not null), TasksWithLink = tasks.Count(task => task.Link is not null),
            StatusNeedsReview = tasks.Count(task => task.State is null), UnsupportedRows = invalid, DuplicateRows = duplicate, FullTaskListsCovered = taskListsCovered,
            TaskListCoursesRead = homeworkExamCoursesRead,
            LearningUnitListsCovered = catalogCovered && learningCoursesRead == selected && !loginRequired && !stopped,
            LearningUnits = tasks.Count(task => task.Kind == "unit"), LearningUnitsWithDeadline = tasks.Count(task => task.Kind == "unit" && task.DeadlineUtc is not null),
            RequiredLearningUnitsIncomplete = tasks.Count(task => task.Kind == "unit" && task.State is TaskState.Pending or TaskState.NeedsReview || task.Kind == "unit" && task.State is null)
        };
    }
}
