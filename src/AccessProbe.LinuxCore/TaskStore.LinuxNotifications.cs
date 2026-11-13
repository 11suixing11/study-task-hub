using AccessProbe.Linux;
using System.Text.Json;
namespace AccessProbe;
public sealed partial class TaskStore
{
    internal void RecordLinuxNotificationRequest(IReadOnlyList<string> ids,bool success,string? errorCode,DateTimeOffset now)
    {
        lock(gate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();
            foreach(var id in ids)Execute(connection,transaction,"""
                UPDATE reminders SET state=$state,requested_utc=$requested,lease_until_utc=NULL,
                    next_attempt_utc=$retry,error_code=$error WHERE id=$id AND state='processing';
                """,("$state",success?"requested":"scheduled"),("$requested",success?now.ToUnixTimeMilliseconds():null),
                ("$retry",success?null:now.AddMinutes(1).ToUnixTimeMilliseconds()),("$error",errorCode),("$id",id));
            transaction.Commit();
        }
    }
    internal int RetryLinuxNotifications()
    {
        lock(gate){using var connection=Open();using var command=Command(connection,null,"UPDATE reminders SET next_attempt_utc=NULL WHERE state='scheduled' AND error_code IS NOT NULL;");return command.ExecuteNonQuery();}
    }
    internal NotificationTestResult? LoadLinuxNotificationTest()
    {
        lock(gate)
        {
            using var connection=Open();using var command=Command(connection,null,"SELECT value FROM settings WHERE key='linux-notification-test';");
            if(command.ExecuteScalar() is not string json)return null;
            try{return JsonSerializer.Deserialize<NotificationTestResult>(json)??throw new JsonException();}
            catch(JsonException){throw new TaskValidationException("DB002","通知测试记录格式不正确，请先备份任务库。");}
        }
    }
    internal void SaveLinuxNotificationTest(NotificationTestResult result)
    {
        lock(gate){using var connection=Open();Execute(connection,null,"INSERT INTO settings(key,value) VALUES('linux-notification-test',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",("$value",JsonSerializer.Serialize(result)));}
    }
}
