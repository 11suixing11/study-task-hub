using System.Text.Json;
namespace AccessProbe;

public sealed partial class TaskStore
{
    internal void SaveLinuxPreferences(AppSettings requested)
    {
        if(requested is null)throw new TaskValidationException("APP001","设置字段不完整。");
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();
            var latest=ReadSettings(connection,transaction);
            var updated=latest with{NotificationsEnabled=requested.NotificationsEnabled,
                AutomaticSyncEnabled=requested.AutomaticSyncEnabled,ChaoxingAutomaticSyncEnabled=requested.ChaoxingAutomaticSyncEnabled,
                DisclaimerSeen=latest.DisclaimerSeen||requested.DisclaimerSeen};
            Execute(connection,transaction,"INSERT INTO settings(key,value) VALUES('app',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                ("$value",JsonSerializer.Serialize(updated)));
            transaction.Commit();
        }
        Changed?.Invoke();
    }
}
