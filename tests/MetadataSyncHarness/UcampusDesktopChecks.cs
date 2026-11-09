using System.Windows;
using System.Windows.Controls;
using AccessProbe;
using U = PlatformMetadataTestData.UcampusFixtures;
using Data = PlatformMetadataTestData.Fixtures;

namespace MetadataSyncHarness;

internal static partial class Program
{
    private static async Task VerifyUcampusAsync()
    {
        var service = services["ucampus"]; var fixture = fixtures["ucampus"];
        var tasks = store.LoadTasks().Where(task => task.Source == TaskSource.Ucampus).ToArray();
        Check(tasks.Count(task => MetadataTaskNavigation.ParseUcampus(task.Link)?.Kind == "exam") == U.ExamTaskCount
            && tasks.Count(task => MetadataTaskNavigation.ParseUcampus(task.Link)?.Kind == "homework") == U.HomeworkTaskCount,
            "unified desktop includes both legacy list kinds");
        Check(tasks.Where(task => task.PlatformTitle is "合成 U 校园待办" or "合成 U 校园待测")
            .Select(task => task.CollectedNavigationKey).Distinct().Count() == 2, "desktop homework and exam with same id have separate entries");
        foreach (var task in tasks)
        {
            var requestCount = fixture.Requests; string? opened = null;
            var navigation = new TaskNavigationService(store, service.ResolveTaskAddressAsync, address => opened = address);
            var result = await navigation.OpenAsync(task.Id, CancellationToken.None);
            Check(result.Outcome == "opened" && opened is not null && MetadataTaskNavigation.UcampusDestinationMatches(task.Link!, opened), "desktop legacy task resolves current signature");
            Check(fixture.Requests == requestCount + 2 && fixture.Paths.All(path => !path.StartsWith("/uexercise/", StringComparison.Ordinal)), "desktop signature resolution never downloads questions");
        }
        var recording = store.LoadCourseRecording("ucampus");
        Check(recording.CatalogComplete && recording.Courses.Count == 1, "legacy class course offered in unified picker");
        store.SaveCourseRecording("ucampus", false, [], true, recording.Revision, Data.Now); fixture.Paths.Clear();
        await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table.Items.Count == Data.SchoolExamTaskCount + 1);
        Check(fixture.Paths.SequenceEqual(new[] { "/user/data/getToken", "/user/student/index" }) && store.LoadTasks().Count == Data.SchoolExamTaskCount + U.TaskCount + 1,
            "legacy selection hides tasks while retaining history and skipping task requests");
        recording = store.LoadCourseRecording("ucampus"); store.SaveCourseRecording("ucampus", true, [], true, recording.Revision, Data.Now);
        await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table.Items.Count == Data.SchoolExamTaskCount + U.TaskCount + 1);
        fixture.UnknownCatalog = true; await service.RefreshCourseCatalogAsync(); fixture.UnknownCatalog = false;
        Check(store.LoadCourseRecording("ucampus") is { Courses.Count: 1, LastCatalogRefresh.Outcome: "partial" }, "unrecognized legacy HTML keeps official course choice");
        await service.RefreshCourseCatalogAsync();
        fixture.RejectLogin = true; await service.StartAsync(true); fixture.RejectLogin = false;
        Check(store.LoadMetadataSettings("ucampus").LastSyncOutcome == "login_required" && store.LoadTasks().Count == Data.SchoolExamTaskCount + U.TaskCount + 1,
            "legacy login expiry retains imported task history");
        fixture.SetCookies(browsers["ucampus"]); await Task.Delay(60); await service.StartAsync(true);
        Check(store.LoadMetadataSettings("ucampus").LastSyncOutcome == "completed", "legacy new login recovers homework and exam collection");
    }
}
