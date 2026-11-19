"""Verify read-only sync accounting with isolated data and C#-derived course keys."""
import argparse
import importlib.util
import json
import sqlite3
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import uuid4


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("sync_status", ROOT / "scripts/read-sync-status.py")
status_reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(status_reader)

# Produced by System.Text.Json and CourseRecordingRules for the manual course 同名课程.
MANUAL_KEY = "mooc:088E66FCAC2666CD784F59FD62072AAEE60C2E20BDCAA03916E5AF7D0EC721F0"
SELECTED = "mooc:" + "A" * 64
EXCLUDED = "mooc:" + "B" * 64


def verify():
    run_root = ROOT / ".local/sync-status-checks" / uuid4().hex
    run_root.mkdir(parents=True)
    database = run_root / "tasks.db"
    now = datetime.now(timezone.utc)
    checks = {}

    def check(label, condition):
        checks[label] = bool(condition)
        if not condition:
            raise AssertionError(label)

    def task(platform, source, course, key, state, hours, label=""):
        return {"PlatformId": platform, "Source": source, "CourseName": course,
                "RecordingCourseKey": key, "State": state, "DeadlineKind": 1 if hours is not None else 0,
                "DeadlineUtc": (now + timedelta(hours=hours)).isoformat() if hours is not None else None,
                "DeadlineEvidence": 1 if hours is not None else 0, "PlatformStatusText": label,
                "Title": "synthetic-private-title", "SourceKey": "synthetic-private-account"}

    tasks = [
        task("mooc", 1, "可编辑课程名称", SELECTED, 0, 1, "未提交"),
        task("mooc", 1, "未选课程", EXCLUDED, 0, 48, "未提交"),
        task("mooc", 0, "  同名课程  ", "", 0, -1),
        task("mooc", 1, "历史课程", "", 1, None),
        task("ucampus", 1, "独立平台", "", 1, 48, "已结束（state=2; newState=2）"),
        task("zhihuishu", 1, "独立平台", "", 0, None, "synthetic-private-status?token=synthetic-secret"),
    ]
    recording = {"SelectionConfirmed": True, "RecordAllCourses": False, "HideUnselectedTasks": True,
                 "SelectedCourseKeys": [SELECTED, MANUAL_KEY],
                 "Courses": [{"Key": SELECTED, "Available": True}, {"Key": EXCLUDED, "Available": True}]}
    with sqlite3.connect(database) as connection:
        connection.executescript("""
            CREATE TABLE tasks(id TEXT PRIMARY KEY, payload TEXT NOT NULL, protected_link BLOB);
            CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE reminders(id TEXT PRIMARY KEY, task_id TEXT NOT NULL, state TEXT NOT NULL);
        """)
        connection.executemany("INSERT INTO tasks VALUES(?,?,?)",
                               [(str(index), json.dumps(row), b"synthetic-encrypted") for index, row in enumerate(tasks)])
        connection.executemany("INSERT INTO settings VALUES(?,?)",
                               [("app", json.dumps({"NotificationsEnabled": False})),
                                ("course-recording:mooc", json.dumps(recording))])
        connection.executemany("INSERT INTO reminders VALUES(?,?,?)",
                               [("a", "0", "scheduled"), ("b", "1", "scheduled"),
                                ("c", "2", "processing"), ("d", "2", "requested"), ("e", "1", "requested")])
    connection.close()
    before = database.read_bytes()
    hidden = status_reader.read_status(database)
    check("read_only_database_unchanged", database.read_bytes() == before)
    check("stored_history_retained", hidden["storedTasks"] == 6 and hidden["storedProtectedLinks"] == 6)
    check("hidden_and_selected_counts", hidden["visibleTasks"] == 4 and hidden["selectedTasks"] == 2
          and hidden["hiddenTasks"] == 2 and hidden["unselectedTasks"] == 4)
    check("hidden_tasks_do_not_affect_priorities", hidden["activePriorities"] == {"P1": 1, "P0": 1, "P4": 1})
    check("manual_chinese_course_matches_csharp", status_reader.task_course_key(tasks[2]) == MANUAL_KEY)
    overview = hidden["courseRecording"]["mooc"]
    check("catalog_and_retained_courses_separate", overview["catalogCourses"] == 2 and overview["overviewCourses"] == 4
          and overview["retainedOnlyCourses"] == 2)
    check("selected_manual_course_in_overview", overview["selectedCatalogCourses"] == 1 and overview["selectedOverviewCourses"] == 2)
    check("platform_selection_isolated", hidden["platforms"]["ucampus"]["visibleTasks"] == 1
          and hidden["platforms"]["zhihuishu"]["visibleTasks"] == 1)
    reminders = hidden["reminders"]
    check("pending_plans_separate_from_requested_history", reminders["storedStates"] == {"scheduled": 2, "processing": 1, "requested": 2}
          and reminders["pendingSelectedCoursePlans"] == 2 and reminders["pendingUnselectedCoursePlans"] == 1)
    check("notification_switch_reported", reminders["notificationsEnabled"] is False)
    check("ucampus_display_status_recognized", hidden["platforms"]["ucampus"]["platformStatusCounts"] == {"已结束": 1})
    encoded = json.dumps(hidden, ensure_ascii=False)
    check("private_fields_and_unrecognized_labels_redacted", all(value not in encoded for value in
          ("synthetic-private", "synthetic-secret", "同名课程", "可编辑课程名称", "未选课程", SELECTED, EXCLUDED, MANUAL_KEY)))
    check("status_prefix_cannot_export_private_suffix", status_reader.status_label({"PlatformId": "ucampus",
          "PlatformStatusText": "已结束（state=2; newState=2）synthetic-secret"}) == "other")

    def apply(**changes):
        recording.update(changes)
        with sqlite3.connect(database) as connection:
            connection.execute("UPDATE settings SET value=? WHERE key='course-recording:mooc'", (json.dumps(recording),))
        connection.close()
        return status_reader.read_status(database)

    visible = apply(HideUnselectedTasks=False)
    check("retained_history_can_be_displayed", visible["visibleTasks"] == 6 and visible["hiddenTasks"] == 0
          and visible["unselectedTasks"] == 4)
    check("visible_priorities_include_retained_history", visible["activePriorities"] == {"P1": 1, "P3": 1, "P0": 1, "P4": 1})
    check("display_does_not_select_reminders", visible["selectedActivePriorities"] == hidden["selectedActivePriorities"]
          and visible["reminders"] == hidden["reminders"])
    restored = apply(RecordAllCourses=True, HideUnselectedTasks=True)
    check("record_all_restores_tasks_and_priorities", restored["visibleTasks"] == 6 and restored["selectedTasks"] == 4
          and restored["activePriorities"] == visible["activePriorities"]
          and restored["selectedActivePriorities"] == {"P1": 1, "P3": 1, "P0": 1})
    check("record_all_reclassifies_pending_plans", restored["reminders"]["pendingUnselectedCoursePlans"] == 0
          and restored["reminders"]["pendingSelectedCoursePlans"] == 3)
    empty = apply(RecordAllCourses=False, SelectedCourseKeys=[])
    check("select_none_keeps_history_and_other_platforms", empty["storedTasks"] == 6 and empty["visibleTasks"] == 2
          and empty["platforms"]["mooc"]["activePriorities"] == {})
    pending = apply(SelectionConfirmed=False)
    check("pending_selection_retains_history", pending["visibleTasks"] == 6 and pending["selectedTasks"] == 0
          and not pending["courseRecording"]["mooc"]["selectionConfirmed"])
    check("manual_reminders_continue_before_selection", pending["reminders"]["pendingSelectedCoursePlans"] == 1
          and pending["reminders"]["pendingUnselectedCoursePlans"] == 2)
    recording.pop("SelectionConfirmed")
    legacy = apply(RecordAllCourses=True)
    check("legacy_all_requires_confirmation", legacy["selectedTasks"] == 0 and legacy["visibleTasks"] == 6
          and legacy["courseRecording"]["mooc"]["pendingChoiceCourses"] == 2
          and not legacy["courseRecording"]["mooc"]["recordAllCourses"])
    with sqlite3.connect(database) as connection:
        connection.executescript("CREATE TABLE platform_sessions(platform_id TEXT PRIMARY KEY,protected_cookies BLOB,saved_utc INTEGER,checked_utc INTEGER,state TEXT NOT NULL);")
        connection.execute("INSERT INTO platform_sessions VALUES(?,?,?,?,?)", ("mooc", b"synthetic-cookie-secret", int(now.timestamp() * 1000), None, "saved"))
        connection.execute("INSERT INTO platform_sessions VALUES(?,?,?,?,?)", ("yuketang", None, None, int(now.timestamp() * 1000), "expired"))
        connection.execute("UPDATE settings SET value=? WHERE key='app'", (json.dumps({"ChaoxingAutomaticSyncEnabled": False}),))
    session_before = database.read_bytes()
    sessions = status_reader.read_status(database)
    check("session_state_read_without_cookie_export", sessions["sessions"]["mooc"]["hasProtectedCookies"]
          and sessions["sessions"]["yuketang"]["state"] == "expired" and "synthetic-cookie-secret" not in json.dumps(sessions))
    check("session_report_is_read_only", database.read_bytes() == session_before)
    check("learning_switch_is_independent", not sessions["platforms"]["chaoxing"]["automaticEnabled"]
          and sessions["platforms"]["mooc"]["automaticEnabled"])
    smart_summary = {"CoverageScope": "flip-shared-and-smart-homework-exam-lists", "SmartCourses": 3,
                     "AgentCourses": 0, "SmartOtherTasks": 6, "AccountVerified": True, "LoginRequired": False}
    with sqlite3.connect(database) as connection:
        connection.execute("INSERT INTO settings VALUES(?,?)", ("platform:zhihuishu", json.dumps({"Connected": True, "LastSummary": smart_summary})))
    smart_before = database.read_bytes()
    smart = status_reader.read_status(database)["platforms"]["zhihuishu"]
    check("smart_course_coverage_reported", smart["coverageScope"] == smart_summary["CoverageScope"] and smart["smartCourses"] == 3 and smart["agentCourses"] == 0)
    check("smart_resource_gap_and_verified_account_reported", smart["smartOtherTasks"] == 6 and smart["accountVerified"] is True)
    check("smart_status_report_remains_readonly", database.read_bytes() == smart_before)
    pta_summary = {"CoverageScope": "teaching-class-problem-set-metadata", "AccountVerified": True,
                   "FullTaskListsCovered": False, "StatusNeedsReview": 1, "Tasks": 2,
                   "TaskListCoursesRead": 2, "TeachingDirectoryFallbackCourses": 1,
                   "Problems": [{"Code": "PTA003", "Message": "synthetic-private-message"}]}
    with sqlite3.connect(database) as connection:
        connection.execute("INSERT INTO settings VALUES(?,?)", ("platform:pta", json.dumps({"Connected": True, "LastSummary": pta_summary})))
    pta_before = database.read_bytes()
    pta = status_reader.read_status(database)["platforms"]["pta"]
    check("pta_teaching_scope_and_codes_reported", pta["coverageScope"] == pta_summary["CoverageScope"] and pta["problemCodes"] == ["PTA003"])
    check("pta_account_and_partial_task_coverage_reported", pta["accountVerified"] is True and pta["fullTaskListsCovered"] is False and pta["statusNeedsReview"] == 1)
    check("pta_list_and_recovered_directory_counts_reported", pta["taskListCoursesRead"] == 2 and pta["teachingDirectoryFallbackCourses"] == 1)
    check("pta_metadata_accounting_readonly_and_redacted", database.read_bytes() == pta_before and "synthetic-private-message" not in json.dumps(pta))
    check("pta_completed_status_category_recognized", status_reader.status_label({"PlatformId": "pta", "PlatformStatusText": "已结束答题（status=END; exam.status=END; ended=true）"}) == "已结束答题")
    check("pta_status_suffix_cannot_export_private_text", status_reader.status_label({"PlatformId": "pta", "PlatformStatusText": "已结束答题（status=END; exam.status=END; ended=true）synthetic-private"}) == "other")
    with sqlite3.connect(database) as connection:
        connection.execute("UPDATE settings SET value=? WHERE key='app'", (json.dumps({"LastSyncSummary": {"DetailsRequirePreview": 2}}),))
    preview_before = database.read_bytes()
    check("chaoxing_preview_count_reported_readonly", status_reader.read_status(database)["detailsRequirePreview"] == 2
          and database.read_bytes() == preview_before)
    check("old_summary_does_not_claim_new_pta_counts", hidden["platforms"]["pta"]["taskListCoursesRead"] is None)
    unit_summary = {"CoverageScope": "legacy-class-homework-exam-and-learning-units", "AccountVerified": True,
                    "CourseListCovered": True, "FullTaskListsCovered": True, "LearningUnitListsCovered": False,
                    "LearningUnits": 30, "LearningUnitsWithDeadline": 20, "RequiredLearningUnitsIncomplete": 2,
                    "Problems": [{"Code": "UC003", "Message": "synthetic-private-unit"}]}
    with sqlite3.connect(database) as connection:
        connection.execute("INSERT INTO settings VALUES(?,?)", ("platform:ucampus", json.dumps({"Connected": True, "LastSummary": unit_summary})))
    unit_before = database.read_bytes()
    unit_status = status_reader.read_status(database)["platforms"]["ucampus"]
    check("ucampus_learning_scope_reported", unit_status["coverageScope"] == unit_summary["CoverageScope"])
    check("ucampus_required_unit_totals_reported", unit_status["learningUnits"] == 30 and unit_status["learningUnitsWithDeadline"] == 20
          and unit_status["requiredLearningUnitsIncomplete"] == 2)
    check("unit_and_assignment_coverage_independent", unit_status["fullTaskListsCovered"] is True and unit_status["learningUnitListsCovered"] is False)
    check("unit_accounting_readonly_and_redacted", database.read_bytes() == unit_before and "synthetic-private-unit" not in json.dumps(unit_status))

    attention = task("mooc", 3, "synthetic-private-course", SELECTED, 1, 1) | {
        "Overrides": 128, "PlatformObservedState": 3,
        "PlatformAttentionToken": "synthetic-private-attention",
        "PlatformAttentionAcknowledgedToken": ""}
    attention_rows = {
        "attention-completed": attention,
        "attention-ignored": attention | {"State": 2, "RecordingCourseKey": EXCLUDED, "PlatformObservedState": 0},
        "attention-confirmed": attention | {"PlatformAttentionAcknowledgedToken": attention["PlatformAttentionToken"]},
        "attention-manual": attention | {"Source": 0},
        "attention-no-override": attention | {"Overrides": 0},
        "attention-unknown": attention | {"PlatformObservedState": 4},
        "attention-empty-token": attention | {"PlatformAttentionToken": ""},
        "attention-null-token": attention | {"PlatformAttentionToken": None},
    }
    with sqlite3.connect(database) as connection:
        connection.executemany("INSERT INTO tasks VALUES(?,?,?)", [
            (identifier, json.dumps(row), None) for identifier, row in attention_rows.items()])
    attention_status = apply(SelectionConfirmed=True, RecordAllCourses=False, HideUnselectedTasks=True,
                             SelectedCourseKeys=[SELECTED, MANUAL_KEY])
    check("official_attention_preserves_completed_and_ignored_counts", attention_status["storedStates"]["completed"] == 9
          and attention_status["storedStates"]["ignored"] == 1)
    check("unacknowledged_official_attention_counted_without_manual_or_unknown_false_positives",
          attention_status["storedPlatformAttention"] == 2)
    check("official_attention_respects_hidden_and_selected_courses", attention_status["visiblePlatformAttention"] == 1
          and attention_status["selectedPlatformAttention"] == 1)
    check("completed_but_reopened_task_participates_in_active_priority",
          attention_status["activePriorities"] == {"P1": 2, "P0": 1, "P4": 1}
          and attention_status["selectedActivePriorities"] == {"P1": 2, "P0": 1})
    check("official_attention_event_identifiers_are_redacted", "synthetic-private-attention" not in json.dumps(attention_status))
    attention_visible = apply(HideUnselectedTasks=False)
    check("showing_retained_attention_does_not_restore_reminder_selection",
          attention_visible["visiblePlatformAttention"] == 2 and attention_visible["selectedPlatformAttention"] == 1)
    attention_rows["attention-completed"]["PlatformAttentionAcknowledgedToken"] = attention["PlatformAttentionToken"]
    with sqlite3.connect(database) as connection:
        connection.execute("UPDATE tasks SET payload=? WHERE id='attention-completed'", (json.dumps(attention),))
    acknowledged_before = database.read_bytes()
    acknowledged = status_reader.read_status(database)
    check("acknowledgement_clears_active_attention_without_reclassifying_completion",
          acknowledged["storedPlatformAttention"] == 1 and acknowledged["selectedPlatformAttention"] == 0
          and acknowledged["selectedActivePriorities"] == {"P1": 1, "P0": 1}
          and acknowledged["storedStates"]["completed"] == attention_status["storedStates"]["completed"])
    check("attention_diagnostic_is_read_only", database.read_bytes() == acknowledged_before)

    uai_manual = task("uai", 0, "synthetic-private-uai-course", "", 0, 10)
    uai_key = status_reader.task_course_key(uai_manual)
    with sqlite3.connect(database) as connection:
        connection.execute("INSERT INTO tasks VALUES(?,?,?)", ("uai-manual", json.dumps(uai_manual), None))
        connection.execute("INSERT INTO settings VALUES(?,?)", ("course-recording:uai", json.dumps({
            "SelectionConfirmed": True, "SelectedCourseKeys": [uai_key], "Courses": [],
            "LastCatalogRefresh": {"Outcome": "partial", "ProblemCodes": ["UAI003", "synthetic-private-code"]}})))
        connection.execute("INSERT INTO settings VALUES(?,?)", ("platform:uai", json.dumps({
            "Connected": True, "AutomaticSyncEnabled": True, "LastSummary": {
                "AccountVerified": True, "Tasks": 0, "FullTaskListsCovered": True,
                "Problems": [{"Code": "UAI004"}, {"Code": "synthetic-private-code"}]}})))
        connection.execute("INSERT INTO platform_sessions VALUES(?,?,?,?,?)", ("uai", b"synthetic-uai-session-secret",
                           int(now.timestamp() * 1000), None, "saved"))
    no_preview = status_reader.read_status(database)
    check("uai_manual_course_and_session_are_independent_from_legacy_ucampus",
          no_preview["courseRecording"]["uai"]["selectedOverviewCourses"] == 1
          and no_preview["platforms"]["uai"]["storedTasks"] == 1
          and no_preview["platforms"]["ucampus"]["storedTasks"] == 1
          and no_preview["sessions"]["uai"]["state"] == "saved")
    check("unread_uai_preview_does_not_claim_zero_official_tasks", no_preview["platforms"]["uai"]["tasksRead"] is None
          and no_preview["platforms"]["uai"]["preview"]["outcome"] == "not_started"
          and no_preview["platforms"]["uai"]["preview"]["overviewTasks"] is None)
    check("uai_cannot_claim_automatic_or_full_task_collection_from_stored_flags",
          not no_preview["platforms"]["uai"]["automaticEnabled"]
          and not no_preview["platforms"]["uai"]["courseListCovered"]
          and not no_preview["platforms"]["uai"]["fullTaskListsCovered"]
          and no_preview["platforms"]["uai"]["automaticTasksImported"] == 0)
    check("uai_diagnostic_codes_survive_safe_code_filtering", no_preview["platforms"]["uai"]["problemCodes"] == ["UAI004"]
          and no_preview["courseRecording"]["uai"]["lastRefreshProblemCodes"] == ["UAI003"])
    uai_preview = {"CheckedAtUtc": now.isoformat(), "Outcome": "partial", "AccountVerified": True,
                   "ShelfResources": 2, "OverviewTasks": 8, "DisplayedTasks": 3, "UaiModuleAllowed": True,
                   "Codes": ["UAI004", "UAI005"], "FullTaskListsCovered": True,
                   "ssoId": "synthetic-private-openid", "school": "synthetic-private-school",
                   "jwt": "synthetic-jwt-secret", "rt": "synthetic-refresh-secret",
                   "rawResponse": "synthetic-private-body"}

    def apply_preview(preview):
        with sqlite3.connect(database) as connection:
            connection.execute("INSERT INTO settings VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                               ("platform:uai-preview", json.dumps(preview)))
        snapshot = database.read_bytes()
        result = status_reader.read_status(database)
        if database.read_bytes() != snapshot:
            raise AssertionError("UAI diagnostic modified the database")
        return result

    uai_status = apply_preview(uai_preview)
    uai = uai_status["platforms"]["uai"]
    check("uai_preview_counts_are_distinct_from_imported_manual_tasks", uai["preview"]["shelfResources"] == 2
          and uai["preview"]["overviewTasks"] == 8 and uai["preview"]["displayedTasks"] == 3
          and uai["storedTasks"] == 1 and uai["tasksRead"] is None)
    check("uai_preview_remains_partial_even_when_stored_coverage_flag_is_true",
          uai["preview"]["outcome"] == "partial" and not uai["preview"]["fullTaskListsCovered"])
    check("uai_preview_exports_only_counts_and_safe_evidence", all(value not in json.dumps(uai_status) for value in
          ("synthetic-private", "synthetic-jwt-secret", "synthetic-refresh-secret", "synthetic-uai-session-secret", uai_key)))
    unknown_preview = apply_preview(uai_preview | {"ShelfResources": None, "OverviewTasks": None,
                                                  "DisplayedTasks": None, "UaiModuleAllowed": None})
    check("unconfirmed_uai_counts_remain_null", unknown_preview["platforms"]["uai"]["preview"]["overviewTasks"] is None
          and unknown_preview["platforms"]["uai"]["preview"]["shelfResources"] is None)
    zero_preview = apply_preview(uai_preview | {"ShelfResources": 0, "OverviewTasks": 0,
                                               "DisplayedTasks": 0, "UaiModuleAllowed": False})
    check("explicit_empty_uai_preview_preserves_zero_but_not_full_coverage",
          zero_preview["platforms"]["uai"]["preview"]["overviewTasks"] == 0
          and zero_preview["platforms"]["uai"]["preview"]["uaiModuleAllowed"] is False
          and not zero_preview["platforms"]["uai"]["fullTaskListsCovered"])
    for label, changes in {
        "private_code": {"Codes": ["UAI004synthetic-private"]},
        "private_time": {"CheckedAtUtc": now.isoformat() + "synthetic-private"},
        "inconsistent_total": {"OverviewTasks": 2, "DisplayedTasks": 3},
        "invalid_outcome": {"Outcome": ["partial"]},
        "boolean_count": {"ShelfResources": True},
        "login_conflict": {"Outcome": "login_required", "AccountVerified": True},
    }.items():
        invalid = apply_preview(uai_preview | changes)["platforms"]["uai"]["preview"]
        check("invalid_uai_preview_is_unrecognized_" + label, invalid["outcome"] == "unrecognized"
              and invalid["overviewTasks"] is None and not invalid["accountVerified"]
              and "synthetic-private" not in json.dumps(invalid))
    return {"schema": 1, "outcome": "passed", "checks": len(checks), "results": checks,
            "data": "synthetic_only", "network": "none", "productionDatabase": "not_accessed"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = verify()
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))
