"""Read aggregate sync counters without exporting accounts, task titles or links."""
import argparse
import json
import re
import sqlite3
from collections import Counter
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
}
PLATFORMS = {"yuketang": "www.yuketang.cn", "changjiang": "changjiang.yuketang.cn", "mooc": "www.icourse163.org"}
HOSTS = {"www.yuketang.cn", "pro.yuketang.cn", "changjiang.yuketang.cn", "www.icourse163.org"}


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


def stored_counts(rows, now):
    tasks = [task for task, _ in rows]
    return {
        "storedTasks": len(rows),
        "storedProtectedLinks": sum(has_link for _, has_link in rows),
        "storedStates": dict(Counter(STATES.get(task.get("State"), "unknown") for task in tasks)),
        "platformStatusCounts": dict(Counter(
            label if label in STATUS_LABELS else "other"
            for task in tasks for label in [task.get("PlatformStatusText", "")]
        )),
        "storedDeadlineEvidence": dict(Counter(
            EVIDENCE.get(task.get("DeadlineEvidence", 0), "unknown") for task in tasks
        )),
        "storedDeadlines": sum(bool(task.get("DeadlineUtc")) for task in tasks),
        "storedDetailTimes": sum(bool(task.get("DetailTimeText")) for task in tasks),
        "activePriorities": dict(Counter(
            priority(task, now) for task in tasks if task.get("State") in (0, 3, 4)
        )),
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
            if isinstance(code, str) and re.fullmatch(r"(?:SYNC|RAIN|MOOC|DB)[0-9]{3}", code)
        ],
    }


def recording_counts(settings):
    courses = settings.get("Courses") or []
    record_all = settings.get("RecordAllCourses", True)
    selected = set(settings.get("SelectedCourseKeys") or [])
    return {
        "recordAllCourses": record_all,
        "hideUnselectedTasks": settings.get("HideUnselectedTasks", True),
        "revision": settings.get("Revision", 0),
        "catalogCourses": len(courses),
        "availableCourses": sum(course.get("Available", True) for course in courses),
        "selectedCatalogCourses": sum(record_all or course.get("Key") in selected for course in courses),
        "selectedCourseKeys": len(selected),
        "catalogCheckedAtUtc": settings.get("CatalogCheckedAtUtc"),
        "catalogComplete": settings.get("CatalogComplete", False),
    }


def read_status(database):
    now = datetime.now(timezone.utc)
    with sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True) as connection:
        connection.execute("PRAGMA query_only=ON")
        settings = {
            key: json.loads(value) for key, value in connection.execute(
                "SELECT key,value FROM settings WHERE key IN ('app','platform:yuketang','platform:changjiang','platform:mooc') OR key LIKE 'course-recording:%'"
            )
        }
        rows = [
            (json.loads(payload), bool(has_link)) for payload, has_link in connection.execute(
                "SELECT payload,protected_link IS NOT NULL AND length(protected_link)>0 FROM tasks"
            )
        ]
        integrity = connection.execute("PRAGMA quick_check").fetchone()[0]
    app = settings.get("app", {})
    summary = app.get("LastSyncSummary") or {}
    # Retain the previous top-level learning-platform result and aggregate task counters.
    status = sync_counts(app, summary) | stored_counts(rows, now)
    for field in ("DetailsSaved", "DetailsDeferred", "DetailsUnavailable", "DetailsRequireAction"):
        status[field[0].lower() + field[1:]] = summary.get(field, 0)
    status["schema"] = 4
    status["generatedAtUtc"] = now.isoformat()
    status["integrity"] = "ok" if integrity == "ok" else "failed"
    status["courseRecording"] = {
        platform_id: recording_counts(settings.get("course-recording:" + platform_id, {}))
        for platform_id in ("chaoxing", "yuketang", "changjiang", "mooc", "ucampus", "fif", "xuetang", "zhihuishu")
    }
    learning_rows = [(task, link) for task, link in rows if task.get("PlatformId") == "chaoxing"]
    status["platforms"] = {"chaoxing": sync_counts(app, summary) | stored_counts(learning_rows, now)}
    for platform_id, default_host in PLATFORMS.items():
        config = settings.get("platform:" + platform_id, {})
        summary = config.get("LastSummary") or {}
        platform_rows = [(task, link) for task, link in rows if task.get("PlatformId") == platform_id]
        result = sync_counts(config, summary) | stored_counts(platform_rows, now)
        result["connected"] = config.get("Connected", False)
        result["platformAutomaticEnabled"] = config.get("AutomaticSyncEnabled", True)
        result["automaticEnabled"] &= app.get("AutomaticSyncEnabled", True)
        host = config.get("Host", default_host)
        result["host"] = host if host in HOSTS else "unrecognized"
        for field in ("TasksWithDeadline", "TasksWithLink", "StatusNeedsReview", "DuplicateRows", "Requests", "CatalogPages", "SpocCourses", "NewExamTasks"):
            result[field[0].lower() + field[1:]] = summary.get(field)
        status["platforms"][platform_id] = result
    return status


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", type=Path, default=Path(__file__).resolve().parents[1] / ".local/data/tasks.db")
    args = parser.parse_args()
    print(json.dumps(read_status(args.database), ensure_ascii=False, indent=2))
