using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessProbe;
using U = PlatformMetadataTestData.UcampusFixtures;

internal static partial class MetadataPlatformChecks
{
    private static async Task UcampusUnitChecksAsync()
    {
        var now = PlatformMetadataTestData.Fixtures.Now; var course = U.BoundCourse();
        var catalog = MetadataPlatformParser.UcampusUnitsCatalog(U.LearningCatalog(), U.LearningCourseProgress(now), U.LearningContentInfo(), course, U.Account);
        Check(catalog is { Outcome: "ok", Units.Count: 3 }, "unit tree binds ordinary and linked learning checkpoints with direct chapter paths");
        var unit = catalog.Units[0];
        Check(unit.Leafs.Count == 3 && unit.Micros.Single() == "u1/u1n1", "nested nodes do not invent additional micro progress");
        var result = MetadataPlatformParser.UcampusUnitProgress(U.LearningProgress("u1", now), U.Account, course, catalog, unit);
        var row = result.Tasks.Single();
        Check(result.Outcome == "ok" && row.State == TaskState.Pending && row.ProgressPercent == 50 && row.DeadlineUtc == now.AddHours(6),
            "required linked checkpoint pass two remains incomplete even when pass2 is one");
        Check(row.TimeText.Contains("单元截止", StringComparison.Ordinal) && row.LearningEvidence.Contains("start_time", StringComparison.Ordinal)
            && !row.LearningEvidence.Contains(U.Account, StringComparison.Ordinal), "unit raw status and Unix-seconds date evidence are preserved without account");
        Check(MetadataTaskNavigation.Matches(row) && MetadataTaskNavigation.ParseUcampusUnit(row.Link) is { UnitId: "u1", Route: "u1/u1n1/u1n2/u1g2" },
            "unit navigation selects an unfinished required checkpoint");
        var finished = MetadataPlatformParser.UcampusUnitProgress(U.LearningProgress("u1", now, firstPass: 1, secondPass: 1), U.Account, course, catalog, unit).Tasks.Single();
        Check(finished.State == TaskState.Completed && finished.ProgressPercent == 100, "all required leaves complete regardless of unfinished electives or unit aggregate");
        var textProgress = JsonNode.Parse(U.LearningProgress("u1", now))!; textProgress["rt"]!["leafs"]!["u1g2"]!["tab_type"] = "text";
        Check(MetadataPlatformParser.UcampusUnitProgress(textProgress.ToJsonString(), U.Account, course, catalog, unit).Tasks.Single()
            is { State: TaskState.Pending, ProgressPercent: 50 }, "official progress text fallback does not discard known catalog checkpoint or required status");
        var early = MetadataPlatformParser.UcampusUnitProgress(U.LearningProgress("u1", now, firstPass: 1, secondPass: 0, earlyDeadline: now.AddHours(-2)), U.Account, course, catalog, unit).Tasks.Single();
        Check(early.DeadlineUtc == now.AddHours(6), "completed checkpoint's expired date cannot outrank unfinished required content");
        early = MetadataPlatformParser.UcampusUnitProgress(U.LearningProgress("u1", now, firstPass: 0, secondPass: 0, earlyDeadline: now.AddMinutes(30)), U.Account, course, catalog, unit).Tasks.Single();
        Check(early.DeadlineUtc == now.AddMinutes(30) && early.TimeText.Contains("未完必学最早截止", StringComparison.Ordinal), "earliest pending required date is selected inside unit window");
        var elective = MetadataPlatformParser.UcampusUnitProgress(U.LearningProgress("u1", now, required: false), U.Account, course, catalog, unit).Tasks.Single();
        Check(elective.State == TaskState.Ignored && elective.ProgressPercent is null && elective.DeadlineUtc is not null, "limited elective unit retains its date without creating mandatory work");
        foreach (var mode in new[] { "foreign-account", "foreign-content", "foreign-unit", "foreign-version", "missing-leaf", "extra-leaf", "missing-micro", "extra-micro", "wrong-tab", "missing-required", "duplicate-leaf" })
        {
            var body = JsonNode.Parse(U.LearningProgress("u1", now))!; var rt = body["rt"]!;
            switch (mode)
            {
                case "foreign-account": rt["open_id"] = "other-account"; break;
                case "foreign-content": rt["tutorialId"] = "other-content"; break;
                case "foreign-unit": rt["unit_id"] = "u2"; break;
                case "foreign-version": rt["publish_version"] = "101"; break;
                case "missing-leaf": rt["leafs"]!.AsObject().Remove("u1g2"); break;
                case "extra-leaf": rt["leafs"]!["foreign"] = rt["leafs"]!["u1g1"]!.DeepClone(); break;
                case "missing-micro": rt["micros"]!.AsObject().Clear(); break;
                case "extra-micro": rt["micros"]!["u1/u1n1/u1n2"] = rt["micros"]!["u1/u1n1"]!.DeepClone(); break;
                case "wrong-tab": rt["leafs"]!["u1g2"]!["tab_type"] = "unknown"; break;
                case "missing-required": rt["leafs"]!["u1g2"]!["strategies"]!.AsObject().Remove("required"); break;
            }
            var json = body.ToJsonString(); if (mode == "duplicate-leaf") json = json.Replace("\"leafs\":{", "\"leafs\":{\"u1g1\":{},", StringComparison.Ordinal);
            Check(MetadataPlatformParser.UcampusUnitProgress(json, U.Account, course, catalog, unit).Tasks.Count == 0, "unbound or incomplete progress does not overwrite unit history: " + mode);
        }
        foreach (var field in new[] { "start_time", "end_time" })
        foreach (var value in new JsonNode?[] { JsonValue.Create("invalid"), JsonValue.Create(now.ToUnixTimeMilliseconds()), JsonValue.Create(-1), null })
        {
            var body = JsonNode.Parse(U.LearningProgress("u1", now))!; var strategy = body["rt"]!["leafs"]!["u1g2"]!["strategies"]!;
            if (value is null) strategy.AsObject().Remove(field); else strategy[field] = value.DeepClone();
            var invalid = MetadataPlatformParser.UcampusUnitProgress(body.ToJsonString(), U.Account, course, catalog, unit);
            Check(invalid is { Outcome: "partial_learning_unit", Tasks.Count: 1 } && invalid.Tasks[0].DeadlineUtc is null && !invalid.Tasks[0].NoDeadline,
                "unknown or millisecond unit dates cannot be promoted to valid or unrestricted");
        }
        var unknownPass = JsonNode.Parse(U.LearningProgress("u1", now))!; unknownPass["rt"]!["leafs"]!["u1g2"]!["state"]!["pass"] = 99;
        var unconfirmedProgress = MetadataPlatformParser.UcampusUnitProgress(unknownPass.ToJsonString(), U.Account, course, catalog, unit).Tasks.Single();
        Check(unconfirmedProgress is { State: null, ProgressPercent: null },
            "unknown completion remains reviewable even when pass2 says done");
        var unlimited = JsonNode.Parse(U.LearningCourseProgress(now))!; unlimited["rt"]!["units"]!["u1"]!["strategies"] = JsonSerializer.SerializeToNode(U.LearningWindow(null, null));
        var unlimitedCatalog = MetadataPlatformParser.UcampusUnitsCatalog(U.LearningCatalog(), unlimited.ToJsonString(), U.LearningContentInfo(), course, U.Account);
        Check(MetadataPlatformParser.UcampusUnitProgress(U.LearningProgress("u1", now, unlimited: true), U.Account, course, unlimitedCatalog, unlimitedCatalog.Units[0]).Tasks.Single()
            is { NoDeadline: true, DeadlineUtc: null, State: TaskState.Pending }, "explicit null windows alone establish an unrestricted required unit");
        var missingUnitTime = JsonNode.Parse(U.LearningCourseProgress(now))!;
        missingUnitTime["rt"]!["units"]!["u1"]!["strategies"]!.AsObject().Remove("end_time");
        var uncertainCatalog = MetadataPlatformParser.UcampusUnitsCatalog(U.LearningCatalog(), missingUnitTime.ToJsonString(), U.LearningContentInfo(), course, U.Account);
        var uncertainUnit = MetadataPlatformParser.UcampusUnitProgress(U.LearningProgress("u1", now), U.Account, course, uncertainCatalog, uncertainCatalog.Units[0]).Tasks.Single();
        Check(!uncertainUnit.NoDeadline && uncertainUnit.DeadlineUtc is null && uncertainUnit.TimeText.Contains("单元截止：待核对", StringComparison.Ordinal),
            "missing unit time is visibly unconfirmed rather than unrestricted");
        foreach (var info in new[] { U.LearningContentInfo(type: "3"), U.LearningContentInfo(type: "5"), U.LearningContentInfo().Replace("100", "101") })
            Check(MetadataPlatformParser.UcampusUnitsCatalog(U.LearningCatalog(), U.LearningCourseProgress(now), info, course, U.Account).Outcome != "ok", "special course families or drifting release do not borrow ordinary unit route");

        var directory = Path.Combine(Path.GetTempPath(), "ucampus-unit-checks-" + Guid.NewGuid().ToString("N")); var store = new TaskStore(Path.Combine(directory, "tasks.db"));
        MetadataResponse? LearningResponse(MetadataRequest request) => request.Operation switch {
            "ucampus-content-info" => new("ok", 200, U.LearningContentInfo()), "ucampus-content-catalog" => new("ok", 200, U.LearningCatalog()),
            "ucampus-content-progress" => new("ok", 200, U.LearningCourseProgress(now)), "ucampus-unit-progress" => new("ok", 200, U.LearningProgress(request.UnitId, now)), _ => null };
        var reader = new U.Reader { Response = LearningResponse };
        var pending = await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync();
        Check(pending.Tasks == 0 && reader.Requests.Count == 2, "no unit metadata requested before selecting courses");
        var recording = store.LoadCourseRecording("ucampus"); store.SaveCourseRecording("ucampus", true, [], true, recording.Revision, now);
        var summary = await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync();
        Check(summary is { LearningUnits: 3, LearningUnitsWithDeadline: 3, RequiredLearningUnitsIncomplete: 2, LearningUnitListsCovered: true, Tasks: U.TaskCount + 3 },
            "selected course imports all unit dates and required progress together with existing assignments");
        var units = store.LoadTasks().Where(task => MetadataTaskNavigation.ParseUcampusUnit(task.Link) is not null).ToArray();
        var order = units.OrderBy(task => TaskRules.IsActive(task) ? 0 : 1).ThenBy(task => TaskRules.Priority(task, now)).ThenBy(task => task.DeadlineUtc).ToArray();
        Check(MetadataTaskNavigation.ParseUcampusUnit(order[0].Link)!.UnitId == "u2" && order[2].State == TaskState.Completed,
            "home ordering prioritizes urgent unfinished unit and places finished past-deadline unit after it");
        var saved = units.Single(task => MetadataTaskNavigation.ParseUcampusUnit(task.Link)!.UnitId == "u1");
        store.ImportMetadataTasks("ucampus", [finished], now);
        store.ImportMetadataTasks("ucampus", [unconfirmedProgress], now);
        Check(store.Get(saved.Id) is { State: TaskState.NeedsReview, ProgressPercent: null },
            "unknown required completion clears previously collected one hundred percent");
        store.ImportMetadataTasks("ucampus", [finished], now);
        store.ImportMetadataTasks("ucampus", [elective], now);
        Check(store.Get(saved.Id) is { State: TaskState.Ignored, ProgressPercent: null },
            "removing required checkpoints clears former completion percentage");
        store.ImportMetadataTasks("ucampus", [row], now);
        saved = store.Get(saved.Id)!;
        var edited = store.Edit(saved.Id, saved.Revision, new(saved.Title, "ucampus", saved.CourseName, DeadlineKind.None, null, null, saved.Link, 75, "", "", 1), now);
        store.SetState(edited.Id, edited.Revision, TaskState.Ignored, now);
        await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync();
        Check(store.Get(saved.Id) is { ProgressPercent: 75, DeadlineKind: DeadlineKind.None, State: TaskState.Ignored, PriorityOverride: 1 }, "manual unit state progress priority and dates remain dominant");
        store.ImportMetadataTasks("ucampus", [unconfirmedProgress], now);
        Check(store.Get(saved.Id) is { ProgressPercent: 75, State: TaskState.Ignored },
            "unknown collected unit progress preserves an explicit manual percentage and state");
        var before = store.LoadTasks().Count; reader.Response = request => request.Operation == "ucampus-content-catalog" ? new("network_error", null) : null;
        summary = await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync();
        Check(!summary.LearningUnitListsCovered && summary.FullTaskListsCovered && summary.Tasks == U.TaskCount && store.LoadTasks().Count == before,
            "unit discovery failure preserves unit history and independently imports assignments");
        using (var cancellation = new CancellationTokenSource())
        {
            reader.Response = request => { if (request.Operation == "ucampus-unit-progress") cancellation.Cancel(); return LearningResponse(request); };
            try { await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync(cancellation: cancellation.Token); throw new Exception("Unit cancellation accepted"); }
            catch (OperationCanceledException) { Check(store.LoadTasks().Count == before && store.Get(saved.Id)!.State == TaskState.Ignored,
                "cancelled unit response cannot import over existing history or manual state"); }
        }
        reader.Response = request =>
        {
            if (request.Operation == "ucampus-unit-progress")
            {
                var current = store.LoadCourseRecording("ucampus");
                store.SaveCourseRecording("ucampus", false, [], true, current.Revision, now);
            }
            return LearningResponse(request);
        };
        try { await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync(); throw new Exception("Changed selection accepted"); }
        catch (CourseRecordingChangedException) { Check(store.LoadTasks().Count == before && store.Get(saved.Id)!.State == TaskState.Ignored,
            "course selection changed during unit read blocks stale unit import"); }
        await UcampusUnitHttpAsync(course, now, row);
    }

    private static async Task UcampusUnitHttpAsync(MetadataCourse course, DateTimeOffset now, PlatformCollectedTask row)
    {
        var paths = new List<string>();
        using var reader = new MetadataHttpReader(MetadataPlatform.Ucampus, (uri, _) => Task.FromResult<IReadOnlyList<MetadataSessionCookie>>(
            new[] { U.Cookie(), U.Cookie() with { Name = "content_cookie", Domain = "ucontent.unipus.cn", Value = "synthetic-content-cookie" } }.Where(cookie => cookie.AppliesTo(uri)).ToArray()),
            new Handler((request, _) =>
            {
                var uri = request.RequestUri!; var path = Uri.UnescapeDataString(uri.AbsolutePath); paths.Add(path);
                Check(request.Method == HttpMethod.Get && request.Content is null && uri.IdnHost is "u.unipus.cn" or "ucontent.unipus.cn", "unit collection uses official metadata GET endpoints only");
                if (uri.IdnHost == "ucontent.unipus.cn") Check(uri.AbsolutePath.Contains("synthetic+content:pc/", StringComparison.Ordinal)
                    && request.Headers.GetValues("X-ANNOTATOR-AUTH-TOKEN").Single() == "synthetic-unused-token"
                    && request.Headers.GetValues("appid").Single() == "5" && request.Headers.GetValues("uni-client-ver").Single() == "12040",
                    "course-content token is bound to successfully confirmed profile");
                else Check(!request.Headers.Contains("X-ANNOTATOR-AUTH-TOKEN"), "content bearer never sent to student profile");
                var body = path switch {
                    "/user/data/getToken" => U.Profile(), "/user/student/mycourse/courseCatalog" => U.CatalogPage(),
                    "/course/api/course/synthetic+content:pc/default/" => U.LearningContentInfo(),
                    "/course/api/course_catalog/synthetic+content:pc/default/" => U.LearningCatalog(),
                    "/course/api/v2/course_progress/synthetic+content:pc/" + U.Account + "/default/" => U.LearningCourseProgress(now),
                    _ => throw new Exception("Unexpected unit navigation metadata route") };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) });
            }));
        var request = new MetadataRequest("ucampus-content-catalog") { Course = course, AccountId = U.Account };
        Check((await reader.ReadAsync(request, default)).Outcome == "course_credentials_unavailable" && paths.Count == 0, "content cannot be read before the profile account is verified");
        var resolution = await reader.ResolveUcampusAsync(row.Link!, default);
        Check(resolution is { Outcome: "ready", Destination: "learning-unit" } && MetadataTaskNavigation.UcampusDestinationMatches(row.Link!, resolution.Address!)
            && resolution.Address!.Contains("#/synthetic+content:pc/courseware/", StringComparison.Ordinal)
            && !resolution.Address!.Contains("openId", StringComparison.Ordinal) && !resolution.Address.Contains("learningUnit", StringComparison.Ordinal),
            "unit click verifies directory then strips local account identity before opening official content route");
        Check(paths.Count == 5 && paths.All(path => !path.Contains("automaticJoinClass", StringComparison.Ordinal) && !path.EndsWith("pc.html", StringComparison.Ordinal)),
            "background resolution never executes learning pages or automatic class membership");
        foreach (var bad in new[] { request with { UnitId = "u1" }, request with { Page = 2 }, request with { AccountId = "other-account" },
            request with { Course = course with { Ucampus = course.Ucampus! with { ContentId = "../foreign" } } } })
            Check((await reader.ReadAsync(bad, default)).Outcome is "unsupported_request" or "course_credentials_unavailable", "unbound content requests are stopped before HTTP");
        var entry = MetadataTaskNavigation.ParseUcampusUnit(row.Link)!;
        Check(!MetadataTaskNavigation.UcampusDestinationMatches(row.Link!, resolution.Address!.Replace("u1g2", "u1g1", StringComparison.Ordinal))
            && MetadataTaskNavigation.Key("ucampus", row.Link!.Replace("contentId=synthetic", "contentId=foreign", StringComparison.Ordinal)) != MetadataTaskNavigation.Key("ucampus", row.Link),
            "unit route or course-content substitution changes protected navigation identity");
    }
}
