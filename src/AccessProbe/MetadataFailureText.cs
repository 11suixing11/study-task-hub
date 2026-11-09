namespace AccessProbe;

public static class MetadataFailureText
{
    public static string Describe(string outcome, int? status = null)
    {
        var detail = outcome switch
        {
            "network_error" => "网络连接失败，请检查网络后重试",
            "timeout" => "平台响应超时，请稍后重试",
            "http_error" => "平台服务请求失败，请稍后重试",
            "unsupported_redirect" => "接口返回尚未适配的跳转，请在连接窗口打开官网后刷新",
            "course_access_denied" => "平台拒绝此项读取，当前登录保留，请在官网核对课程访问权限",
            "course_credentials_unavailable" => "课程页面的登录令牌尚未就绪，请在连接窗口打开学生课程主页后刷新",
            "course_session_expired" => "平台账号登录仍有效，此课程服务尚未恢复；已重试并保留登录，请稍后同步或打开该课程",
            "session_check_unavailable" => "课程服务登录未确认，账号复核未完成；当前登录保留，请稍后重试",
            "session_unavailable" => "当前登录凭据无法读取，请重新打开连接窗口",
            "login_required" => "官方接口未确认有效登录，请在连接窗口完成登录并等待返回课程主页",
            "unrecognized_user" => "官方账号返回未能识别，当前登录保留",
            "unrecognized_response" => "官方返回结构尚未确认，当前登录保留",
            "unsupported_content_scope" => "此课件类型或版本尚未适配单元学习；作业、测试仍按独立列表采集",
            "partial_learning_unit" => "本单元的必学状态、截止时间或入口尚未完整确认，请在官网核对",
            "unit_catalog_changed" => "单元目录与学习进度不一致，已有单元任务保留，请稍后同步",
            "content_version_mismatch" => "课件目录与进度版本不一致，已有单元任务保留，请稍后同步",
            "account_mismatch" => "官方返回与当前账号不一致，已停止本项读取",
            "response_too_large" => "平台返回超过读取上限，本项覆盖未确认",
            "rate_limited" => "平台限制请求频率，请稍后重试",
            _ => "此次读取未完整确认，已有任务和课程选择保留"
        };
        return detail + (status is >= 100 and <= 599 ? "（HTTP " + status + "）" : "");
    }
}
