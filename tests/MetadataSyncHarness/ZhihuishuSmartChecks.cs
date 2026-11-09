using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AccessProbe;
using Data = PlatformMetadataTestData.Fixtures;
using U = PlatformMetadataTestData.UcampusFixtures;

namespace MetadataSyncHarness;

internal static partial class Program
{
    private static async Task VerifySmartDesktopAsync()
    {
        var service = services["zhihuishu"]; var fixture = fixtures["zhihuishu"];
        var count = store.LoadTasks().Count; var previousSummary = JsonSerializer.Serialize(store.LoadMetadataSettings("zhihuishu").LastSummary);
        fixture.IncludeSmartCourses = true; fixture.Paths.Clear();
        await service.RefreshCourseCatalogAsync();
        var recording = store.LoadCourseRecording("zhihuishu");
        Check(recording is { CatalogComplete: true, Courses.Count: 4 } && recording.Courses.Any(course => course.Name == "合成智慧课程"), "desktop refresh discovers new wisdom course alongside shared and flip catalogs");
        Check(store.LoadTasks().Count == count && fixture.Paths.All(path => !path.StartsWith("/student/gateway/t/", StringComparison.Ordinal))
            && JsonSerializer.Serialize(store.LoadMetadataSettings("zhihuishu").LastSummary) == previousSummary, "smart catalog refresh preserves task history and avoids task APIs");
        var smartKey = CourseRecordingRules.MetadataKey("zhihuishu", fixture.Account, Data.SmartCourseModel().Id, "smart");
        store.SaveCourseRecording("zhihuishu", false, [smartKey], true, recording.Revision, DateTimeOffset.UtcNow); fixture.Paths.Clear();
        await service.RecordingCoursesChangedAsync();
        await WaitAsync(() => Table.Items.Count == U.TaskCount + 7);
        var summary = store.LoadMetadataSettings("zhihuishu").LastSummary!;
        Check(summary is { AccountVerified: true, SmartCourses: 1, SelectedCourses: 1, CoursesRead: 1, Tasks: 6, Added: 6, LoginRequired: false, FullTaskListsCovered: true }, "desktop selected智慧course imports five homework and one exam");
        Check(fixture.Paths.Count(path => path == "/student/gateway/t/home/query-course-task-type") == 1
            && fixture.Paths.Count(path => path == "/student/gateway/t/task/taskList") == 4
            && fixture.Paths.All(path => path is not ("/zhsathome/homeworkUser/List" or "/zhsathome/examStu/List")), "desktop directs encrypted requests only to selected wisdom course");
        var smartTasks = store.LoadTasks().Where(task => task.RecordingCourseKey == smartKey).ToArray();
        Check(smartTasks.Length == 6 && smartTasks.Count(task => task.State == TaskState.Completed) == 4
            && smartTasks.Any(task => task.PlatformStatusText.Contains("逾期未提交", StringComparison.Ordinal)), "智慧course official completed and expired state appears in unified dashboard");
        Check(smartTasks.All(task => task.CollectedNavigationKey.Length > 0) && smartTasks.Select(task => task.CollectedNavigationKey).Distinct().Count() == 6,
            "same智慧course task list gives each task a distinct validated navigation binding");
        var navigation = new TaskNavigationService(store, null, _ => checks++); var beforeNavigation = fixture.Requests;
        await navigation.OpenAsync(smartTasks[0].Id, CancellationToken.None);
        Check(fixture.Requests == beforeNavigation, "smart official list navigation never starts paper or submission through background reader");
        var archive = new MetadataSessionStore(MetadataPlatform.Zhihuishu, Path.Combine(runRoot, ".local", "sessions", "zhihuishu.bin"), store);
        Check(archive.Load(DateTimeOffset.UtcNow).Count > 0 && store.LoadPlatformSessionInfo("zhihuishu").State == "ready", "smart metadata uses and retains existing encrypted login");
        var historical = summary with { LoginRequired = true, AccountVerified = false, Problems = [new("ZHS001", "", "登录已失效，请重新登录。") ] };
        store.RecordMetadataSyncResult(historical, "login_required", DateTimeOffset.UtcNow.AddDays(-1));
        await service.RefreshCourseCatalogAsync();
        Check(store.LoadMetadataSettings("zhihuishu").LastSummary!.LoginRequired && store.LoadPlatformSessionInfo("zhihuishu").State == "ready"
            && !Text("ZhihuishuPlatformSummary").Contains("登录过期", StringComparison.Ordinal), "old login-required task summary does not override current catalog and verified cookie");
        Click("SyncDetailsButton"); await Task.Delay(50);
        var details = app.Windows.OfType<Window>().Single(window => window.Title == "各平台同步详情");
        var texts = Visuals<TextBlock>(details).Select(text => text.Text).ToArray();
        Check(texts.Any(text => text.StartsWith("当前登录：登录已确认", StringComparison.Ordinal))
            && texts.Any(text => text.StartsWith("最近课程刷新：", StringComparison.Ordinal))
            && texts.Any(text => text.StartsWith("上次任务同步记录（当前登录已确认）", StringComparison.Ordinal)), "details explicitly distinguish current login catalog refresh and historical login error");
        details.Close();
        fixture.UnknownCatalog = true; await service.RefreshCourseCatalogAsync(); fixture.UnknownCatalog = false;
        Check(service.LoginDiagnostics.Snapshot().AccountConfirmed && !service.LoginDiagnostics.SessionConfirmed
            && Text("ZhihuishuPlatformSummary") == "已登录 · 课程刷新需核对", "partial catalog leaves account connected with actionable course refresh status");
        recording = store.LoadCourseRecording("zhihuishu");
        store.SaveCourseRecording("zhihuishu", false, [], true, recording.Revision, DateTimeOffset.UtcNow);
        await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table.Items.Count == U.TaskCount + 1);
        Check(store.LoadTasks().Count == count + 6, "deselecting wisdom course retains imported task history");
        fixture.IncludeSmartCourses = false; await service.RefreshCourseCatalogAsync();
        recording = store.LoadCourseRecording("zhihuishu");
        var originalKeys = recording.Courses.Where(course => course.Available && course.Key != smartKey).Select(course => course.Key).ToArray();
        store.SaveCourseRecording("zhihuishu", false, originalKeys, true, recording.Revision, DateTimeOffset.UtcNow);
        await service.RecordingCoursesChangedAsync(); await WaitAsync(() => Table.Items.Count == Data.SchoolExamTaskCount + U.TaskCount + 1);
        Check(store.LoadTasks().Count == count + 6 && store.LoadRecordedTasks().All(task => task.RecordingCourseKey != smartKey), "switching back to shared and flip courses keeps new history excluded from reminders");
        await service.ClearSessionAsync(); fixture.SetCookies(browsers["zhihuishu"]); await Task.Delay(60);
        fixture.UnknownProfile = true; fixture.Paths.Clear();
        await service.RefreshCourseCatalogAsync(); fixture.UnknownProfile = false;
        Check(store.LoadPlatformSessionInfo("zhihuishu").State != "ready" && !service.LoginDiagnostics.Snapshot().AccountConfirmed
            && fixture.Paths.Count == 1 && fixture.Paths[0].EndsWith("getLoginUserInfo", StringComparison.Ordinal), "unverified profile cannot falsely confirm login or read catalogs");
        await service.RefreshCourseCatalogAsync();
        Check(store.LoadPlatformSessionInfo("zhihuishu").State == "ready" && service.LoginDiagnostics.Snapshot().AccountConfirmed,
            "verified retry confirms saved login after unknown profile recovers");
    }
}
