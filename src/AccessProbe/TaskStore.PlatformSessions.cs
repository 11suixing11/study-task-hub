using Microsoft.Data.Sqlite;

namespace AccessProbe;

public sealed record PlatformSessionInfo(string PlatformId, string State, DateTimeOffset? SavedAtUtc, DateTimeOffset? CheckedAtUtc)
{
    public bool CanReuse => State is "saved" or "ready";
    public string Description => State switch
    {
        "ready" => "登录已确认 · 下次自动复用",
        "saved" => "登录已保存 · 使用时自动校验",
        "expired" => "登录已过期 · 请重新登录",
        _ => "尚未保存登录 · 请连接平台"
    };
}

public sealed partial class TaskStore
{
    private static void CheckSessionPlatform(string platformId)
    {
        if (!CourseRecordingRules.SupportsAutomatic(platformId))
            throw new TaskValidationException("SESSION001", "该平台未启用登录保存。");
    }

    public PlatformSessionInfo LoadPlatformSessionInfo(string platformId)
    {
        CheckSessionPlatform(platformId);
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT state,saved_utc,checked_utc FROM platform_sessions WHERE platform_id=$platform;", ("$platform", platformId));
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(platformId, reader.GetString(0),
                reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)))
                : new(platformId, "not_saved", null, null);
        }
    }

    internal (bool Exists, byte[]? Data) ReadProtectedPlatformSession(string platformId)
    {
        CheckSessionPlatform(platformId);
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT protected_cookies FROM platform_sessions WHERE platform_id=$platform;", ("$platform", platformId));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (true, reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0)) : (false, null);
        }
    }

    internal void WriteProtectedPlatformSession(string platformId, byte[] data, DateTimeOffset now)
    {
        CheckSessionPlatform(platformId);
        if (data.Length is 0 or > 2 * 1024 * 1024 + 4096)
            throw new System.IO.InvalidDataException("Protected session size is invalid.");
        lock (gate)
        {
            using var connection = Open();
            Execute(connection, null, """
                INSERT INTO platform_sessions(platform_id,protected_cookies,saved_utc,state)
                VALUES($platform,$cookies,$saved,'saved')
                ON CONFLICT(platform_id) DO UPDATE SET protected_cookies=excluded.protected_cookies,
                saved_utc=excluded.saved_utc,state=CASE WHEN platform_sessions.state='ready' THEN 'ready' ELSE 'saved' END;
                """, ("$platform", platformId), ("$cookies", data), ("$saved", now.ToUnixTimeMilliseconds()));
        }
        Changed?.Invoke();
    }

    public void ConfirmPlatformSession(string platformId, DateTimeOffset now)
    {
        CheckSessionPlatform(platformId);
        lock (gate)
        {
            using var connection = Open();
            Execute(connection, null,
                "UPDATE platform_sessions SET state='ready',checked_utc=$checked WHERE platform_id=$platform AND protected_cookies IS NOT NULL;",
                ("$platform", platformId), ("$checked", now.ToUnixTimeMilliseconds()));
        }
        Changed?.Invoke();
    }

    public void ExpirePlatformSession(string platformId, DateTimeOffset now, bool preserveForRevalidation = false)
    {
        CheckSessionPlatform(platformId);
        lock (gate)
        {
            using var connection = Open();
            Execute(connection, null, """
                INSERT INTO platform_sessions(platform_id,state,checked_utc) VALUES($platform,'expired',$checked)
                ON CONFLICT(platform_id) DO UPDATE SET protected_cookies=CASE WHEN $preserve THEN platform_sessions.protected_cookies ELSE NULL END,
                state='expired',checked_utc=excluded.checked_utc;
                """, ("$platform", platformId), ("$checked", now.ToUnixTimeMilliseconds()),
                ("$preserve", preserveForRevalidation && platformId is "ucampus" or "zhihuishu"));
        }
        Changed?.Invoke();
    }

    public void ClearPlatformSession(string platformId)
    {
        CheckSessionPlatform(platformId);
        lock (gate)
        {
            using var connection = Open();
            Execute(connection, null, "DELETE FROM platform_sessions WHERE platform_id=$platform;", ("$platform", platformId));
        }
        Changed?.Invoke();
    }
}
