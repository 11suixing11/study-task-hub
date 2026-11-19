namespace AccessProbe;
public sealed partial class TaskStore
{
    internal void WriteNewLinuxSession(string platform,byte[] data,DateTimeOffset now)
    {
        CheckSessionPlatform(platform);
        if(data.Length is 0 or >2*1024*1024+4096)throw new TaskValidationException("SESSION001","会话超出大小限制。");
        lock(gate)
        {
            using var connection=Open();
            Execute(connection,null,"""
                INSERT INTO platform_sessions(platform_id,protected_cookies,saved_utc,state,checked_utc)
                VALUES($platform,$data,$now,'saved',NULL)
                ON CONFLICT(platform_id) DO UPDATE SET protected_cookies=excluded.protected_cookies,
                saved_utc=excluded.saved_utc,state='saved',checked_utc=NULL;
                """,("$platform",platform),("$data",data),("$now",now.ToUnixTimeMilliseconds()));
        }
        Changed?.Invoke();
    }
}
