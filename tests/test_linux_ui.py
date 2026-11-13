import unittest
import importlib.util
from linux_ui_helpers import TestAppFixture

class DashboardChecks(unittest.TestCase):
    def test_dashboard_queries_real_backend_and_search_filters(self):
        with TestAppFixture() as fx:
            self.assertTrue(hasattr(fx.window, "dashboard"), "Native dashboard is not implemented")
            item=fx.request("create-task", {"title":"数学作业", "platformId":"chaoxing", "courseName":"数学", "deadlineKind":"unknown", "scoreText":"", "notes":""})
            fx.window.dashboard.refresh()
            fx.wait(lambda:fx.window.dashboard.task_model.get_n_items()==1)
            fx.window.dashboard.search_entry.set_text("不存在的课程")
            fx.wait(lambda:fx.window.dashboard.task_model.get_n_items()==0)
            self.assertEqual(fx.request("snapshot")["total"],1)
            fx.window.dashboard.search_entry.set_text("")
            fx.request("set-state",{"id":item["view"]["task"]["id"],"revision":1,"state":"completed"})
            fx.window.dashboard.refresh()
            fx.wait(lambda:fx.window.dashboard.task_model.get_n_items()==1)
            self.assertEqual(fx.window.dashboard.task_model.get_item(0).view["task"]["state"],"completed")

    def test_pagination_summary_and_platform_scope(self):
        with TestAppFixture() as fx:
            self.assertTrue(hasattr(fx.window, "dashboard"), "Native dashboard is not implemented")
            for index in range(105):
                fx.request("create-task", {"title": "同名作业", "platformId": "chaoxing" if index < 103 else "pta",
                    "courseName": str(index), "deadlineKind": "unknown", "scoreText": "", "notes": ""})
            dashboard = fx.window.dashboard
            dashboard.refresh()
            fx.wait(lambda: dashboard.task_model.get_n_items() == 100)
            self.assertEqual(dashboard.page["summary"]["pending"], 105)
            dashboard.load_more_button.emit("clicked")
            fx.wait(lambda: dashboard.task_model.get_n_items() == 105)
            ids = [dashboard.task_model.get_item(i).view["task"]["id"] for i in range(105)]
            self.assertEqual(len(set(ids)), 105)
            dashboard.platform_filter.set_selected(8)  # PTA after all and seven earlier platforms
            fx.wait(lambda: dashboard.task_model.get_n_items() == 2)
            self.assertEqual(dashboard.page["filteredTotal"], 2)
            self.assertEqual(dashboard.page["total"], 105)

    def test_backend_failure_keeps_last_snapshot_and_can_recover(self):
        with TestAppFixture() as fx:
            self.assertTrue(hasattr(fx.window, "dashboard"), "Native dashboard is not implemented")
            fx.request("create-task", {"title":"离线作业", "platformId":"pta", "courseName":"", "deadlineKind":"unknown", "scoreText":"", "notes":""})
            dashboard = fx.window.dashboard
            dashboard.refresh()
            fx.wait(lambda: dashboard.task_model.get_n_items() == 1)
            fx.bridge.close()
            dashboard.refresh()
            fx.wait(lambda: dashboard.error_label.get_visible())
            self.assertEqual(dashboard.task_model.get_n_items(), 1)

class EditorChecks(unittest.TestCase):
    def editor_type(self):
        self.assertIsNotNone(importlib.util.find_spec("study_task_hub.task_editor"), "Task editor is not implemented")
        from study_task_hub.task_editor import TaskEditor
        return TaskEditor

    def test_save_invalid_input_preserves_form_then_valid_save_and_restart(self):
        Editor = self.editor_type()
        with TestAppFixture() as fx:
            editor = Editor(fx.window, fx.bridge)
            editor.present()
            editor.fields["notes"].get_buffer().set_text("保留这条备注")
            editor.save_button.emit("clicked")
            fx.wait(lambda: editor.error_label.get_visible())
            self.assertEqual(fx.request("snapshot")["total"], 0)
            editor.fields["title"].set_text("系统作业")
            editor.save_button.emit("clicked")
            fx.wait(lambda: editor.saved_detail is not None)
            task_id = editor.saved_detail["view"]["task"]["id"]
            from study_task_hub.bridge import BackendBridge
            fx.bridge.close()
            restarted = BackendBridge(fx.application.backend_path, fx.temp.name)
            try:
                detail = restarted.request_async("get-task", {"id": task_id}).result(timeout=5)
                self.assertEqual(detail["view"]["task"]["notes"], "保留这条备注")
            finally:
                restarted.close()

    def test_stale_revision_keeps_edit_and_rejects_write(self):
        Editor = self.editor_type()
        with TestAppFixture() as fx:
            input = {"title":"原名称", "platformId":"pta", "courseName":"", "deadlineKind":"unknown", "scoreText":"", "notes":""}
            task = fx.request("create-task", input)["view"]["task"]
            editor = Editor(fx.window, fx.bridge, task["id"])
            editor.present()
            fx.wait(lambda: editor.loaded)
            fx.request("edit-task", {"id": task["id"], "revision": 1, "input": dict(input, notes="另一处修改")})
            editor.fields["title"].set_text("尚未保存的名称")
            editor.save_button.emit("clicked")
            fx.wait(lambda: editor.error_label.get_visible())
            self.assertEqual(editor.fields["title"].get_text(), "尚未保存的名称")
            self.assertTrue(editor.save_button.get_sensitive())
            self.assertEqual(fx.request("get-task", {"id": task["id"]})["view"]["task"]["title"], "原名称")
            editor.destroy()

    def test_local_dates_include_offset_and_dst_gaps_are_rejected(self):
        self.editor_type()
        from study_task_hub.task_editor import parse_local_datetime
        from zoneinfo import ZoneInfo
        self.assertEqual(parse_local_datetime("2026-11-14 20:00", ZoneInfo("Asia/Shanghai")), "2026-11-14T20:00:00+08:00")
        for text in ("2026-03-29 02:30", "2026-10-25 02:30"):
            with self.assertRaises(ValueError):
                parse_local_datetime(text, ZoneInfo("Europe/Berlin"))

    def test_state_and_delete_confirmation_use_backend(self):
        self.editor_type()
        with TestAppFixture() as fx:
            task = fx.request("create-task", {"title":"可完成任务", "platformId":"pta", "courseName":"", "deadlineKind":"unknown", "scoreText":"", "notes":""})["view"]["task"]
            dashboard = fx.window.dashboard
            dashboard.refresh()
            fx.wait(lambda: dashboard.task_model.get_n_items() == 1)
            dashboard.selection.set_selected(0)
            fx.wait(lambda: dashboard.selected_detail is not None)
            dashboard.state_action.set_selected(2)
            dashboard.state_button.emit("clicked")
            fx.wait(lambda: fx.request("get-task", {"id": task["id"]})["view"]["task"]["state"] == "completed")
            dashboard.refresh()
            fx.wait(lambda: dashboard.selected_detail is not None and dashboard.selected_detail["view"]["task"]["revision"] == 2)
            dialog = dashboard.confirm_delete()
            dialog.emit("response", "cancel")
            self.assertEqual(fx.request("snapshot")["total"], 1)
            dialog = dashboard.confirm_delete()
            dialog.emit("response", "delete")
            fx.wait(lambda: fx.request("snapshot")["total"] == 0)

if __name__=="__main__":unittest.main()
