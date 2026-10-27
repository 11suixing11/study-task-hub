namespace AccessProbe;

public sealed class RainSynchronizer(TaskStore store, IRainMetadataReader reader, RainPlatform platform, string host)
{
    public const int MaximumRequests = 1200;

    public async Task<RainSyncSummary> RunAsync(IProgress<string>? progress = null, CancellationToken cancellation = default, bool catalogOnly = false)
    {
        platform.CheckHost(host);
        var problems = new List<RainSyncProblem>();
        var allTasks = new Dictionary<string, CollectedTask>(StringComparer.Ordinal);
        var coursesRead = 0; var added = 0; var updated = 0; var duplicates = 0; var loginRequired = false;
        var covered = false;
        var requests = 0; var stopped = false; var problemCount = 0;
        var recordingRevision = store.LoadCourseRecording(platform.Id).Revision;
        var selectedCourses = 0;
        async Task<RainResponse> ReadAsync(Uri address)
        {
            cancellation.ThrowIfCancellationRequested();
            store.CheckCourseRecordingRevision(platform.Id, recordingRevision, cancellation);
            if (requests >= MaximumRequests) { stopped = true; return new("request_limit", null); }
            requests++;
            var response = await reader.ReadAsync(address, cancellation);
            if (response.Outcome == "rate_limited") stopped = true;
            return response;
        }
        var user = await ReadAsync(RainAddressPolicy.Api(host, "/v2/api/web/userinfo"));
        if (user.Outcome != "ok")
        {
            Problem("", user.Outcome, "账号登录检查");
            return Summary(0);
        }
        var account = RainClassroomParser.AccountId(user.Json);
        if (account is null)
        {
            Problem("", "unrecognized_user", "账号归属检查");
            return Summary(0);
        }
        progress?.Report(platform.Name + "：正在读取学生课程…");
        var list = await ReadAsync(RainAddressPolicy.Api(host, "/v2/api/web/courses/list", ("identity", "2")));
        if (list.Outcome != "ok") { Problem("", list.Outcome, "课程列表"); return Summary(0); }
        var catalog = RainClassroomParser.Catalog(list.Json);
        covered = catalog.Covered; duplicates += catalog.DuplicateRows;
        if (catalog.Outcome != "ok") Problem("", catalog.Outcome, "课程列表结构或数量");
        var recording = await Task.Run(() => store.ObserveRecordingCourses(platform.Id,
            catalog.Courses.Select(course => CourseRecordingRules.Rain(platform, host, account, course)).ToArray(), covered,
            DateTimeOffset.UtcNow, cancellation), cancellation);
        store.CheckCourseRecordingRevision(platform.Id, recordingRevision, cancellation);
        var selected = catalog.Courses.Where(course => CourseRecordingRules.IsSelected(recording,
            CourseRecordingRules.RainKey(platform.Id, host, account, course.Id))).ToArray();
        selectedCourses = selected.Length;
        foreach (var original in catalogOnly ? [] : selected)
        {
            cancellation.ThrowIfCancellationRequested();
            if (loginRequired || stopped) break;
            store.CheckCourseRecordingRevision(platform.Id, recordingRevision, cancellation);
            progress?.Report($"{platform.Name}：正在读取 {coursesRead + 1}/{selectedCourses} 门已选课程…");
            var beforeProblems = problemCount;
            var detail = await ReadAsync(Api("/v2/api/web/classrooms/" + original.Id, original, ("role", "5")));
            if (detail.Outcome != "ok") { Problem(original.CourseName, detail.Outcome, "班级信息"); continue; }
            var room = RainClassroomParser.Classroom(detail.Json, original.Id, original);
            if (room is null) { Problem(original.CourseName, "classroom_mismatch", "班级归属"); continue; }
            var scheduleResponse = await ReadAsync(Api("/mooc-api/v1/lms/learn/course/schedule", room,
                CourseFields(room, ("is_h5", "true"))));
            var schedule = scheduleResponse.Outcome == "ok" ? RainClassroomParser.Schedule(scheduleResponse.Json)
                : new RainSchedule(scheduleResponse.Outcome, new Dictionary<string, System.Text.Json.JsonElement>());
            if (schedule.Outcome == "login_required" || stopped) { Problem(room.CourseName, schedule.Outcome, "任务进度"); break; }
            var roomTasks = new Dictionary<string, CollectedTask>(StringComparer.Ordinal);
            var hasLeafContent = room.HasChapterContent == true;
            var activitiesCovered = false;
            var pageFingerprints = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < 100; page++)
            {
                cancellation.ThrowIfCancellationRequested();
                var response = await ReadAsync(Api("/v2/api/web/logs/learn/" + room.Id, room,
                    ("actype", "-1"), ("page", page.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("offset", "20"), ("sort", "-1")));
                if (response.Outcome != "ok") { Problem(room.CourseName, response.Outcome, "活动列表"); break; }
                var batch = RainClassroomParser.Activities(response.Json, platform, host, account, room, schedule);
                hasLeafContent |= batch.HasLeafContent;
                if (batch.Outcome is not ("ok" or "partial_tasks")) { Problem(room.CourseName, batch.Outcome, "活动列表结构"); break; }
                AddBatch(batch, roomTasks, false, room.CourseName);
                if (batch.HasMore == false) { activitiesCovered = true; break; }
                var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(response.Json)));
                if (!pageFingerprints.Add(fingerprint)) { Problem(room.CourseName, "pagination_loop", "活动分页"); break; }
                if (page == 99) Problem(room.CourseName, "page_limit", "活动分页达到上限");
            }
            if (loginRequired) break;
            // Campus content can contain tasks absent from the activity feed. Read chapter metadata when advertised.
            if (!stopped && (hasLeafContent || room.Sign.Length > 0))
            {
                var response = await ReadAsync(Api("/mooc-api/v1/lms/learn/course/chapter", room,
                    CourseFields(room, ("show_unpublished", "0"))));
                if (response.Outcome != "ok") Problem(room.CourseName, response.Outcome, "章节任务");
                else
                {
                    var batch = RainClassroomParser.Chapter(response.Json, platform, host, account, room, schedule);
                    if (batch.Outcome is "ok" or "partial_tasks") AddBatch(batch, roomTasks, true, room.CourseName);
                    else Problem(room.CourseName, batch.Outcome, "章节结构");
                }
                if (schedule.Outcome != "ok") Problem(room.CourseName, schedule.Outcome, "任务进度");
            }
            if (loginRequired) break;
            if (roomTasks.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var imported = await Task.Run(() => store.ImportCollectedTasks(roomTasks.Values.ToArray(), DateTimeOffset.UtcNow, cancellation, recordingRevision), cancellation);
                added += imported.Added; updated += imported.Updated; duplicates += imported.DuplicateRows;
                foreach (var (key, task) in roomTasks) allTasks[key] = task;
            }
            if (activitiesCovered && problemCount == beforeProblems) coursesRead++;
        }
        store.CheckCourseRecordingRevision(platform.Id, recordingRevision, cancellation);
        return Summary(catalog.Courses.Count);

        RainSyncSummary Summary(int courses) => new(platform.Id, courses, coursesRead, allTasks.Count,
            added, updated, loginRequired, covered, problems)
        {
            TasksWithDeadline = allTasks.Values.Count(task => task.DeadlineUtc is not null),
            TasksWithLink = allTasks.Values.Count(task => task.Link is not null),
            StatusNeedsReview = allTasks.Values.Count(task => task.State is null), DuplicateRows = duplicates, Requests = requests,
            SelectedCourses = selectedCourses
        };
        void Problem(string course, string outcome, string context)
        {
            problemCount++;
            loginRequired |= outcome == "login_required";
            var code = outcome == "login_required" ? "RAIN001" : outcome is "timeout" or "network_error" or "rate_limited" or "request_limit" or "http_error" ? "RAIN002" : "RAIN003";
            var message = outcome switch
            {
                "login_required" => "需要完成官方登录；本地已有任务保留。",
                "rate_limited" => "平台请求频率受限，请稍后同步。",
                "request_limit" => "达到本轮读取上限，当前清单可能不完整；已保存任务保留，请稍后同步。",
                "network_error" or "timeout" => "网络请求失败或超时，请检查连接后重试。",
                "forbidden" => "平台拒绝访问当前课程，请核对官方页面中的权限。",
                _ => "平台返回尚未完整适配的元数据（" + outcome + "），本轮覆盖不完整。"
            };
            if (problems.Count < 250) problems.Add(new(code, course, context + "：" + message));
        }
        void AddBatch(RainTaskBatch batch, Dictionary<string, CollectedTask> destination, bool chapter, string courseName)
        {
            foreach (var issue in batch.Issues) Problem(courseName, issue, chapter ? "章节任务" : "活动任务");
            foreach (var task in batch.Tasks)
            {
                var key = TaskStore.CollectedTaskKey(task);
                if (destination.TryGetValue(key, out var existing))
                {
                    duplicates++;
                    // Chapter metadata supplies the chapter-specific link; explicit status beats a progress inference.
                    var stronger = task.StateEvidence == "explicit" || existing.StateEvidence != "explicit" ? task : existing;
                    destination[key] = task with
                    {
                        State = stronger.State, PlatformStatusText = stronger.PlatformStatusText,
                        StateEvidence = stronger.StateEvidence, StatusExplanation = stronger.StatusExplanation,
                        ProgressPercent = task.ProgressPercent ?? existing.ProgressPercent,
                        DeadlineKind = task.DeadlineUtc is not null ? task.DeadlineKind : existing.DeadlineKind,
                        DeadlineUtc = task.DeadlineUtc ?? existing.DeadlineUtc,
                        BeginUtc = task.BeginUtc ?? existing.BeginUtc,
                        TimeText = task.DeadlineUtc is not null ? task.TimeText : existing.TimeText,
                        Link = task.Link ?? existing.Link, ScoreText = task.ScoreText.Length > 0 ? task.ScoreText : existing.ScoreText
                    };
                }
                else destination.Add(key, task);
            }
        }
    }

    private Uri Api(string path, RainClassroom room, params (string Name, string Value)[] fields)
    {
        var values = fields.ToList();
        if (room.UniversityId.Length > 0) values.Add(("uv_id", room.UniversityId));
        if (room.PlatformId.Length > 0) values.Add(("platform_id", room.PlatformId));
        return RainAddressPolicy.Api(host, path, values.ToArray());
    }

    private static (string Name, string Value)[] CourseFields(RainClassroom room, (string Name, string Value) extra)
    {
        var result = new List<(string Name, string Value)> { ("cid", room.Id), ("classroom_id", room.Id), extra };
        if (room.Sign.Length > 0) result.Add(("sign", room.Sign));
        return result.ToArray();
    }
}
