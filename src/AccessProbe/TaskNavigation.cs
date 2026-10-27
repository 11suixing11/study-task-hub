using System.Text.Json.Serialization;

namespace AccessProbe;

public sealed record TaskLinkResolution(string Outcome, string Destination,
    [property: JsonIgnore] string? Address = null)
{
    public override string ToString() => nameof(TaskLinkResolution);
}

public static class TaskNavigationRules
{
    public static string? Destination(Uri original, Uri target)
    {
        if (WorkListParser.ParseTaskLocation(original.OriginalString) is null || !TaskRules.IsSafeLink(target.OriginalString)) return null;
        if (ChaoxingSyncParser.IsWorkActionRedirect(original, target)) return "answer";
        if (!ChaoxingSyncParser.IsReadAddress(target) || !ChaoxingSyncParser.IsWorkRedirect(original, target)
            || WorkListParser.ParseDetailLocation(target.OriginalString) is null) return null;
        return target.AbsolutePath switch
        {
            "/mooc-ans/mooc2/work/view" => "view",
            "/mooc-ans/mooc2/work/prompt" => "submitted",
            "/mooc-ans/mooc2/work/task" => "entry",
            _ => null
        };
    }

    public static string ActionName(TaskItem task) => TaskRules.IsActive(task) ? "做题 / 提交 ↗" : "查看作业 ↗";

    public static string OpenMessage(TaskLinkResolution result) => result.Outcome == "opened_saved_link"
        ? (result.Destination == "manual" ? "已在系统浏览器打开该任务的作业链接。"
            : result.Destination == "login_required"
                ? "已在系统浏览器打开该任务入口。采集会话需要重新登录；浏览器沿用自己的登录状态。"
                : "已在系统浏览器打开该任务的已保存入口，页面由官方平台继续跳转。")
        : result.Destination switch
        {
            "answer" => "已在系统浏览器打开该作业的做题 / 提交页面。",
            "submitted" => "已在系统浏览器打开该作业的提交记录页面。",
            "view" => "已在系统浏览器打开该作业的查看页面。",
            _ => "已在系统浏览器打开该任务入口，页面由官方平台继续跳转。"
        };
}

public sealed class TaskNavigationService
{
    private readonly TaskStore store;
    private readonly Func<Uri, CancellationToken, Task<TaskLinkResolution>>? resolve;
    private readonly Action<string> openExternal;

    public TaskNavigationService(TaskStore store,
        Func<Uri, CancellationToken, Task<TaskLinkResolution>>? resolve, Action<string> openExternal)
    {
        this.store = store; this.resolve = resolve; this.openExternal = openExternal;
    }

    public async Task<TaskLinkResolution> OpenAsync(string taskId, CancellationToken cancellation)
    {
        // Read the latest saved signature rather than the dashboard's older display snapshot.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var task = await Task.Run(() => store.Get(taskId), cancellation)
                ?? throw new TaskValidationException("TASK004", "任务已不存在，请刷新清单。");
            if (!TaskRules.IsSafeLink(task.Link))
                throw new TaskValidationException(task.LinkIssue ?? "LINK001", "作业链接不可用，请同步对应平台或编辑补充。");
            var result = new TaskLinkResolution("opened_saved_link", "manual", task.Link);
            if (task.Source == TaskSource.Mooc && !task.Overrides.HasFlag(TaskOverrides.Link))
            {
                if (task.CollectedNavigationKey.Length == 0 || MoocTaskNavigation.Key(task.Link!) != task.CollectedNavigationKey)
                    throw new TaskValidationException("LINK003", "MOOC 入口与所选任务的课程、学期或编号不一致，请同步后重试。");
                result = new("opened", TaskRules.IsActive(task) ? "answer" : "view", task.Link);
            }
            if (task.Source == TaskSource.RainClassroom && !task.Overrides.HasFlag(TaskOverrides.Link))
            {
                if (task.CollectedNavigationKey.Length == 0 || RainTaskNavigation.Key(task.PlatformId, task.Link!) != task.CollectedNavigationKey)
                    throw new TaskValidationException("LINK003", "作业入口与所选任务的归属不一致，请同步对应平台后重试。");
                // The official SPA route is opened externally; the aggregator never GETs answering content.
                result = new("opened", TaskRules.IsActive(task) ? "answer" : "view", task.Link);
            }
            if (task.Source == TaskSource.Chaoxing && !task.Overrides.HasFlag(TaskOverrides.Link)
                && resolve is not null && WorkListParser.ParseTaskLocation(task.Link) is { } location)
            {
                var resolved = await resolve(location.PageUri, cancellation);
                if (resolved.Outcome == "ready" && resolved.Address is { } address)
                {
                    if (!Uri.TryCreate(address, UriKind.Absolute, out var target)
                        || TaskNavigationRules.Destination(location.PageUri, target) is not { } destination)
                        throw new TaskValidationException("LINK003", "平台返回的作业入口与所选任务不一致，未打开。");
                    result = new("opened", destination, address);
                }
                else if (resolved.Outcome is "invalid_link" or "unsupported_redirect" or "redirect_loop")
                    throw new TaskValidationException("LINK003", "平台返回的作业入口无法确认归属，请同步学习通或在编辑中核对链接。");
                else result = new("opened_saved_link", resolved.Outcome, task.Link);
            }
            cancellation.ThrowIfCancellationRequested();
            var latest = await Task.Run(() => store.Get(taskId), cancellation);
            if (latest is null) throw new TaskValidationException("TASK004", "任务已不存在，未打开作业。");
            if (latest.Link != task.Link || latest.Overrides.HasFlag(TaskOverrides.Link) != task.Overrides.HasFlag(TaskOverrides.Link))
                continue;
            cancellation.ThrowIfCancellationRequested();
            openExternal(result.Address!);
            return result;
        }
        throw new TaskValidationException("TASK004", "作业链接刚被更新，请再点击一次。");
    }
}
