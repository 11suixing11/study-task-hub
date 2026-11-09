namespace AccessProbe;

public sealed class XuetangSynchronizer(TaskStore store, IXuetangMetadataReader reader,
    Func<CancellationToken, Task>? validateSession = null)
{
    public const int MaximumPages = 100;
    public const int MaximumRequests = 1200;

    public async Task<XuetangSyncSummary> RunAsync(IProgress<string>? progress = null, CancellationToken cancellation = default, bool catalogOnly = false)
    {
        catalogOnly |= !store.LoadCourseRecording(XuetangPlatform.Id).SelectionConfirmed;
        var problems = new List<XuetangSyncProblem>(); var courses = new Dictionary<string, XuetangCourse>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal); var allTasks = new List<XuetangTask>();
        var requests = 0; var pages = 0; var duplicates = 0; var unsupported = 0;
        var coursesRead = 0; var selectedCourses = 0; var added = 0; var updated = 0;
        var loginRequired = false; var stopped = false; var covered = true;
        string? account = null;
        var revision = store.LoadCourseRecording(XuetangPlatform.Id).Revision;
        var login = await Read(XuetangPlatform.Api(XuetangPlatform.LoginCheck));
        var loginOutcome = login.Outcome == "ok" ? XuetangParser.LoginOutcome(login.Json) : login.Outcome;
        if (loginOutcome != "ok") { Problem("", loginOutcome, "登录检查"); return Summary(); }
        var profile = await Read(XuetangPlatform.Api(XuetangPlatform.Profile));
        if (profile.Outcome != "ok") { Problem("", profile.Outcome, "账号检查"); return Summary(); }
        account = XuetangParser.AccountId(profile.Json);
        if (account is null) { Problem("", "unrecognized_user", "账号归属检查"); return Summary(); }
        progress?.Report("学堂在线：正在读取正在上课、即将开课、已结课和已收起课程…");
        for (var status = 1; status <= 3 && !stopped && !loginRequired; status++)
        {
            var seenPages = new HashSet<string>(StringComparer.Ordinal); var finished = false; var rows = 0; var bucketPages = 0; int? expected = null;
            for (var page = 1; page <= MaximumPages && !stopped && !loginRequired; page++)
            {
                var response = await Read(XuetangPlatform.Api(XuetangPlatform.Courses, ("status", status.ToString()), ("page", page.ToString())));
                pages++;
                bucketPages++;
                if (response.Outcome != "ok") { covered = false; Problem("", response.Outcome, "课程分页"); break; }
                var batch = XuetangParser.Catalog(response.Json, status);
                if (batch.Outcome != "ok") { covered = false; Problem("", batch.Outcome, "课程结构"); }
                expected ??= batch.Total;
                if (expected != batch.Total) { covered = false; Problem("", "catalog_changed", "课程分页数量变化"); break; }
                duplicates += batch.DuplicateRows; unsupported += batch.UnsupportedCourses;
                var before = courses.Count; AddCourses(batch.Courses);
                var signature = string.Join("\n", batch.Courses.Select(CourseKey).Order(StringComparer.Ordinal));
                if (batch.Rows == 0)
                {
                    finished = expected is null || rows == expected;
                    if (!finished) Problem("", "premature_empty_page", "课程分页提前结束");
                    break;
                }
                if (!seenPages.Add(signature) || before == courses.Count && batch.Courses.Count > 0)
                { covered = false; Problem("", "repeated_page", "课程接口重复页面，无法确认分页结束"); break; }
                rows += batch.Rows;
                if (expected is { } total && rows >= total)
                {
                    finished = rows == total;
                    if (!finished) Problem("", "catalog_count_mismatch", "课程分页数量不一致");
                    break;
                }
            }
            if (!finished) covered = false;
            if (!finished && !stopped && !loginRequired && bucketPages == MaximumPages) Problem("", "page_limit", "课程分页上限");
        }
        if (!stopped && !loginRequired)
        {
            // The official stowed-course view has a separate, unpaged request.
            var response = await Read(XuetangPlatform.Api(XuetangPlatform.Courses, ("course_select_delete_status", "1")));
            pages++;
            if (response.Outcome != "ok") { covered = false; Problem("", response.Outcome, "已收起课程"); }
            else
            {
                var batch = XuetangParser.Catalog(response.Json, 0, true);
                duplicates += batch.DuplicateRows; unsupported += batch.UnsupportedCourses;
                if (batch.Outcome != "ok" || batch.Total is { } total && total != batch.Rows)
                { covered = false; Problem("", batch.Outcome == "ok" ? "catalog_count_mismatch" : batch.Outcome, "已收起课程结构"); }
                AddCourses(batch.Courses);
            }
        }
        if (loginRequired || stopped) return Summary();
        if (unsupported > 0) Problem("", "unsupported_courses", "多课项目尚未适配");
        await Validate();
        var recording = store.ObserveRecordingCourses(XuetangPlatform.Id,
            courses.Values.Select(course => CourseRecordingRules.Xuetang(account, course)).ToArray(), covered,
            DateTimeOffset.UtcNow, cancellation);
        store.CheckCourseRecordingRevision(XuetangPlatform.Id, revision, cancellation);
        var selected = courses.Values.Where(course => CourseRecordingRules.IsSelected(recording,
            CourseRecordingRules.XuetangKey(account, course.ClassroomId, course.ProductSign, course.CourseSign))).ToArray();
        selectedCourses = selected.Length;
        foreach (var course in catalogOnly ? [] : selected)
        {
            if (loginRequired || stopped) break;
            var problemCount = problems.Count;
            progress?.Report($"学堂在线：正在读取 {coursesRead + 1}/{selectedCourses} 门已选课程…");
            var response = await Read(CourseApi(XuetangPlatform.Chapter, course));
            if (response.Outcome != "ok") { Problem(course.Name, response.Outcome, "章节任务"); continue; }
            var scheduleResponse = await Read(CourseApi(XuetangPlatform.Schedule, course));
            if (loginRequired || stopped) break;
            var schedule = scheduleResponse.Outcome == "ok" ? XuetangParser.Schedule(scheduleResponse.Json)
                : new XuetangSchedule(scheduleResponse.Outcome, new Dictionary<string, decimal>());
            if (schedule.Outcome != "ok") Problem(course.Name, schedule.Outcome, "任务学习进度");
            var resets = new XuetangResetTimes("reset_scope_missing", new Dictionary<string, DateTimeOffset>());
            var sku = course.SkuId;
            if (sku.Length == 0)
            {
                var metadata = await Read(CourseApi(XuetangPlatform.Product, course));
                if (loginRequired || stopped) break;
                if (metadata.Outcome != "ok") Problem(course.Name, metadata.Outcome, "课程服务元数据");
                else sku = XuetangParser.ProductSkuId(metadata.Json) ?? "";
            }
            if (XuetangPlatform.IsId(sku))
            {
                var reset = await Read(XuetangPlatform.Api(XuetangPlatform.ResetDeadlines, ("sku_id", sku)));
                if (loginRequired || stopped) break;
                resets = reset.Outcome == "ok" ? XuetangParser.ResetTimes(reset.Json)
                    : new(reset.Outcome, new Dictionary<string, DateTimeOffset>());
            }
            var batch = XuetangParser.Chapter(response.Json, account, course, schedule, resets);
            if (batch.Outcome is not ("ok" or "partial_tasks")) { Problem(course.Name, batch.Outcome, "章节结构"); continue; }
            if (batch.Issues.Count > 0) Problem(course.Name, batch.Issues.Contains("reset_times_unverified") ? "reset_times_unverified" : "partial_tasks", "部分任务元数据待核对");
            duplicates += batch.DuplicateRows;
            await Validate();
            var imported = store.ImportXuetangTasks(batch.Tasks, DateTimeOffset.UtcNow, cancellation, revision);
            added += imported.Added; updated += imported.Updated; duplicates += imported.DuplicateRows;
            allTasks.AddRange(batch.Tasks);
            if (problems.Count == problemCount) coursesRead++;
        }
        await Validate();
        return Summary();

        async Task Validate()
        {
            cancellation.ThrowIfCancellationRequested();
            store.CheckCourseRecordingRevision(XuetangPlatform.Id, revision, cancellation);
            if (validateSession is not null) await validateSession(cancellation);
            cancellation.ThrowIfCancellationRequested();
        }
        async Task<XuetangResponse> Read(Uri address)
        {
            await Validate();
            if (stopped || loginRequired) return new(loginRequired ? "login_required" : "request_limit", null);
            if (requests >= MaximumRequests) { stopped = true; Problem("", "request_limit", "元数据请求上限"); return new("request_limit", null); }
            requests++;
            var value = await reader.ReadAsync(address, cancellation);
            await Validate();
            if (value.Outcome == "login_required") loginRequired = true;
            if (value.Outcome == "rate_limited") stopped = true;
            return value;
        }
        string CourseKey(XuetangCourse course) => course.ClassroomId + "\n" + course.ProductSign + "\n" + course.CourseSign;
        void AddCourses(IReadOnlyList<XuetangCourse> rows)
        {
            foreach (var course in rows)
            {
                var key = CourseKey(course);
                if (conflicts.Contains(key)) continue;
                if (courses.TryGetValue(key, out var previous))
                {
                    duplicates++;
                    if (previous.Name != course.Name || previous.Status != course.Status || previous.SkuId != course.SkuId)
                    { courses.Remove(key); conflicts.Add(key); covered = false; Problem("", "conflicting_course", "课程归属变化"); }
                    else if (course.Stowed) courses[key] = course;
                }
                else courses.Add(key, course);
            }
        }
        void Problem(string course, string outcome, string context)
        {
            if (outcome == "login_required") loginRequired = true;
            if (problems.Count >= 100) return;
            var code = outcome == "login_required" ? "XT001" : outcome is "rate_limited" or "request_limit" or "page_limit" or "repeated_page" ? "XT005"
                : outcome == "reset_times_unverified" ? "XT006" : outcome.Contains("unrecognized", StringComparison.Ordinal)
                    || outcome.Contains("partial", StringComparison.Ordinal) || outcome is "unsupported_courses" or "conflicting_course" ? "XT003" : "XT002";
            var detail = outcome == "login_required" ? "请完成官方登录；已有任务保留。"
                : outcome == "reset_times_unverified" ? "延期记录归属或结构未确认，截止时间显示待核对；任务入口保留。"
                : outcome == "unsupported_courses" ? "新版多课项目尚未覆盖，请在官网核对这些项目。"
                : outcome is "rate_limited" or "request_limit" or "page_limit" or "repeated_page" ? "本轮覆盖未确认，稍后重试；已有任务保留。"
                : "元数据读取或结构未完整确认，已有任务保留。";
            problems.Add(new(code, course, context + "：" + detail));
        }
        XuetangSyncSummary Summary() => new(courses.Count, coursesRead, allTasks.Count, added, updated, loginRequired,
            covered && account is not null && !stopped && !loginRequired, problems.ToArray())
        {
            SelectedCourses = selectedCourses, Requests = requests, CatalogPages = pages, DuplicateRows = duplicates,
            UnsupportedCourses = unsupported, TasksWithDeadline = allTasks.Count(task => task.DeadlineUtc is not null),
            TasksWithLink = allTasks.Count(task => task.Link is not null), StatusNeedsReview = allTasks.Count(task => task.State is null)
        };
    }

    private static Uri CourseApi(string path, XuetangCourse course)
        => XuetangPlatform.Api(path, ("cid", course.ClassroomId), ("sign", course.ProductSign));
}
