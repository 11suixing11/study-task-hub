using System.Globalization;
using System.Text.Json.Serialization;

namespace AccessProbe;

public enum TaskState { Pending, Completed, Ignored, Returned, NeedsReview }
public enum TaskSource { Manual = 0, Chaoxing = 1, RainClassroom = 2, Mooc = 3 }
public enum DeadlineKind { Unknown, Known, None }
[Flags]
public enum TaskOverrides { None = 0, Title = 1, Course = 2, Deadline = 4, Link = 8, Progress = 16, Priority = 32, Begin = 64, State = 128, Score = 256 }

public sealed record TaskItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Revision { get; init; } = 1;
    public TaskSource Source { get; init; }
    public string? SourceKey { get; init; }
    public string Title { get; init; } = "";
    public string PlatformId { get; init; } = "chaoxing";
    public string CourseName { get; init; } = "";
    public string RecordingCourseKey { get; init; } = "";
    public TaskState State { get; init; } = TaskState.Pending;
    public DeadlineKind DeadlineKind { get; init; } = DeadlineKind.Unknown;
    public DateTimeOffset? BeginUtc { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public DeadlineEvidence DeadlineEvidence { get; init; }
    public int AutomaticMetadataVersion { get; init; }
    public string PlatformListTimeText { get; init; } = "";
    public DateTimeOffset? PlatformListTimeObservedAtUtc { get; init; }
    public string AutomaticWindowText { get; init; } = "";
    public decimal? ProgressPercent { get; init; }
    public string ScoreText { get; init; } = "";
    public string Notes { get; init; } = "";
    public int? PriorityOverride { get; init; }
    public string PlatformTitle { get; init; } = "";
    public string PlatformStatusText { get; init; } = "";
    public string PlatformStateEvidence { get; init; } = "";
    public string AutomaticStatusExplanation { get; init; } = "";
    public string CollectedNavigationKey { get; init; } = "";
    public string DisplayedTimeText { get; init; } = "";
    public string DetailTimeText { get; init; } = "";
    public DateTimeOffset? DetailCollectedAtUtc { get; init; }
    public DateTimeOffset? DetailCheckedAtUtc { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CollectedAtUtc { get; init; }
    public TaskOverrides Overrides { get; init; }
    [JsonIgnore] public string? Link { get; init; }
    [JsonIgnore] public string? LinkIssue { get; init; }
    public override string ToString() => nameof(TaskItem);
}

public sealed record TaskDraft(
    string Title, string PlatformId, string CourseName,
    DeadlineKind DeadlineKind, DateTimeOffset? BeginUtc, DateTimeOffset? DeadlineUtc,
    string? Link, decimal? ProgressPercent, string ScoreText, string Notes, int? PriorityOverride)
{
    public bool ConfirmDeadline { get; init; }
}

public sealed record AppSettings(bool NotificationsEnabled = true, bool DisclaimerSeen = false)
{
    public bool AutomaticSyncEnabled { get; init; } = true;
    public DateTimeOffset? LastSyncAtUtc { get; init; }
    public string LastSyncOutcome { get; init; } = "not_started";
    public ChaoxingSyncSummary? LastSyncSummary { get; init; }
}
public sealed record ImportSummary(int Added, int Updated, int Unresolved, int DuplicateRows);
public sealed record PlatformSite(string Id, string Name, string Domain, string Login, string Status);

public static class PlatformCatalog
{
    public static readonly IReadOnlyList<PlatformSite> Sites =
    [
        new("mooc", "中国大学 MOOC", "icourse163.org", "官方手机号 / 邮箱 / 微信登录", "登录后自动同步课程、作业、测验及考试；覆盖范围以同步详情为准"),
        new("chaoxing", "学习通", "chaoxing.com / xuexitong.com", "官方扫码 / 账号登录", "后台发现课程并同步作业；覆盖范围以最近同步结果为准"),
        new("changjiang", "长江雨课堂", "changjiang.yuketang.cn", "官方扫码 / 账号登录", "登录后自动采集课程、作业与测验；实际覆盖范围见同步详情"),
        new("yuketang", "雨课堂", "www.yuketang.cn / pro.yuketang.cn", "官方扫码 / 账号登录", "登录后自动采集课程、作业与测验；实际覆盖范围见同步详情"),
        new("ucampus", "U 校园", "u.unipus.cn", "官方登录", "可手动录入；自动采集未适配"),
        new("fif", "Fi 口语", "fifedu.com", "官方登录", "可手动录入；自动采集未适配"),
        new("xuetang", "学堂在线", "xuetangx.com", "官方登录", "可手动录入；自动采集未适配"),
        new("zhihuishu", "智慧树", "zhihuishu.com", "官方登录", "可手动录入；自动采集未适配")
    ];
    public static string Name(string id) => Sites.FirstOrDefault(site => site.Id == id)?.Name ?? id;
}

public sealed class TaskValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class TaskRules
{
    public const string ExtensionText = "老师您好，因个人原因本次作业未能按时提交，恳请老师批准延期，感谢老师。";
    public const string Disclaimer = "本项目仅对网课平台任务信息进行聚合展示，仅供参考；截止时间与任务状态来自第三方平台，可能存在抓取错误或更新延迟，最终解释权归原平台所有。作业判定与提交请以对应网课官方平台为准。本工具不提供代答题、代提交功能，也不对任务遗漏或逾期承担责任。";

    public static string StateName(TaskState state) => state switch
    {
        TaskState.Pending => "未完成", TaskState.Completed => "已完成",
        TaskState.Ignored => "已忽略", TaskState.Returned => "被打回", TaskState.NeedsReview => "待核对", _ => "未知"
    };

    public static bool IsActive(TaskItem task) => task.State is TaskState.Pending or TaskState.Returned or TaskState.NeedsReview;
    public static bool IsCollected(TaskItem task) => task.Source != TaskSource.Manual;

    public static bool CanComplete(TaskItem task) => task.Source == TaskSource.Manual
        || task.ProgressPercent is null or 100m;

    public static TaskItem Transition(TaskItem task, TaskState next)
    {
        if (!Enum.IsDefined(next)) throw new TaskValidationException("TASK001", "任务状态无效。");
        if (next == task.State) return task;
        if (next == TaskState.Returned && task.State != TaskState.Completed)
            throw new TaskValidationException("TASK002", "请先选择已完成任务，再标记被打回。");
        if (next == TaskState.Completed && !CanComplete(task))
            throw new TaskValidationException("TASK003", "平台进度不足 100%，请核对或修正进度后再标记完成。");
        return task with { State = next };
    }

    public static bool IsSafeLink(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 16384
            || address.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '"' or '\'' or '<' or '>' or '\\'))
            return false;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host);
    }

    public static void Validate(TaskDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Trim().Length > 300)
            throw new TaskValidationException("TASK001", "请填写任务名称，最多 300 字。");
        if (!PlatformCatalog.Sites.Any(site => site.Id == draft.PlatformId))
            throw new TaskValidationException("TASK001", "请选择来源平台。");
        if (draft.CourseName.Length > 200 || draft.Notes.Length > 4000 || draft.ScoreText.Length > 200)
            throw new TaskValidationException("TASK001", "课程或成绩最多 200 字，备注最多 4000 字。");
        if (!Enum.IsDefined(draft.DeadlineKind)
            || (draft.DeadlineKind == DeadlineKind.Known) != draft.DeadlineUtc.HasValue)
            throw new TaskValidationException("TIME001", "请填写完整截止时间，或选择未知 / 无截止时间。");
        if (draft.DeadlineUtc is { } date && (date <= DateTimeOffset.MinValue.AddDays(1) || date >= DateTimeOffset.MaxValue.AddDays(-1)))
            throw new TaskValidationException("TIME001", "截止时间超出提醒支持范围，请核对年份。");
        if (draft.BeginUtc.HasValue && draft.DeadlineUtc.HasValue && draft.BeginUtc > draft.DeadlineUtc)
            throw new TaskValidationException("TIME001", "开放时间不能晚于截止时间。");
        if (draft.ProgressPercent is < 0 or > 100)
            throw new TaskValidationException("TASK003", "进度必须是 0–100 的百分比；成绩请填在单独的成绩栏。");
        if (draft.PriorityOverride is < 1 or > 4)
            throw new TaskValidationException("TASK001", "手动优先级只能选择 P1–P4 或自动。");
        if (!string.IsNullOrEmpty(draft.Link) && !IsSafeLink(draft.Link))
            throw new TaskValidationException("LINK001", "请填写完整 HTTPS 作业地址，去掉地址后面的 HTML 属性。");
    }

    public static TaskItem Create(TaskDraft draft, DateTimeOffset now)
    {
        Validate(draft);
        return ApplyDraft(new TaskItem { CreatedAtUtc = now.ToUniversalTime() }, draft, now)
            with { Overrides = TaskOverrides.None };
    }

    public static TaskItem ApplyDraft(TaskItem task, TaskDraft draft, DateTimeOffset now)
    {
        Validate(draft);
        if (IsCollected(task) && task.State == TaskState.Completed && draft.ProgressPercent is not null and not 100m
            && task.PlatformStateEvidence != "explicit")
            throw new TaskValidationException("TASK003", "已完成任务的进度不足 100%，请先恢复未完成再修正进度。");
        var overrides = task.Overrides;
        if (IsCollected(task))
        {
            if (task.Title != draft.Title.Trim()) overrides |= TaskOverrides.Title;
            if (task.CourseName != draft.CourseName.Trim()) overrides |= TaskOverrides.Course;
            if (draft.ConfirmDeadline || task.DeadlineKind != draft.DeadlineKind || task.DeadlineUtc != draft.DeadlineUtc) overrides |= TaskOverrides.Deadline;
            if (task.BeginUtc != draft.BeginUtc) overrides |= TaskOverrides.Begin;
            if (task.Link != draft.Link) overrides |= TaskOverrides.Link;
            if (task.ProgressPercent != draft.ProgressPercent) overrides |= TaskOverrides.Progress;
            if (task.ScoreText != draft.ScoreText.Trim()) overrides |= TaskOverrides.Score;
            if (task.PriorityOverride != draft.PriorityOverride) overrides |= TaskOverrides.Priority;
        }
        if (IsCollected(task) && draft.PlatformId != task.PlatformId)
            throw new TaskValidationException("TASK001", "采集任务的来源平台不能修改。");
        return task with
        {
            Title = draft.Title.Trim(), PlatformId = draft.PlatformId, CourseName = draft.CourseName.Trim(),
            DeadlineKind = draft.DeadlineKind, BeginUtc = draft.BeginUtc?.ToUniversalTime(),
            DeadlineUtc = draft.DeadlineUtc?.ToUniversalTime(), Link = draft.Link,
            LinkIssue = task.Link == draft.Link ? task.LinkIssue : null,
            ProgressPercent = draft.ProgressPercent, ScoreText = draft.ScoreText.Trim(),
            Notes = draft.Notes.Trim(), PriorityOverride = draft.PriorityOverride,
            Overrides = overrides, UpdatedAtUtc = now.ToUniversalTime()
        };
    }

    public static bool HasTrustedDeadline(TaskItem task) => task.DeadlineKind == DeadlineKind.Known
        && task.DeadlineUtc.HasValue
        && (task.Source == TaskSource.Manual || task.Overrides.HasFlag(TaskOverrides.Deadline)
            || task.DeadlineEvidence == DeadlineEvidence.PlatformDate);

    public static bool HasUsableDeadline(TaskItem task) => HasTrustedDeadline(task)
        || task.DeadlineKind == DeadlineKind.Known && task.DeadlineUtc.HasValue
            && task.DeadlineEvidence is DeadlineEvidence.InferredYear or DeadlineEvidence.RemainingTime;

    public static int Priority(TaskItem task, DateTimeOffset now)
    {
        if (task.DeadlineKind == DeadlineKind.None && (task.Source == TaskSource.Manual || task.Overrides.HasFlag(TaskOverrides.Deadline)))
            return task.PriorityOverride ?? 3;
        if (!HasUsableDeadline(task)) return 4;
        var remaining = task.DeadlineUtc!.Value - now;
        if (remaining <= TimeSpan.Zero) return 0;
        if (task.PriorityOverride.HasValue) return task.PriorityOverride.Value;
        if (remaining <= TimeSpan.FromHours(2)) return 1;
        if (remaining <= TimeSpan.FromHours(24)) return 2;
        return 3;
    }

    public static string DeadlineName(TaskItem task) => task.DeadlineKind switch
    {
        DeadlineKind.None => "无截止时间",
        DeadlineKind.Known when task.DeadlineUtc is { } date =>
            (task.DeadlineEvidence == DeadlineEvidence.RemainingTime && !task.Overrides.HasFlag(TaskOverrides.Deadline) ? "约 " : "")
            + date.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            + (task.DeadlineEvidence == DeadlineEvidence.InferredYear && !task.Overrides.HasFlag(TaskOverrides.Deadline) ? "（年份推定）" : ""),
        _ => IsCollected(task) ? "平台未提供" : "待填写"
    };

    public static string Confidence(TaskItem task) => task.Source == TaskSource.Manual ? "手动录入"
        : (task.Overrides.HasFlag(TaskOverrides.Deadline) ? "截止时间已手动确认；"
            : task.DeadlineEvidence switch
            {
                DeadlineEvidence.PlatformDate => "平台完整日期，按北京时间；",
                DeadlineEvidence.InferredYear => "按北京时间读取，年份推定；",
                DeadlineEvidence.RemainingTime => "按平台剩余时长估算；",
                _ => "平台未提供截止时间；"
            }) + (task.Overrides.HasFlag(TaskOverrides.State) ? "状态已手动修改；" : "状态随平台同步；")
            + "来自" + PlatformCatalog.Name(task.PlatformId) + "；覆盖范围见最近同步结果"
            + (task.AutomaticStatusExplanation.Length > 0 ? "；" + task.AutomaticStatusExplanation : "");

    public static DateTimeOffset? ParseEditedLocalTime(string text, DateTimeOffset? original) =>
        original is { } value && text.Trim() == value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            ? original : ParseLocalTime(text);

    public static DateTimeOffset? ParseLocalTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value))
            throw new TaskValidationException("TIME001", "时间格式为 yyyy-MM-dd HH:mm，例如 2026-10-25 23:59。");
        value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(value) || TimeZoneInfo.Local.IsAmbiguousTime(value))
            throw new TaskValidationException("TIME001", "此本地时间处于夏令时切换区间，请选择明确时间。");
        return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value)).ToUniversalTime();
    }
}
