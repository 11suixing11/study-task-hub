using System.Text.Json;

namespace AccessProbe;

public sealed partial class TaskStore
{
    public UaiPortalSummary? LoadUaiPortalSummary()
    {
        lock (gate)
        {
            using var connection = Open(); using var command = Command(connection, null, "SELECT value FROM settings WHERE key='platform:uai-preview';");
            if (command.ExecuteScalar() is not string json) return null;
            try
            {
                var result = JsonSerializer.Deserialize<UaiPortalSummary>(json) ?? throw new JsonException();
                if (!IsUaiSummary(result)) throw new JsonException();
                return result;
            }
            catch (JsonException) { throw new TaskValidationException("DB002", "U 校园 AI 版概要格式不正确，已有任务保留。"); }
        }
    }
    public void SaveUaiPortalSummary(UaiPortalSummary summary, CancellationToken cancellation)
    {
        if (!IsUaiSummary(summary))
            throw new TaskValidationException("UAI003", "概要结果无法确认。");
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested(); using var connection = Open(); using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "INSERT INTO settings(key,value) VALUES('platform:uai-preview',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                ("$value", JsonSerializer.Serialize(summary)));
            cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }
        Changed?.Invoke();
    }

    private static bool IsUaiSummary(UaiPortalSummary summary) => summary.CheckedAtUtc != default
        && summary.Codes is { Count: <= 16 } && summary.Codes.All(CourseRecordingRules.IsCatalogProblemCode)
        && summary.Outcome is "partial" or "login_required" && (summary.Outcome != "login_required" || !summary.AccountVerified)
        && summary.ShelfResources is null or >= 0 and <= 10000 && summary.OverviewTasks is null or >= 0
        && summary.DisplayedTasks is null or >= 0 and <= 10000
        && (summary.DisplayedTasks is null || summary.OverviewTasks is { } total && summary.DisplayedTasks <= total);
}
