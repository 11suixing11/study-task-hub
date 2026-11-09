"""Read aggregate sync counters without exporting accounts, task titles or links."""
import argparse
import hashlib
import json
import re
import sqlite3
from collections import Counter
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path


STATES = {0: "pending", 1: "completed", 2: "ignored", 3: "returned", 4: "needs_review"}
EVIDENCE = {0: "unknown", 1: "platform_date", 2: "inferred_year", 3: "remaining_time"}
STATUS_LABELS = {
    "", "未交", "未提交", "未完成", "未开始", "未作答", "未参加", "待提交", "进行中",
    "作答中", "已完成", "已提交", "待批阅", "已批阅", "已结束", "被打回", "需重做",
    "已提交（待批阅）", "已完成（平台题目进度）", "待核对", "状态待核对",
    "已提交 / 待批阅",
    "已批改", "草稿 / 未提交",
    "已完成（平台学习进度）", "进行中（平台学习进度）",
}
PLATFORMS = {"yuketang": "www.yuketang.cn", "changjiang": "changjiang.yuketang.cn", "mooc": "www.icourse163.org", "xuetang": "www.xuetangx.com",
             "ucampus": "u.unipus.cn", "zhihuishu": "onlineweb.zhihuishu.com", "fif": "www.fifedu.com", "pta": "pintia.cn"}
HOSTS = set(PLATFORMS.values()) | {"pro.yuketang.cn"}
RECORDING_PLATFORMS = ("chaoxing", "yuketang", "changjiang", "mooc", "ucampus", "fif", "xuetang", "zhihuishu", "pta")
DOTNET_WHITESPACE = "\t\n\v\f\r \u0085\u00a0\u1680" + "".join(chr(value) for value in range(0x2000, 0x200b)) + "\u2028\u2029\u202f\u205f\u3000"


def dotnet_json_string(value):
    # System.Text.Json's default Basic Latin encoder uses uppercase UTF-16 escapes and HTML-sensitive escaping.
    short = {"\b": r"\b", "\t": r"\t", "\n": r"\n", "\f": r"\f", "\r": r"\r", "\\": r"\\"}
    encoded = []
    for char in value:
        if char in short:
            encoded.append(short[char])
        elif 0x20 <= ord(char) < 0x7f and char not in "\"'&+<>`":
            encoded.append(char)
        else:
            data = char.encode("utf-16-be", errors="surrogatepass")
            encoded.extend("\\u" + data[index:index + 2].hex().upper() for index in range(0, len(data), 2))
    return '"' + "".join(encoded) + '"'


def task_course_key(task):
    platform_id = task.get("PlatformId", "")
    key = task.get("RecordingCourseKey", "")
    if isinstance(key, str) and re.fullmatch(re.escape(platform_id) + r":[0-9A-Fa-f]{64}", key):
        return key
    # Match CourseRecordingRules.TaskCourseKey for manual and pre-catalog tasks.
    identity = ["manual" if task.get("Source") == 0 else "legacy", task.get("CourseName", "").strip(DOTNET_WHITESPACE)]
    serialized = "[" + ",".join(dotnet_json_string(value) for value in identity) + "]"
    digest = hashlib.sha256(serialized.encode("utf-8")).hexdigest().upper()
    return platform_id + ":" + digest


def is_selected(task, settings):
    recording = settings.get("course-recording:" + task.get("PlatformId", ""), {})
    return recording.get("SelectionConfirmed", False) and (
        recording.get("RecordAllCourses", False) or task_course_key(task) in (recording.get("SelectedCourseKeys") or [])
    )


def is_reminder_selected(task, settings):
    recording = settings.get("course-recording:" + task.get("PlatformId", ""), {})
    return task.get("Source") == 0 and not recording.get("SelectionConfirmed", False) or is_selected(task, settings)


def is_visible(task, settings):
    recording = settings.get("course-recording:" + task.get("PlatformId", ""), {})
    return not recording.get("SelectionConfirmed", False) or not recording.get("HideUnselectedTasks", True) or is_selected(task, settings)


def status_label(task):
    label = task.get("PlatformStatusText", "")
    if isinstance(label, str) and label in STATUS_LABELS:
        return label
    if task.get("PlatformId") == "ucampus" and isinstance(label, str):
        matched = re.fullmatch(
            r"(已结束|进行中|未提交|状态待核对)(?: · 重做)?(?: · 互评)?"
            r"（state=(?:-?[0-9]{1,10}|未提供); newState=(?:-?[0-9]{1,10}|未提供)）", label
        )
        if matched:
            return matched[1]
    if task.get("PlatformId") == "pta" and isinstance(label, str):
        matched = re.fullmatch(
            r"(尚未开始|未开始 · 题目集已关闭|答题中|已结束答题|答题已停止 · 完成待核对|状态待核对)"
            r"（status=(?:PENDING|READY|PROCESSING|END|未提供); exam.status=(?:PROCESSING|END|未提供); ended=(?:true|false|未提供)）", label)
        if matched:
            return matched[1]
    return "other"


def priority(task, now):
    overrides = task.get("Overrides", 0)
    manual = task.get("Source") == 0
    kind = task.get("DeadlineKind")
    custom = task.get("PriorityOverride")
    if kind == 2 and (manual or overrides & 4):
        return f"P{custom if custom is not None else 3}"
    deadline = task.get("DeadlineUtc")
    usable = kind == 1 and bool(deadline) and (
        manual or overrides & 4 or task.get("DeadlineEvidence", 0) in (1, 2, 3)
    )
    if not usable:
        return "P4"
    try:
        date = datetime.fromisoformat(deadline)
        if date.tzinfo is None:
            return "P4"
        seconds = (date - now).total_seconds()
    except (ValueError, TypeError):
        return "P4"
    return (
        "P0" if seconds <= 0 else f"P{custom}" if custom is not None
        else "P1" if seconds <= 7200 else "P2" if seconds <= 86400 else "P3"
    )


def stored_counts(rows, now, settings=None):
    settings = settings or {}
    tasks = [task for task, _ in rows]
    visible = [task for task in tasks if is_visible(task, settings)]
    selected = [task for task in tasks if is_selected(task, settings)]
    return {
        "storedTasks": len(rows),
        "storedProtectedLinks": sum(has_link for _, has_link in rows),
        "storedStates": dict(Counter(STATES.get(task.get("State"), "unknown") for task in tasks)),
        "platformStatusCounts": dict(Counter(status_label(task) for task in tasks)),
        "storedDeadlineEvidence": dict(Counter(
            EVIDENCE.get(task.get("DeadlineEvidence", 0), "unknown") for task in tasks
        )),
        "storedDeadlines": sum(bool(task.get("DeadlineUtc")) for task in tasks),
        "storedDetailTimes": sum(bool(task.get("DetailTimeText")) for task in tasks),
        "visibleTasks": len(visible),
        "hiddenTasks": len(tasks) - len(visible),
        "selectedTasks": len(selected),
        "unselectedTasks": len(tasks) - len(selected),
        "activePriorities": dict(Counter(priority(task, now) for task in visible if task.get("State") in (0, 3, 4))),
        "selectedActivePriorities": dict(Counter(priority(task, now) for task in selected if task.get("State") in (0, 3, 4))),
    }


def sync_counts(settings, summary):
    problems = summary.get("Problems") or []
    return {
        "automaticEnabled": settings.get("AutomaticSyncEnabled", True),
        "outcome": settings.get("LastSyncOutcome", "not_started"),
        "checkedAt": settings.get("LastSyncAtUtc"),
        "courses": summary.get("Courses"),
        "selectedCourses": summary.get("SelectedCourses") if summary.get("SelectedCourses") is not None else summary.get("Courses"),
        "coursesRead": summary.get("CoursesRead"),
        "tasksRead": summary.get("Tasks"),
        "added": summary.get("Added"),
        "updated": summary.get("Updated"),
        "loginRequired": summary.get("LoginRequired"),
        "courseListCovered": summary.get("CourseListCovered"),
        "problemCount": len(problems),
        "problemCodes": [
            code for problem in problems for code in [problem.get("Code")]
            if isinstance(code, str) and re.fullmatch(r"(?:SYNC|RAIN|MOOC|XT|UC|ZHS|FIF|PTA|DB)[0-9]{3}", code)
        ],
    }


def recording_counts(settings, tasks=()):
    courses = settings.get("Courses") or []
    confirmed = settings.get("SelectionConfirmed", False)
    record_all = confirmed and settings.get("RecordAllCourses", False)
    selected = set(settings.get("SelectedCourseKeys") or [])
    if "SelectionConfirmed" not in settings and settings.get("RecordAllCourses", False):
        selected = {course.get("Key") for course in courses if course.get("Available", True)}
    refresh = settings.get("LastCatalogRefresh") or {}
    refresh_outcome = refresh.get("Outcome", "not_started")
    refresh_codes = refresh.get("ProblemCodes") or []
    catalog_keys = {course.get("Key") for course in courses}
    local_keys = {task_course_key(task) for task in tasks}
    overview_keys = catalog_keys | local_keys
    return {
        "selectionConfirmed": confirmed,
        "recordAllCourses": record_all,
        "hideUnselectedTasks": settings.get("HideUnselectedTasks", True),
        "revision": settings.get("Revision", 0),
        "catalogCourses": len(courses),
        "overviewCourses": len(overview_keys),
        "retainedOnlyCourses": len(local_keys - catalog_keys),
        "availableCourses": sum(course.get("Available", True) for course in courses),
        "selectedCatalogCourses": sum(confirmed and (record_all or course.get("Key") in selected) for course in courses),
        "selectedOverviewCourses": sum(confirmed and (record_all or key in selected) for key in overview_keys),
        "pendingChoiceCourses": sum(not confirmed and key in selected for key in overview_keys),
        "selectedCourseKeys": len(selected),
        "catalogCheckedAtUtc": settings.get("CatalogCheckedAtUtc"),
        "catalogComplete": settings.get("CatalogComplete", False),
        "lastRefreshAtUtc": refresh.get("CheckedAtUtc"),
        "lastRefreshOutcome": refresh_outcome if refresh_outcome in {"completed", "partial", "login_required", "error", "not_started"} else "unrecognized",
        "lastRefreshProblemCodes": [
            code for code in refresh_codes
            if isinstance(code, str) and re.fullmatch(r"(?:SYNC|RAIN|MOOC|XT|UC|ZHS|FIF|PTA|SESSION|DB|COURSE)[0-9]{3}", code)
        ],
    }


def read_status(database):
    now = datetime.now(timezone.utc)
    with closing(sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)) as connection:
        connection.execute("PRAGMA query_only=ON")
        connection.execute("BEGIN")
        settings = {
            key: json.loads(value) for key, value in connection.execute(
                "SELECT key,value FROM settings WHERE key='app' OR key LIKE 'platform:%' OR key LIKE 'course-recording:%'"
            )
        }
        rows = [
            (json.loads(payload), bool(has_link)) for payload, has_link in connection.execute(
                "SELECT payload,protected_link IS NOT NULL AND length(protected_link)>0 FROM tasks"
            )
        ]
        sessions = {}
        if connection.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='platform_sessions'").fetchone():
            sessions = {platform: {"state": state if state in {"saved", "ready", "expired"} else "unrecognized",
                                    "savedAtUtc": datetime.fromtimestamp(saved / 1000, timezone.utc).isoformat() if saved is not None else None,
                                    "checkedAtUtc": datetime.fromtimestamp(checked / 1000, timezone.utc).isoformat() if checked is not None else None,
                                    "hasProtectedCookies": bool(has_cookies)}
                        for platform, state, saved, checked, has_cookies in connection.execute(
                            "SELECT platform_id,state,saved_utc,checked_utc,protected_cookies IS NOT NULL AND length(protected_cookies)>0 FROM platform_sessions")
                        if platform in RECORDING_PLATFORMS and platform != "fif"}
        reminder_states = Counter()
        reminder_courses = Counter()
        for payload, state, count in connection.execute(
            "SELECT tasks.payload,reminders.state,count(*) FROM reminders "
            "JOIN tasks ON tasks.id=reminders.task_id GROUP BY tasks.id,reminders.state"
        ):
            # Counts describe stored plans, not whether Windows displayed a notification.
            category = state if state in {"scheduled", "processing", "requested"} else "other"
            reminder_states[category] += count
            if state != "requested":
                reminder_courses["selected" if is_reminder_selected(json.loads(payload), settings) else "unselected"] += count
        integrity = connection.execute("PRAGMA quick_check").fetchone()[0]
    app = settings.get("app", {})
    summary = app.get("LastSyncSummary") or {}
    # Retain the previous top-level learning-platform result and aggregate task counters.
    status = sync_counts(app, summary) | stored_counts(rows, now, settings)
    for field in ("DetailsSaved", "DetailsDeferred", "DetailsUnavailable", "DetailsRequireAction", "DetailsRequirePreview"):
        status[field[0].lower() + field[1:]] = summary.get(field, 0)
    status["schema"] = 9
    status["sessions"] = sessions
    status["generatedAtUtc"] = now.isoformat()
    status["integrity"] = "ok" if integrity == "ok" else "failed"
    status["reminders"] = {
        "notificationsEnabled": app.get("NotificationsEnabled", True),
        "storedStates": dict(reminder_states),
        "pendingSelectedCoursePlans": reminder_courses["selected"],
        "pendingUnselectedCoursePlans": reminder_courses["unselected"],
    }
    status["courseRecording"] = {
        platform_id: recording_counts(settings.get("course-recording:" + platform_id, {}),
                                      (task for task, _ in rows if task.get("PlatformId") == platform_id))
        for platform_id in RECORDING_PLATFORMS
    }
    learning_rows = [(task, link) for task, link in rows if task.get("PlatformId") == "chaoxing"]
    status["platforms"] = {"chaoxing": sync_counts(app, summary) | stored_counts(learning_rows, now, settings)}
    status["platforms"]["chaoxing"]["platformAutomaticEnabled"] = app.get("ChaoxingAutomaticSyncEnabled", True)
    status["platforms"]["chaoxing"]["automaticEnabled"] &= app.get("ChaoxingAutomaticSyncEnabled", True)
    for platform_id, default_host in PLATFORMS.items():
        config = settings.get("platform:" + platform_id, {})
        summary = config.get("LastSummary") or {}
        platform_rows = [(task, link) for task, link in rows if task.get("PlatformId") == platform_id]
        result = sync_counts(config, summary) | stored_counts(platform_rows, now, settings)
        result["connected"] = config.get("Connected", False)
        result["platformAutomaticEnabled"] = config.get("AutomaticSyncEnabled", True)
        result["automaticEnabled"] &= app.get("AutomaticSyncEnabled", True)
        host = config.get("Host", default_host)
        result["host"] = host if host in HOSTS else "unrecognized"
        for field in ("TasksWithDeadline", "TasksWithLink", "StatusNeedsReview", "DuplicateRows", "Requests", "CatalogPages", "SpocCourses", "NewExamTasks", "UnsupportedCourses", "UnsupportedRows",
                      "FlipCourses", "SharedCourses", "SmartCourses", "AgentCourses", "SmartOtherTasks", "AccountVerified", "FullTaskListsCovered", "ReminderListCovered", "SharedCourseReminders",
                      "RoomExamTasks", "OfflineExamTasks", "MakeupExamTasks", "UnidentifiedExamRows", "TaskListCoursesRead", "TeachingDirectoryFallbackCourses",
                      "LearningUnits", "LearningUnitsWithDeadline", "RequiredLearningUnitsIncomplete", "LearningUnitListsCovered"):
            result[field[0].lower() + field[1:]] = summary.get(field)
        if platform_id == "xuetang":
            result["coverageScope"] = "current_main_site_standard_courses"
        if platform_id in {"ucampus", "zhihuishu", "pta"}:
            scope = summary.get("CoverageScope", "unverified")
            result["coverageScope"] = scope if scope in {"unverified", "legacy-official-login-only", "legacy-class-homework-and-exam-lists", "legacy-class-homework-exam-and-learning-units", "flip-course-lists-and-shared-task-lists", "flip-shared-and-smart-homework-exam-lists", "teaching-class-problem-set-metadata"} else "unrecognized"
        if platform_id == "fif":
            result["connected"] = False
            result["automaticEnabled"] = result["platformAutomaticEnabled"] = False
            result["adaptationEnabled"] = False
            result["coverageScope"] = "adaptation-disabled"
        status["platforms"][platform_id] = result
    return status


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", type=Path, default=Path(__file__).resolve().parents[1] / ".local/data/tasks.db")
    args = parser.parse_args()
    print(json.dumps(read_status(args.database), ensure_ascii=False, indent=2))
