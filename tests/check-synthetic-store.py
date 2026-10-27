"""Read-only checks for WorkPageHarness data. Never use the production task store."""
import json
import pathlib
import sqlite3
import sys

run_root = pathlib.Path(sys.argv[1]).resolve()
if run_root.parent.name != "work-page-harness":
    raise SystemExit("Expected an isolated WorkPageHarness run directory.")
database = run_root / ".local" / "data" / "tasks.db"
with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as connection:
    tasks = [json.loads(row[0]) for row in connection.execute("SELECT payload FROM tasks")]
checks = {
    "three_tasks": len(tasks) == 3,
    "course_saved": all(task["CourseName"] == "后台导入验收课程" for task in tasks),
    "user_state_preserved": all(task["State"] == 0 for task in tasks),
    "deadline_not_guessed": all(task["DeadlineUtc"] is None for task in tasks),
}
result = {"outcome": "passed" if all(checks.values()) else "failed", "data": "synthetic_only", "checks": checks}
print(json.dumps(result, ensure_ascii=False))
raise SystemExit(0 if result["outcome"] == "passed" else 1)
