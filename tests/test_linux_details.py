import unittest
from linux_ui_helpers import TestAppFixture

class DetailChecks(unittest.TestCase):
    def test_sync_details_show_all_problems_from_saved_report(self):
        import sqlite3,json,datetime
        from pathlib import Path
        from study_task_hub.sync_details import SyncDetailsWindow
        with TestAppFixture() as fx:
            stamp=datetime.datetime.now(datetime.timezone.utc).isoformat()
            report={"Platform":"pta","Stage":"tasks","Outcome":"partial","StartedAtUtc":stamp,"FinishedAtUtc":stamp,"LastSuccessfulAtUtc":None,
                "Complete":False,"TasksObserved":3,"Scope":"题目集元数据","Metrics":{"CoursesRead":"1","Tasks":"3"},
                "Problems":[{"Code":f"SYNTHETIC{i}","Course":"数学","Message":f"问题说明 {i}"} for i in range(8)],
                "Courses":[{"Key":"pta:"+"A"*64,"Name":"数学","Selected":True,"Available":True,"LocalTasks":6,"ObservedTasks":3,"Coverage":"已有读取结果，覆盖待核对","LastSuccessfulAtUtc":None}]}
            with sqlite3.connect(Path(fx.temp.name)/"tasks.db") as connection:
                connection.execute("INSERT INTO settings(key,value) VALUES(?,?)",("linux-sync-report:pta:tasks",json.dumps(report)))
            window=SyncDetailsWindow(fx.window,fx.bridge,"pta");window.present()
            fx.wait(lambda:window.page is not None)
            text=window.report_text
            self.assertIn("SYNTHETIC7",text);self.assertIn("本地 6 条",text);self.assertIn("本轮 3 条",text);self.assertIn("覆盖待核对",text)
            window.destroy()
    def test_selected_task_shows_progress_and_score(self):
        with TestAppFixture() as fx:
            fx.request("create-task",{"title":"进度任务","platformId":"pta","courseName":"数学","deadlineKind":"unknown","progressPercent":42,"scoreText":"86分","notes":"需检查"})
            dashboard=fx.window.dashboard;dashboard.refresh()
            fx.wait(lambda:dashboard.task_model.get_n_items()==1)
            dashboard.selection.set_selected(0);fx.wait(lambda:dashboard.selected_detail is not None)
            self.assertIn("42%",dashboard.detail_body.get_text());self.assertIn("86分",dashboard.detail_body.get_text())

    def test_platform_evidence_and_manual_overrides_are_visible(self):
        from study_task_hub.task_details import format_task_detail
        with TestAppFixture() as fx:
            detail=fx.request("create-task",{"title":"依据任务","platformId":"pta","courseName":"数学","deadlineKind":"unknown","scoreText":"","notes":""})
            detail["view"]["task"].update(source="pta",platformStatusText="待批改",automaticStatusExplanation="平台未明确完成",platformStateEvidence="explicit",
                deadlineEvidence="remainingTime",displayedTimeText="剩余 2 天",detailTimeText="开放至周五",overrides="deadline, state, score")
            text=format_task_detail(detail)
            for expected in ("待批改","平台未明确完成","剩余 2 天","开放至周五","剩余时长估算","截止时间","状态","成绩"):
                self.assertIn(expected,text)

if __name__=="__main__":unittest.main()
