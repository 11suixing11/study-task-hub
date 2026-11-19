using AccessProbe;

namespace AccessProbe.Mobile;

public sealed record MobilePlatformStatus(DateTimeOffset? CheckedAtUtc, string Outcome, string Detail)
{
    public string Description => CheckedAtUtc is { } time ? Detail + " · " + time.ToLocalTime().ToString("MM-dd HH:mm") : Detail;
    public static MobilePlatformStatus Read(TaskStore store, string platform) => platform switch
    {
        "chaoxing" => Make(store.LoadSettings().LastSyncAtUtc, store.LoadSettings().LastSyncOutcome),
        "mooc" => Make(store.LoadMoocSettings().LastSyncAtUtc, store.LoadMoocSettings().LastSyncOutcome),
        "xuetang" => Make(store.LoadXuetangSettings().LastSyncAtUtc, store.LoadXuetangSettings().LastSyncOutcome),
        "yuketang" or "changjiang" => Make(store.LoadPlatformSettings(platform).LastSyncAtUtc, store.LoadPlatformSettings(platform).LastSyncOutcome),
        "ucampus" or "zhihuishu" or "pta" => Make(store.LoadMetadataSettings(platform).LastSyncAtUtc, store.LoadMetadataSettings(platform).LastSyncOutcome),
        "uai" => store.LoadUaiPortalSummary() is { } summary ? new(summary.CheckedAtUtc, summary.Outcome, "AI 版概要预览；完整任务未接入")
            : new(null, "not_started", "AI 版概要尚未读取"),
        _ => new(null, "not_started", "尚未同步")
    };
    private static MobilePlatformStatus Make(DateTimeOffset? time, string outcome) => new(time, outcome, outcome switch
    {
        "completed" => "支持的任务范围已同步",
        "partial" => "任务读取未完整完成，请核对官网",
        "login_required" => "上次任务同步需要登录",
        _ => "尚未完成任务同步"
    });
}
