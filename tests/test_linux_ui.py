import unittest
import importlib.util
from linux_ui_helpers import TestAppFixture

class DashboardChecks(unittest.TestCase):
    def test_older_search_response_cannot_replace_current_filter(self):
        import concurrent.futures
        import time
        from linux_ui_helpers import run_until
        with TestAppFixture() as fx:
            fx.request("create-task",{"title":"数学作业","platformId":"pta","courseName":"","deadlineKind":"unknown","scoreText":"","notes":""})
            dashboard=fx.window.dashboard
            dashboard.refresh()
            fx.wait(lambda:dashboard.task_model.get_n_items()==1)
            pending=[]
            class DelayedResponses:
                def request_async(self,command,args=None):
                    value=fx.bridge.request_async(command,args).result(timeout=5)
                    future=concurrent.futures.Future()
                    pending.append((future,value))
                    return future
            dashboard.bridge=DelayedResponses()
            dashboard.search_entry.set_text("数学")
            dashboard.refresh()
            dashboard.search_entry.set_text("程序")
            dashboard.refresh()
            latest=pending[-1]
            older=next(item for item in pending if item[1]["filteredTotal"]==1)
            latest[0].set_result(latest[1])
            fx.wait(lambda:dashboard.task_model.get_n_items()==0)
            older[0].set_result(older[1])
            until=time.monotonic()+0.1
            run_until(lambda:time.monotonic()>until)
            self.assertEqual(dashboard.task_model.get_n_items(),0)
            self.assertEqual(dashboard.page["filteredTotal"],0)
    def test_large_font_detail_actions_fit_inside_window(self):
        from gi.repository import Gtk
        settings=Gtk.Settings.get_default()
        original=settings.get_property("gtk-font-name")
        settings.set_property("gtk-font-name","Sans 16")
        try:
            with TestAppFixture(width=1000,height=700) as fx:
                fx.request("create-task",{"title":"大字体详情","platformId":"pta","courseName":"","deadlineKind":"unknown","scoreText":"","notes":""})
                dashboard=fx.window.dashboard
                dashboard.refresh()
                fx.wait(lambda:dashboard.task_model.get_n_items()==1)
                dashboard.selection.set_selected(0)
                fx.wait(lambda:dashboard.selected_detail is not None)
                from linux_ui_helpers import run_until
                import time
                until=time.monotonic()+0.2
                run_until(lambda:time.monotonic()>until)
                ok,bounds=dashboard.open_button.compute_bounds(fx.window)
                self.assertTrue(ok)
                self.assertLessEqual(bounds.get_x()+bounds.get_width(),fx.window.get_width()+1,"Detail action is horizontally clipped")
        finally:
            settings.set_property("gtk-font-name",original)
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
            self.assertTrue(hasattr(fx.window,"restart_backend"), "Backend recovery is not implemented")
            fx.window.restart_backend()
            fx.wait(lambda: fx.window.bridge is not fx.bridge and not dashboard.error_label.get_visible())
            self.assertEqual(dashboard.task_model.get_n_items(), 1)
            fx.window.bridge.close()

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

    def test_editing_notes_preserves_existing_deadline_seconds(self):
        Editor = self.editor_type()
        with TestAppFixture() as fx:
            task=fx.request("create-task",{"title":"保留原日期","platformId":"pta","courseName":"","deadlineKind":"known","deadlineIso":"2026-11-14T20:00:59+08:00","notes":"","scoreText":""})["view"]["task"]
            editor=Editor(fx.window,fx.bridge,task["id"])
            editor.present()
            fx.wait(lambda:editor.loaded)
            editor.fields["notes"].get_buffer().set_text("仅修改备注")
            editor.save_button.emit("clicked")
            fx.wait(lambda:editor.saved_detail is not None)
            self.assertEqual(fx.request("get-task",{"id":task["id"]})["view"]["task"]["deadlineUtc"],"2026-11-14T12:00:59+00:00")

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

class PlatformChecks(unittest.TestCase):
    def test_open_platform_window_receives_jobs_after_backend_recovery(self):
        from study_task_hub.platform_window import PlatformWindow
        with TestAppFixture() as fx:
            fx.application.window=fx.window
            fx.application.bridge=fx.bridge
            platforms=PlatformWindow(fx.window,fx.bridge)
            platforms.present()
            fx.wait(lambda:len(platforms.views)==9)
            try:
                fx.bridge.close()
                fx.window.restart_backend()
                fx.wait(lambda:platforms.bridge is not fx.bridge)
                platforms.start_sync("pta",True)
                fx.wait(lambda:platforms.last_result is not None and not platforms.active_jobs)
                self.assertTrue(platforms.last_result["result"]["loginRequired"])
            finally:
                platforms.on_close();platforms.destroy()
                if fx.application.scheduler:fx.application.scheduler.stop()
                fx.window.bridge.close()
                fx.application.window=None

class RecoveryChecks(unittest.TestCase):
    def test_application_exit_deadline_closes_an_unresponsive_login(self):
        from gi.repository import Adw
        from linux_ui_helpers import run_until
        with TestAppFixture() as fx:
            class UnresponsiveLogin(Adw.Window):
                closed=False
                def close_with_save(self,*_):return True
                def finish_close(self,*_):self.closed=True;self.destroy()
            login=UnresponsiveLogin(transient_for=fx.window)
            login.set_default_size(300,200)
            login.present()
            fx.application.window=fx.window
            fx.application.bridge=fx.bridge
            try:
                fx.application.request_close()
                run_until(lambda:fx.application.window is None,6)
                self.assertTrue(login.closed)
                self.assertIsNotNone(fx.bridge.process.poll())
            finally:
                login.finish_close()

    def test_editor_can_recover_without_closing_or_discarding_inputs(self):
        from study_task_hub.task_editor import TaskEditor
        from linux_ui_helpers import find_button
        with TestAppFixture() as fx:
            fx.application.window=fx.window
            fx.application.bridge=fx.bridge
            task=fx.request("create-task",{"title":"原任务","platformId":"pta","courseName":"","deadlineKind":"unknown","scoreText":"","notes":"原备注"})["view"]["task"]
            editor=TaskEditor(fx.window,fx.bridge,task["id"])
            editor.present();fx.wait(lambda:editor.loaded)
            editor.fields["title"].set_text("保留的新名称")
            editor.fields["notes"].get_buffer().set_text("保留的新备注")
            try:
                fx.bridge.close()
                editor.save_button.emit("clicked")
                fx.wait(lambda:editor.error_label.get_visible())
                button=find_button(editor,"恢复连接")
                self.assertIsNotNone(button,"Modal editor has no accessible recovery action")
                button.emit("clicked")
                fx.wait(lambda:editor.bridge is not fx.bridge)
                self.assertEqual(editor.fields["title"].get_text(),"保留的新名称")
                editor.save_button.emit("clicked")
                fx.wait(lambda:editor.saved_detail is not None)
                self.assertEqual(editor.saved_detail["view"]["task"]["notes"],"保留的新备注")
            finally:
                editor.destroy()
                if fx.application.scheduler:fx.application.scheduler.stop()
                fx.window.bridge.close()
                fx.application.window=None
    def test_open_settings_can_save_existing_inputs_after_rebinding(self):
        from study_task_hub.settings_window import SettingsWindow
        from study_task_hub.bridge import BackendBridge
        with TestAppFixture() as fx:
            dialog=SettingsWindow(fx.window,fx.bridge)
            dialog.present();fx.wait(lambda:dialog.settings is not None)
            dialog.notifications.set_active(False)
            dialog.automatic.set_active(False)
            fx.bridge.close()
            restarted=BackendBridge(fx.application.backend_path,fx.temp.name)
            dialog.bridge=restarted
            def find_button(widget):
                from gi.repository import Gtk
                if isinstance(widget,Gtk.Button) and widget.get_label()=="保存设置":return widget
                child=widget.get_first_child()
                while child:
                    result=find_button(child)
                    if result:return result
                    child=child.get_next_sibling()
            try:
                find_button(dialog).emit("clicked")
                fx.wait(lambda:not restarted.request_async("get-settings").result(timeout=5)["notificationsEnabled"])
                self.assertFalse(restarted.request_async("get-settings").result(timeout=5)["automaticSyncEnabled"])
            finally:
                dialog.destroy();restarted.close()
class PlatformCatalogChecks(unittest.TestCase):
    def test_platforms_show_eight_automatic_sources_and_manual_fif(self):
        self.assertIsNotNone(importlib.util.find_spec("study_task_hub.platform_window"), "Platform management is not implemented")
        from study_task_hub.platform_window import PlatformWindow
        with TestAppFixture() as fx:
            platforms = PlatformWindow(fx.window, fx.bridge)
            platforms.present()
            fx.wait(lambda: len(platforms.views) == 9)
            self.assertEqual(len([p for p in platforms.views if p["loginUri"]]), 8)
            platforms.start_sync("pta", True)
            fx.wait(lambda: not platforms.active_jobs and platforms.last_result is not None)
            self.assertTrue(platforms.last_result["result"]["loginRequired"])
            self.assertEqual(fx.request("snapshot")["total"], 0)
            platforms.destroy()

class SmartCourseChecks(unittest.TestCase):
    def test_switching_semester_recommends_matching_courses_and_keeps_review_draft(self):
        import sqlite3,json
        from study_task_hub.course_picker import CoursePicker
        with TestAppFixture() as fx:
            courses=self.seed(fx)
            courses[0]["Name"]="2026-2027第一学期 高等数学";courses[0]["Detail"]="官方课程"
            courses[1]["Name"]="2026-2027第二学期 大学物理";courses[1]["Detail"]="官方课程"
            with sqlite3.connect(fx.temp.name+"/tasks.db") as connection:
                value=json.loads(connection.execute("SELECT value FROM settings WHERE key='course-recording:pta'").fetchone()[0])
                value["Courses"]=courses
                connection.execute("UPDATE settings SET value=? WHERE key='course-recording:pta'",(json.dumps(value,ensure_ascii=False),))
            picker=CoursePicker(fx.window,fx.bridge,"pta")
            picker.present();fx.wait(lambda:len(picker.choices)==3)
            try:
                picker.year_filter.set_selected(picker.years.index(2026));picker.term_filter.set_selected(0)
                fx.wait(lambda:not picker.loading and picker.semester["term"]=="autumn")
                self.assertTrue(picker.choices[courses[0]["Key"]].get_active());self.assertFalse(picker.choices[courses[1]["Key"]].get_active())
                picker.choices[courses[0]["Key"]].set_active(False)
                picker.custom_name_entry.set_text("上学期补充课程");picker.add_custom_button.emit("clicked")
                picker.term_filter.set_selected(1)
                fx.wait(lambda:not picker.loading and picker.semester["term"]=="spring")
                self.assertFalse(picker.choices[courses[0]["Key"]].get_active());self.assertTrue(picker.choices[courses[1]["Key"]].get_active())
                picker.term_filter.set_selected(0)
                fx.wait(lambda:not picker.loading and picker.semester["term"]=="autumn")
                self.assertFalse(picker.choices[courses[0]["Key"]].get_active())
                self.assertEqual(len(picker.choices),4)
            finally:picker.close()
    @staticmethod
    def seed(fx):
        import sqlite3,json
        courses=[{"Key":"pta:"+str(i)*64,"Name":name,"Detail":detail,"Available":True} for i,name,detail in
            ((1,"正在学习的数学","正在上课"),(2,"未注明学期的课程","班级 1"),(3,"已完成历史课程","已结课"))]
        settings={"Revision":0,"SelectionConfirmed":False,"RecordAllCourses":False,"HideUnselectedTasks":True,"SelectedCourseKeys":[],"Courses":courses,"CatalogComplete":True}
        fx.request("get-courses",{"platform":"pta"})
        with sqlite3.connect(fx.temp.name+"/tasks.db") as connection:
            connection.execute("INSERT OR REPLACE INTO settings(key,value) VALUES(?,?)",("course-recording:pta",json.dumps(settings,ensure_ascii=False)))
        return courses

    def test_current_courses_are_prechecked_but_not_saved_until_review(self):
        from study_task_hub.course_picker import CoursePicker
        with TestAppFixture() as fx:
            courses=self.seed(fx)
            picker=CoursePicker(fx.window,fx.bridge,"pta")
            picker.present();fx.wait(lambda:len(picker.choices)==3)
            try:
                self.assertTrue(picker.choices[courses[0]["Key"]].get_active(),"Current course was not recommended")
                self.assertTrue(picker.choices[courses[1]["Key"]].get_active())
                self.assertFalse(picker.choices[courses[2]["Key"]].get_active())
                self.assertFalse(fx.request("get-courses",{"platform":"pta"})["selectionConfirmed"])
            finally:picker.close()

    def test_custom_course_and_user_exclusion_survive_review_and_restart(self):
        from study_task_hub.course_picker import CoursePicker
        with TestAppFixture() as fx:
            courses=self.seed(fx)
            picker=CoursePicker(fx.window,fx.bridge,"pta")
            picker.present();fx.wait(lambda:len(picker.choices)==3)
            self.assertTrue(hasattr(picker,"custom_name_entry"),"Custom course entry is missing")
            picker.choices[courses[0]["Key"]].set_active(False)
            picker.custom_name_entry.set_text("政治经济学")
            picker.add_custom_button.emit("clicked")
            self.assertEqual(len(picker.choices),4)
            self.assertEqual(len(fx.request("get-courses",{"platform":"pta"})["courses"]),3)
            picker.save_button.emit("clicked")
            fx.wait(lambda:len(fx.request("get-courses",{"platform":"pta"})["courses"])==4)
            plan=fx.request("get-course-plan",{"platform":"pta"})
            self.assertFalse(next(c for c in plan["courses"] if c["course"]["key"]==courses[0]["Key"])["preselected"])
            self.assertTrue(next(c for c in plan["courses"] if c["course"]["name"]=="政治经济学")["preselected"])
            self.assertEqual(fx.request("snapshot")["total"],0)
            from study_task_hub.bridge import BackendBridge
            fx.bridge.close()
            restarted=BackendBridge(fx.application.backend_path,fx.temp.name)
            try:
                restored=restarted.request_async("get-course-plan",{"platform":"pta"}).result(timeout=5)
                self.assertTrue(next(c for c in restored["courses"] if c["course"]["name"]=="政治经济学")["preselected"])
                self.assertFalse(next(c for c in restored["courses"] if c["course"]["key"]==courses[0]["Key"])["preselected"])
            finally:restarted.close()

    def test_cancel_discards_custom_course_and_search_preserves_choices(self):
        from study_task_hub.course_picker import CoursePicker
        with TestAppFixture() as fx:
            courses=self.seed(fx)
            picker=CoursePicker(fx.window,fx.bridge,"pta")
            picker.present();fx.wait(lambda:len(picker.choices)==3)
            self.assertTrue(hasattr(picker,"search_entry"),"Course search is missing")
            picker.search_entry.set_text("数学")
            picker.choices[courses[0]["Key"]].set_active(False)
            picker.search_entry.set_text("")
            self.assertFalse(picker.choices[courses[0]["Key"]].get_active())
            picker.custom_name_entry.set_text("取消的自定义课程")
            picker.add_custom_button.emit("clicked")
            picker.close()
            self.assertEqual(len(fx.request("get-courses",{"platform":"pta"})["courses"]),3)

if __name__=="__main__":unittest.main()
