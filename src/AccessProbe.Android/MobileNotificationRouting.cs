namespace AccessProbe.Mobile;

public enum MobileNotificationStatusKind { Ready, PermissionDenied, AppDisabled, ChannelDisabled, ChannelMissing, Unavailable }

public sealed record MobileNotificationStatus(MobileNotificationStatusKind Kind, string Message)
{
    public bool CanRequest => Kind == MobileNotificationStatusKind.Ready;

    public static MobileNotificationStatus FromSignals(bool permissionGranted, bool appEnabled,
        bool channelExists, bool channelEnabled, bool serviceAvailable = true)
    {
        if (!permissionGranted) return For(MobileNotificationStatusKind.PermissionDenied);
        if (!serviceAvailable) return For(MobileNotificationStatusKind.Unavailable);
        if (!appEnabled) return For(MobileNotificationStatusKind.AppDisabled);
        if (!channelExists) return For(MobileNotificationStatusKind.ChannelMissing);
        return For(channelEnabled ? MobileNotificationStatusKind.Ready : MobileNotificationStatusKind.ChannelDisabled);
    }

    public static MobileNotificationStatus For(MobileNotificationStatusKind kind) => new(kind, kind switch
    {
        MobileNotificationStatusKind.Ready => "系统通知与“课程任务提醒”通道已开启。",
        MobileNotificationStatusKind.PermissionDenied => "通知权限未开启，系统会阻止课程任务提醒。请允许通知权限，或进入系统通知设置开启。",
        MobileNotificationStatusKind.AppDisabled => "系统已关闭本应用的通知，请在系统通知设置中开启。",
        MobileNotificationStatusKind.ChannelDisabled => "“课程任务提醒”通道已关闭，请在系统通知设置中开启该通道。",
        MobileNotificationStatusKind.ChannelMissing => "课程任务提醒通道尚未建立，请重新打开应用后重试。",
        _ => "暂时无法读取系统通知状态，请进入系统通知设置核对。"
    });
}

public enum MobileNotificationRequestOutcome { Requested, Blocked, Failed }

public sealed record MobileNotificationRequestResult(MobileNotificationRequestOutcome Outcome, string Message)
{
    public bool Requested => Outcome == MobileNotificationRequestOutcome.Requested;
}

public static class MobileNotificationRequests
{
    // A successful Notify call only acknowledges the request; it cannot confirm that the user saw it.
    public static MobileNotificationRequestResult Attempt(MobileNotificationStatus status, Action request, bool test = false)
    {
        if (!status.CanRequest) return new(MobileNotificationRequestOutcome.Blocked, status.Message);
        try
        {
            request();
            return new(MobileNotificationRequestOutcome.Requested, test
                ? "已向系统请求测试提醒，请查看通知栏确认。若未显示，可检查系统通知设置。"
                : "已向系统请求课程任务提醒。");
        }
        catch
        {
            return new(MobileNotificationRequestOutcome.Failed, test
                ? "测试提醒未能交给系统，请检查系统通知设置后重试。"
                : "提醒未能交给系统，将保留计划稍后重试。");
        }
    }
}

public enum MobileNotificationRouteOutcome { None, Invalid, Deferred, Open, Missing, Unavailable }

public sealed record MobileNotificationRouteResult(MobileNotificationRouteOutcome Outcome, string? TaskId, string Message);

/// <summary>Retains the most recently clicked task while a login, edit, or ongoing operation owns the page.</summary>
public sealed class MobileNotificationRouting
{
    public string? PendingTaskId { get; private set; }
    public bool HasPending => PendingTaskId is not null;

    public MobileNotificationRouteResult Receive(string? taskId)
    {
        if (taskId is null) return new(MobileNotificationRouteOutcome.None, null, "");
        if (taskId.Length != 32 || !taskId.All(char.IsAsciiHexDigit))
            return new(MobileNotificationRouteOutcome.Invalid, null, "这条提醒的任务标识无效，请返回任务清单查找。");
        PendingTaskId = taskId.ToLowerInvariant();
        return new(MobileNotificationRouteOutcome.Deferred, PendingTaskId, "已保留提醒中的任务。");
    }

    public MobileNotificationRouteResult Resolve(bool loginOpen, bool editing, bool busy, Func<string, bool> taskExists)
    {
        if (PendingTaskId is not { } id) return new(MobileNotificationRouteOutcome.None, null, "");
        if (loginOpen) return new(MobileNotificationRouteOutcome.Deferred, id, "已保留提醒中的任务，结束当前登录后会打开。");
        if (editing) return new(MobileNotificationRouteOutcome.Deferred, id, "已保留提醒中的任务，保存或退出当前编辑后会打开。");
        if (busy) return new(MobileNotificationRouteOutcome.Deferred, id, "已保留提醒中的任务，当前操作结束后会打开。");
        try
        {
            if (!taskExists(id))
            {
                PendingTaskId = null;
                return new(MobileNotificationRouteOutcome.Missing, id, "提醒中的任务已不存在，请返回任务清单查看最新任务。");
            }
        }
        catch
        {
            return new(MobileNotificationRouteOutcome.Unavailable, id, "暂时无法读取提醒中的任务，已保留入口，请稍后返回任务清单重试。");
        }
        PendingTaskId = null;
        return new(MobileNotificationRouteOutcome.Open, id, "已打开提醒对应的任务。");
    }
}
