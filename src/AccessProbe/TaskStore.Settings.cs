using System.Text.Json;

namespace AccessProbe;

public sealed partial class TaskStore
{
    // UI preference changes can overlap with background synchronization results.
    // Read and write the record in the same transaction to preserve both updates.
    public AppSettings UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings updated;
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            updated = change(ReadSettings(connection, transaction));
            ArgumentNullException.ThrowIfNull(updated);
            Execute(connection, transaction,
                "INSERT INTO settings(key,value) VALUES('app',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                ("$value", JsonSerializer.Serialize(updated)));
            transaction.Commit();
        }
        Changed?.Invoke();
        return updated;
    }
}
