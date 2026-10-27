using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using AccessProbe;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SyncDiagnosticHarness;

// Reads official course/task metadata through the project's selected profiles.
// Does not navigate a login/answering page or instantiate the writable TaskStore.
internal static class RainDiagnosis
{
    public static int Run(string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var reportRoot = Path.Combine(root, ".local", "sync-diagnostics");
        Directory.CreateDirectory(reportRoot);
        var reportPath = Path.Combine(reportRoot, "rain-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        File.WriteAllText(Path.Combine(reportRoot, "latest-rain-report.txt"), reportPath);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var browser = new WebView2 { Width = 1, Height = 1, Visibility = Visibility.Hidden };
        var hostWindow = new Window { Content = browser, Width = 1, Height = 1, Left = -10000, Top = -10000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var outcomes = new List<object>();
        var exitCode = 1;
        hostWindow.Loaded += async (_, _) =>
        {
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                foreach (var platform in RainPlatform.All)
                {
                    var settings = ReadSettings(root, platform);
                    if (!settings.Connected)
                    {
                        outcomes.Add(new { platform = platform.Id, outcome = "not_connected" });
                        continue;
                    }
                    var environment = await CoreWebView2Environment.CreateAsync(null, RainSyncService.ProfilePath(root, platform));
                    using var session = new WebView2 { Width = 1, Height = 1, Visibility = Visibility.Hidden };
                    hostWindow.Content = session;
                    await session.EnsureCoreWebView2Async(environment);
                    session.CoreWebView2.PermissionRequested += (_, request) => request.State = CoreWebView2PermissionState.Deny;
                    session.CoreWebView2.ServerCertificateErrorDetected += (_, error) => error.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                    async Task<IReadOnlyList<Cookie>> Cookies(Uri address, CancellationToken token)
                    {
                        token.ThrowIfCancellationRequested();
                        var observed = await session.CoreWebView2.CookieManager.GetCookiesAsync(address.AbsoluteUri);
                        return observed.Select(cookie => new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                        { Secure = cookie.IsSecure, HttpOnly = cookie.IsHttpOnly }).ToArray();
                    }
                    using var reader = new RainHttpReader(platform, settings.Host, Cookies);
                    var result = await InspectAsync(root, platform, settings.Host, reader, cancellation.Token);
                    outcomes.Add(result);
                }
                exitCode = 0;
                Write("diagnosed", null);
            }
            catch (Exception error) { Write("failed", error.GetType().Name); }
            finally { browser.Dispose(); app.Shutdown(exitCode); }
        };
        app.Run(hostWindow);
        return exitCode;

        void Write(string outcome, string? error) => File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            schema = 1, generatedAtUtc = DateTimeOffset.UtcNow, outcome, error, outcomes,
            databaseMode = "read_only", profileNavigation = "none", answeringPagesRequested = 0,
            externalBrowserLaunched = false, responseBodiesRetained = false,
            privacy = "no_account_ids_no_titles_no_course_names_no_links_no_cookies"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<object> InspectAsync(string root, RainPlatform platform, string selectedHost,
        IRainMetadataReader reader, CancellationToken token)
    {
        var requests = 0;
        var stages = new List<object>();
        var collected = new Dictionary<string, CollectedTask>(StringComparer.Ordinal);
        var courseOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        async Task<RainResponse> Read(string stage, RainClassroom? room, Uri address)
        {
            if (++requests > 150) throw new InvalidOperationException("Metadata diagnosis request limit.");
            var result = await reader.ReadAsync(address, token);
            stages.Add(new { stage, courseIndex = room is null ? 0 : courseOrdinals.GetValueOrDefault(room.Id),
                result.Outcome, result.StatusCode });
            return result;
        }
        var user = await Read("userinfo", null, RainAddressPolicy.Api(selectedHost, "/v2/api/web/userinfo"));
        var account = user.Outcome == "ok" ? RainClassroomParser.AccountId(user.Json) : null;
        if (account is null) return new { platform = platform.Id, outcome = user.Outcome, stages, requests };
        var list = await Read("catalog", null, RainAddressPolicy.Api(selectedHost, "/v2/api/web/courses/list", ("identity", "2")));
        var catalog = RainClassroomParser.Catalog(list.Json);
        var courseResults = new List<object>();
        foreach (var original in catalog.Courses.Take(20))
        {
            courseOrdinals.Add(original.Id, courseOrdinals.Count + 1);
            var detail = await Read("classroom", original, Api("/v2/api/web/classrooms/" + original.Id, original, ("role", "5")));
            var room = detail.Outcome == "ok" ? RainClassroomParser.Classroom(detail.Json, original.Id, original) : null;
            if (room is null) continue;
            var schedulePage = await Read("schedule", room, Api("/mooc-api/v1/lms/learn/course/schedule", room, Fields(room, ("is_h5", "true"))));
            var schedule = RainClassroomParser.Schedule(schedulePage.Json);
            var activityTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var leafTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var activityRows = 0; var activityTasks = 0; var chapterTasks = 0; var pages = 0;
            var activityCovered = false; var hasChapter = room.HasChapterContent == true || room.Sign.Length > 0;
            var parserIssues = new List<string>();
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < 100; page++)
            {
                var response = await Read("activities", room, Api("/v2/api/web/logs/learn/" + room.Id, room,
                    ("actype", "-1"), ("page", page.ToString(CultureInfo.InvariantCulture)), ("offset", "20"), ("sort", "-1")));
                if (response.Outcome != "ok") break;
                pages++;
                using var document = JsonDocument.Parse(response.Json);
                var activities = Field(Field(document.RootElement, "data"), "activities");
                if (activities.ValueKind == JsonValueKind.Array)
                    foreach (var row in activities.EnumerateArray()) { activityRows++; CountType(row, "type", activityTypes); }
                var batch = RainClassroomParser.Activities(response.Json, platform, selectedHost, account, room, schedule);
                hasChapter |= batch.HasLeafContent;
                activityTasks += batch.Tasks.Count;
                parserIssues.AddRange(batch.Issues);
                foreach (var task in batch.Tasks) collected[TaskStore.CollectedTaskKey(task)] = task;
                if (batch.HasMore == false) { activityCovered = true; break; }
                if (!fingerprints.Add(response.Json)) { parserIssues.Add("pagination_loop"); break; }
            }
            string? chapterOutcome = null;
            if (hasChapter)
            {
                var chapter = await Read("chapter", room, Api("/mooc-api/v1/lms/learn/course/chapter", room, Fields(room, ("show_unpublished", "0"))));
                chapterOutcome = chapter.Outcome;
                if (chapter.Outcome == "ok")
                {
                    using var document = JsonDocument.Parse(chapter.Json);
                    CountTree(Field(Field(document.RootElement, "data"), "course_chapter"), leafTypes, 0);
                    var batch = RainClassroomParser.Chapter(chapter.Json, platform, selectedHost, account, room, schedule);
                    chapterTasks = batch.Tasks.Count; parserIssues.AddRange(batch.Issues);
                    foreach (var task in batch.Tasks) collected[TaskStore.CollectedTaskKey(task)] = task;
                }
            }
            courseResults.Add(new { courseIndex = courseOrdinals[room.Id], room.HasChapterContent,
                hasCourseSignature = room.Sign.Length > 0, scheduleOutcome = schedule.Outcome,
                scheduleTaskCount = schedule.Leaves.Count, pages, activityRows, activityTypes,
                activityTasks, activityCovered, chapterOutcome, chapterTasks, chapterLeafTypes = leafTypes,
                parserIssues = parserIssues.Distinct().ToArray() });
        }
        var saved = ReadTasks(root, platform.Id);
        var protector = new WindowsLinkProtector();
        var matched = 0; var metadataMatched = 0; var linksMatched = 0; var taskResults = new List<object>();
        foreach (var (task, protectedLink) in saved)
        {
            if (task.SourceKey is null || !collected.TryGetValue(task.SourceKey, out var fresh)) continue;
            matched++;
            var sameMetadata = (task.Overrides.HasFlag(TaskOverrides.State) || task.State == fresh.State)
                && (task.Overrides.HasFlag(TaskOverrides.Deadline) || task.DeadlineUtc == fresh.DeadlineUtc)
                && (task.Overrides.HasFlag(TaskOverrides.Progress) || task.ProgressPercent == fresh.ProgressPercent);
            if (sameMetadata) metadataMatched++;
            var link = protectedLink is null ? null : protector.Unprotect(protectedLink);
            var sameLink = link is not null && (task.Overrides.HasFlag(TaskOverrides.Link)
                || RainTaskNavigation.IsOfficialTaskLink(platform.Id, link, fresh.ClassroomId, fresh.RemoteId, fresh.Kind)
                    && link == fresh.Link && RainTaskNavigation.Key(platform.Id, link) == task.CollectedNavigationKey);
            if (sameLink) linksMatched++;
            taskResults.Add(new { fresh.Kind, state = task.State.ToString(), task.PlatformStateEvidence,
                task.ProgressPercent, task.DeadlineUtc, task.BeginUtc, sameMetadata, sameLink });
        }
        return new { platform = platform.Id, host = selectedHost, outcome = "diagnosed", catalogOutcome = catalog.Outcome,
            courses = catalog.Courses.Count, catalog.Covered, coursesInspected = courseResults.Count,
            courseResults, freshTasks = collected.Count, storedTasks = saved.Count, matchedTasks = matched,
            metadataMatched, linksMatched, taskResults, requests, stages };

        Uri Api(string path, RainClassroom room, params (string Name, string Value)[] fields)
        {
            var values = fields.ToList();
            if (room.UniversityId.Length > 0) values.Add(("uv_id", room.UniversityId));
            if (room.PlatformId.Length > 0) values.Add(("platform_id", room.PlatformId));
            return RainAddressPolicy.Api(selectedHost, path, values.ToArray());
        }
    }

    private static (string Name, string Value)[] Fields(RainClassroom room, (string Name, string Value) extra) =>
        room.Sign.Length > 0 ? [("cid", room.Id), ("classroom_id", room.Id), ("sign", room.Sign), extra]
            : [("cid", room.Id), ("classroom_id", room.Id), extra];

    private static SqliteConnection OpenReadOnly(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, ".local", "data", "tasks.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    private static PlatformSyncSettings ReadSettings(string root, RainPlatform platform)
    {
        using var connection = OpenReadOnly(root); using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key;";
        command.Parameters.AddWithValue("$key", "platform:" + platform.Id);
        var settings = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PlatformSyncSettings>(json) : null;
        if (settings is null) return new() { Host = platform.DefaultHost };
        platform.CheckHost(settings.Host); return settings;
    }

    private static List<(TaskItem Task, byte[]? Link)> ReadTasks(string root, string platformId)
    {
        using var connection = OpenReadOnly(root); using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload,protected_link FROM tasks;";
        using var reader = command.ExecuteReader();
        var result = new List<(TaskItem, byte[]?)>();
        while (reader.Read())
            if (JsonSerializer.Deserialize<TaskItem>(reader.GetString(0)) is { } task && task.PlatformId == platformId)
                result.Add((task, reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1)));
        return result;
    }

    private static JsonElement Field(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(name, out var value) ? value : default;
    private static void CountType(JsonElement row, string name, Dictionary<string, int> counts)
    {
        var value = Field(row, name);
        var key = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is >= 0 and <= 30
            ? number.ToString(CultureInfo.InvariantCulture) : "other";
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }
    private static void CountTree(JsonElement node, Dictionary<string, int> counts, int depth)
    {
        if (depth > 20) return;
        if (node.ValueKind == JsonValueKind.Array)
        { foreach (var child in node.EnumerateArray()) CountTree(child, counts, depth + 1); }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("leaf_type", out _)) CountType(node, "leaf_type", counts);
            foreach (var name in new[] { "section_leaf_list", "leaf_list" }) CountTree(Field(node, name), counts, depth + 1);
        }
    }
}
