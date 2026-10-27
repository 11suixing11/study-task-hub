using System.Globalization;
using System.Text.RegularExpressions;

namespace AccessProbe;

public enum DeadlineEvidence { Unknown, PlatformDate, InferredYear, RemainingTime }
public sealed record PlatformTaskTime(DateTimeOffset? BeginUtc, DateTimeOffset DeadlineUtc, DeadlineEvidence Evidence);

public static class ChaoxingTaskMetadata
{
    // Chaoxing's desktop dates use Beijing time. Year inference and countdowns remain visibly marked as estimates.
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private static readonly Regex Remaining = new(
        @"^剩余\s*(?:(?<days>[0-9]{1,5})\s*天\s*)?(?:(?<hours>[0-9]{1,5})\s*小时\s*)?(?:(?<minutes>[0-9]{1,5})\s*分钟\s*)?(?:(?<seconds>[0-9]{1,5})\s*秒\s*)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static TaskState? ReadState(string text) => string.Concat(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) switch
    {
        "已完成" or "已提交" or "已交" or "待批阅" or "已批阅" or "已批改" or "已提交待批阅" => TaskState.Completed,
        "未交" or "未完成" or "未提交" or "待提交" or "待完成" or "进行中" or "作答中" or "未开始" => TaskState.Pending,
        "被打回" or "已打回" or "退回重做" or "需重做" => TaskState.Returned,
        _ => null
    };

    public static PlatformTaskTime? ReadRemaining(string text, DateTimeOffset observedAt)
    {
        var match = Remaining.Match(text.Trim());
        if (!match.Success || !new[] { "days", "hours", "minutes", "seconds" }.Any(name => match.Groups[name].Success)) return null;
        double Part(string name) => match.Groups[name].Success
            ? double.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        var duration = TimeSpan.FromDays(Part("days")) + TimeSpan.FromHours(Part("hours"))
            + TimeSpan.FromMinutes(Part("minutes")) + TimeSpan.FromSeconds(Part("seconds"));
        if (duration > TimeSpan.FromDays(730)) return null;
        return new(null, observedAt.ToUniversalTime().Add(duration), DeadlineEvidence.RemainingTime);
    }

    public static PlatformTaskTime? ReadWindow(WorkAnswerWindow window, DateTimeOffset observedAt, TaskState state,
        string remainingText = "", DateTimeOffset? remainingObservedAt = null)
    {
        var reference = observedAt.ToOffset(BeijingOffset);
        var countdown = ReadRemaining(remainingText, remainingObservedAt ?? observedAt);
        var candidates = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var endYears = window.End.Year.HasValue ? new[] { window.End.Year.Value }
            : window.Start.Year.HasValue ? new[] { window.Start.Year.Value, window.Start.Year.Value + 1 }
            : new[] { reference.Year - 1, reference.Year, reference.Year + 1 };
        foreach (var endYear in endYears)
        {
            var end = Date(window.End, endYear);
            if (end is null) continue;
            var startYear = window.Start.Year ?? endYear;
            var start = Date(window.Start, startYear);
            if (window.Start.Year is null && (start is null || start > end)) start = Date(window.Start, startYear - 1);
            if (start is null || start > end || end - start > TimeSpan.FromDays(370)) continue;
            // A completed work cannot have an inferred opening date in the future.
            if (window.Start.Year is null && state == TaskState.Completed && start > reference) continue;
            candidates.Add((start.Value, end.Value));
        }
        if (candidates.Count == 0) return null;
        var chosen = candidates.OrderBy(value => countdown is not null
                ? Math.Abs((value.End - countdown.DeadlineUtc).TotalSeconds)
                : reference >= value.Start && reference <= value.End ? 0 : Math.Abs((value.End - reference).TotalSeconds))
            .ThenBy(value => Math.Abs(value.End.Year - reference.Year)).First();
        return new(chosen.Start.ToUniversalTime(), chosen.End.ToUniversalTime(),
            window.Start.Year.HasValue && window.End.Year.HasValue ? DeadlineEvidence.PlatformDate : DeadlineEvidence.InferredYear);
    }

    public static TaskItem Apply(TaskItem task, string listTimeText, DateTimeOffset observedAt, bool refreshWindow = false)
    {
        var overrides = task.Overrides;
        // Older releases recorded manual states without an override bit. Preserve their non-default states once.
        if (task.AutomaticMetadataVersion == 0 && task.State != TaskState.Pending) overrides |= TaskOverrides.State;
        var platformState = ReadState(task.PlatformStatusText);
        var state = overrides.HasFlag(TaskOverrides.State) ? task.State : platformState ?? task.State;
        var parsed = task.DetailTimeText.Length > 0 ? WorkDetailParser.ParseDisplayedWindow(task.DetailTimeText) : null;
        var countdown = ReadRemaining(listTimeText, observedAt);
        PlatformTaskTime? time = null;
        if (parsed?.AnswerWindow is { } window)
        {
            // Reusing a yearless detail header must not roll its inferred date into a new year.
            var reuseWindow = !refreshWindow && countdown is null && task.AutomaticWindowText == task.DetailTimeText
                && task.DeadlineKind == DeadlineKind.Known && task.DeadlineUtc.HasValue
                && !overrides.HasFlag(TaskOverrides.Deadline)
                && task.DeadlineEvidence is DeadlineEvidence.PlatformDate or DeadlineEvidence.InferredYear;
            time = reuseWindow ? new(task.BeginUtc, task.DeadlineUtc!.Value, task.DeadlineEvidence)
                : ReadWindow(window, countdown is not null && !refreshWindow ? observedAt : task.DetailCollectedAtUtc ?? observedAt,
                    platformState ?? state, listTimeText, observedAt);
        }
        time ??= countdown;
        return task with
        {
            State = state, Overrides = overrides, AutomaticMetadataVersion = 1,
            PlatformListTimeText = listTimeText, PlatformListTimeObservedAtUtc = observedAt.ToUniversalTime(),
            AutomaticWindowText = parsed?.AnswerWindow is not null
                && time?.Evidence is DeadlineEvidence.PlatformDate or DeadlineEvidence.InferredYear
                    ? task.DetailTimeText : task.AutomaticWindowText,
            BeginUtc = !overrides.HasFlag(TaskOverrides.Begin) && time?.BeginUtc is { } begin ? begin : task.BeginUtc,
            DeadlineKind = !overrides.HasFlag(TaskOverrides.Deadline) && time is not null ? DeadlineKind.Known : task.DeadlineKind,
            DeadlineUtc = !overrides.HasFlag(TaskOverrides.Deadline) && time is not null ? time.DeadlineUtc : task.DeadlineUtc,
            DeadlineEvidence = !overrides.HasFlag(TaskOverrides.Deadline) && time is not null ? time.Evidence : task.DeadlineEvidence
        };
    }

    private static DateTimeOffset? Date(WorkDisplayedTime value, int year)
    {
        if (year is < 1 or > 9999 || value.Month is < 1 or > 12 || value.Day < 1
            || value.Day > DateTime.DaysInMonth(year, value.Month) || value.Hour is < 0 or > 23 || value.Minute is < 0 or > 59) return null;
        return new DateTimeOffset(year, value.Month, value.Day, value.Hour, value.Minute, 0, BeijingOffset);
    }
}
