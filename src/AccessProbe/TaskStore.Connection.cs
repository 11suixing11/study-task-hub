using System.Text.Json;

namespace AccessProbe;

public sealed partial class TaskStore
{
    private static string ConnectionReportKey(string platformId) => "connection-report:" + platformId;

    public ConnectionReport? LoadConnectionReport(string platformId)
    {
        CheckSessionPlatform(platformId);
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT value FROM settings WHERE key=$key;", ("$key", ConnectionReportKey(platformId)));
            if (command.ExecuteScalar() is not string json) return null;
            try
            {
                var report = JsonSerializer.Deserialize<ConnectionReport>(json);
                return report is not null && report.PlatformId == platformId && report.CheckedAtUtc != default ? SanitizeConnectionReport(report) : null;
            }
            catch (JsonException) { return null; }
        }
    }

    public ConnectionReport SaveConnectionReport(ConnectionReport report, CancellationToken cancellation = default)
    {
        CheckSessionPlatform(report.PlatformId);
        report = SanitizeConnectionReport(report);
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            using var connection = Open();
            Execute(connection, null, "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                ("$key", ConnectionReportKey(report.PlatformId)), ("$value", JsonSerializer.Serialize(report)));
        }
        Changed?.Invoke();
        return report;
    }

    private static ConnectionReport SanitizeConnectionReport(ConnectionReport report) => report with
    {
        AccountVerified = report.AccountVerified && !report.LoginRequired && report.SessionAvailable,
        Courses = Math.Clamp(report.Courses, 0, 100000), SelectedCourses = Math.Clamp(report.SelectedCourses, 0, 100000),
        Tasks = Math.Clamp(report.Tasks, 0, 1000000),
        SupportedTaskListsCovered = report.SupportedTaskListsCovered || report.TaskListsCovered,
        StatusNeedsReview = Math.Clamp(report.StatusNeedsReview, 0, 100000),
        SmartOtherTasks = Math.Clamp(report.SmartOtherTasks, 0, 100000),
        ProblemCodes = (report.ProblemCodes ?? []).Where(CourseRecordingRules.IsCatalogProblemCode).Distinct().Take(16).ToArray(),
        ProblemOutcomes = (report.ProblemOutcomes ?? []).Where(value => value is { Length: > 0 and <= 64 }
            && ConnectionReportOutcomes.Contains(value)).Distinct().Take(16).ToArray()
    };

    private static readonly HashSet<string> ConnectionReportOutcomes = new(StringComparer.Ordinal)
    {
        "login_required", "network_error", "network_failed", "certificate_failed", "timeout", "http_error", "http_failed",
        "rate_limited", "request_limit", "temporarily_unavailable", "forbidden", "course_access_denied",
        "verification_required", "captcha_required", "unsupported_redirect", "blocked_navigation", "csrf_rejected", "csrf_missing",
        "course_credentials_unavailable", "session_unavailable", "course_session_expired", "session_check_unavailable",
        "script_failed", "style_failed", "blank_page", "renderer_failed", "environment_failed", "account_unrecognized",
        "unrecognized_user", "unrecognized_response", "unrecognized_authorization", "unrecognized_encryption", "schema_unrecognized",
        "unrecognized_catalog", "unrecognized_profile", "unrecognized_task", "unrecognized_tasks", "unrecognized_schedule", "unrecognized_chapter", "unrecognized_reset_times",
        "unrecognized", "partial", "partial_catalog", "parsed_partial", "partial_tasks", "partial_learning_unit", "unknown_structure",
        "page_limit", "course_limit", "response_too_large", "pagination_loop", "pagination_changed", "pagination_inconsistent",
        "pagination_unrecognized", "repeated_page", "premature_empty_page", "catalog_count_mismatch", "catalog_changed",
        "unsupported_courses", "unsupported_content_scope", "unsupported_request", "content_version_mismatch", "unit_catalog_changed",
        "task_list_unrecognized", "task_list_incomplete", "detail_requires_preview", "detail_requires_user_action",
        "submitted_prompt_without_time_window", "reset_scope_missing", "reset_times_unverified", "smart_other_tasks_unverified",
        "account_mismatch", "classroom_mismatch", "conflicting_course", "unverified", "incomplete", "navigation_timeout", "overview_truncated"
    };
}
