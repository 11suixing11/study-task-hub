using System.Security.Cryptography;
using System.Text;

namespace AccessProbe;

public enum ReminderKind { NewTask, DueSoon, Overdue }
public sealed record ReminderPlan(string Id, string TaskId, ReminderKind Kind, DateTimeOffset DueAtUtc, DateTimeOffset? DeadlineUtc);
public sealed record DueReminder(ReminderPlan Plan, TaskItem Task, int Attempts);
public sealed record ReminderHistory(ReminderPlan Plan, string State, DateTimeOffset? RequestedAtUtc, string? ErrorCode, int Attempts);

public static class ReminderPlanner
{
    public static IReadOnlyList<ReminderPlan> TimedPlans(TaskItem task, DateTimeOffset now)
    {
        if (!TaskRules.IsActive(task) || !TaskRules.HasTrustedDeadline(task)) return [];
        var deadline = task.DeadlineUtc!.Value.ToUniversalTime();
        var plans = new List<ReminderPlan>();
        // A reminder about an approaching deadline is no longer useful once it has passed.
        if (now <= deadline)
            plans.Add(Make(task.Id, ReminderKind.DueSoon, deadline - TimeSpan.FromHours(24), deadline));
        plans.Add(Make(task.Id, ReminderKind.Overdue, deadline + TimeSpan.FromHours(2), deadline));
        return plans;
    }

    public static ReminderPlan NewTask(TaskItem task, DateTimeOffset now) => Make(task.Id, ReminderKind.NewTask, now, null);

    public static bool IsCurrent(ReminderPlan plan, TaskItem task, DateTimeOffset now) =>
        TaskRules.IsActive(task) && (plan.Kind == ReminderKind.NewTask
            || TaskRules.HasTrustedDeadline(task) && plan.DeadlineUtc?.ToUnixTimeMilliseconds() == task.DeadlineUtc?.ToUnixTimeMilliseconds()
               && (plan.Kind != ReminderKind.DueSoon || now <= task.DeadlineUtc));

    private static ReminderPlan Make(string taskId, ReminderKind kind, DateTimeOffset due, DateTimeOffset? deadline)
    {
        var key = taskId + "|" + kind + "|" + (deadline?.ToUnixTimeMilliseconds().ToString() ?? "new");
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))), taskId, kind, due.ToUniversalTime(), deadline);
    }

    public static string KindName(ReminderKind kind) => kind switch
    {
        ReminderKind.NewTask => "新任务", ReminderKind.DueSoon => "截止前 24 小时", ReminderKind.Overdue => "逾期 2 小时", _ => "提醒"
    };
}
