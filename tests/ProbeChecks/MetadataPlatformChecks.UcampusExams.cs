using AccessProbe;
using U = PlatformMetadataTestData.UcampusFixtures;

internal static partial class MetadataPlatformChecks
{
    private static async Task UcampusExamChecks(MetadataCourse course, string directory)
    {
        foreach (var replacement in new[] { ("classId=2001", "classId=2999"), ("eccId=3001", "eccId=3999"), ("type=2", "type=1") })
            Check(MetadataPlatformParser.UcampusExamEntry(U.ExamShell().Replace(replacement.Item1, replacement.Item2), course).Outcome != "ok",
                "exam shell requires current class ECC and list type");
        Check(MetadataPlatformParser.UcampusExamEntry(U.ExamShell(), course).Course!.Ucampus!.ExamListSchoolId == "6001",
            "exam list school is obtained from official shell");
        var batch = MetadataPlatformParser.UcampusExam(U.DefaultExamList(), U.Account, course, 1);
        Check(batch is { Outcome: "ok", ListComplete: true, TotalRows: U.ExamTaskCount } && batch.Tasks.All(task => task.Kind == "exam"),
            "all exam states collected independently from homework");
        Check(batch.Tasks.Select(task => task.State).SequenceEqual(new TaskState?[] { TaskState.Pending, TaskState.Completed, TaskState.Returned }),
            "exam states preserve pending submitted and redo evidence");
        var homework = MetadataPlatformParser.UcampusHomework(U.DefaultList(), U.Account, course, 1).Tasks[0];
        var exam = batch.Tasks[0];
        Check(homework.RemoteId == exam.RemoteId && TaskStore.MetadataTaskKey(homework) != TaskStore.MetadataTaskKey(exam)
            && MetadataTaskNavigation.Key("ucampus", homework.Link) != MetadataTaskNavigation.Key("ucampus", exam.Link),
            "same legacy homework id remains distinct between homework and exam");
        foreach (var task in batch.Tasks)
        {
            Check(MetadataTaskNavigation.Matches(task), "exam unsigned entry matches account course class and kind");
            var entry = MetadataTaskNavigation.ParseUcampus(task.Link)!;
            Check(entry.Kind == "exam" && entry.Forward.Contains("type=2", StringComparison.Ordinal), "exam entry returns to official test list");
            var signed = MetadataTaskNavigation.UcampusSignedAddress(entry, "synthetic-exam-sign", "2")!;
            Check(MetadataTaskNavigation.UcampusDestinationMatches(task.Link!, signed) && signed.Contains("exerciseType=2", StringComparison.Ordinal),
                "click-time exam destination has official exercise type");
            Check(!MetadataTaskNavigation.UcampusDestinationMatches(task.Link!, signed.Replace("exerciseType=2", "exerciseType=1")),
                "exam cannot resolve to homework type");
        }
        Check(MetadataPlatformParser.UcampusExam(U.List([U.Row()]), U.Account, course, 1) is { UnsupportedRows: 1, Tasks.Count: 0 },
            "homework row cannot be imported as exam");
        Check(MetadataPlatformParser.UcampusExam(U.List([U.Row(type: 2, classId: "2999")]), U.Account, course, 1)
            is { UnsupportedRows: 1, Tasks.Count: 0 }, "exam cannot cross class binding");
        Check(MetadataPlatformParser.UcampusExam(U.List(), U.Account, course, 1) is { Outcome: "ok", ListComplete: true, TotalRows: 0 },
            "explicit zero-page exam list is recognized");
        Check(MetadataPlatformParser.UcampusExam(U.List(), U.Account, course, 2).Outcome != "ok", "exam wrong page cannot become empty success");

        var store = FixtureTaskStore.Create(Path.Combine(directory, "exam-tasks.db")); var reader = new U.Reader();
        var summary = await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync();
        Check(summary is { FullTaskListsCovered: true, CoursesRead: 1, Tasks: U.TaskCount }
            && summary.CoverageScope == "legacy-class-homework-exam-and-learning-units" && summary.LearningUnitListsCovered, "legacy course coverage includes both lists and unit metadata");
        var savedPair = store.LoadTasks().Where(task => task.PlatformTitle is "合成 U 校园待办" or "合成 U 校园待测").ToArray();
        Check(savedPair.Length == 2 && savedPair.Select(task => task.CollectedNavigationKey).Distinct().Count() == 2,
            "same homework id in two lists stores two protected task entries");

        var tenRows = Enumerable.Range(1, 10).Select(number => U.Row((7100 + number).ToString(), type: 2)).ToArray();
        reader.Requests.Clear(); reader.Response = request => request.Operation == "ucampus-exam"
            ? new("ok", 200, request.Page == 1 ? U.List(tenRows, 1, 11) : U.List([U.Row("7111", type: 2)], 2, 11)) : null;
        summary = await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync();
        Check(summary.FullTaskListsCovered && summary.Tasks == U.HomeworkTaskCount + 11
            && reader.Requests.Where(request => request.Operation == "ucampus-exam").Select(request => request.Page).SequenceEqual(new[] { 1, 2 }),
            "exam pagination collects the exact total before importing");
        var before = store.LoadTasks().Count;
        reader.Response = request => request.Operation == "ucampus-exam"
            ? new("ok", 200, request.Page == 1 ? U.List(tenRows, 1, 11) : U.List([tenRows[0]], 2, 11)) : null;
        summary = await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.CoursesRead == 0 && summary.Tasks == U.HomeworkTaskCount && store.LoadTasks().Count == before,
            "overlapping exam pages discard exam batch while homework remains available");
        var unsavedRows = Enumerable.Range(1, 10).Select(number => U.Row((8100 + number).ToString(), "合成未完成分页测试", type: 2)).ToArray();
        reader.Response = request => request.Operation == "ucampus-exam" ? request.Page == 1
            ? new("ok", 200, U.List(unsavedRows, 1, 11)) : new("network_error", null) : null;
        summary = await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.Tasks == U.HomeworkTaskCount
            && store.LoadTasks().Count == before && store.LoadTasks().All(task => task.PlatformTitle != "合成未完成分页测试"),
            "failed second exam page never imports incomplete staged rows");
        reader.Response = request => request.Operation == "ucampus-exam-entry" ? new("ok", 200, "<!doctype html><title>未知页面</title>") : null;
        reader.Requests.Clear(); summary = await new MetadataSynchronizer(MetadataPlatform.Ucampus, store, reader).RunAsync();
        Check(!summary.FullTaskListsCovered && summary.Tasks == U.HomeworkTaskCount && !reader.Requests.Any(request => request.Operation == "ucampus-exam"),
            "unknown exam contract cannot be treated as a course without tests");
    }
}
