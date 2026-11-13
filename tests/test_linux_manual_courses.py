import unittest
from linux_ui_helpers import TestAppFixture
from study_task_hub.ui import PLATFORMS

class ManualCourseChecks(unittest.TestCase):
    def test_editor_selects_registered_course_and_manager_renames_and_archives(self):
        from study_task_hub.task_editor import TaskEditor
        from study_task_hub.manual_courses import ManualCoursesWindow
        with TestAppFixture() as fx:
            plan=fx.request("get-course-plan",{"platform":"pta"})
            fx.request("save-course-selection",{"platform":"pta","revision":plan["revision"],"semester":plan["semester"],"keys":[],"customCourses":[{"name":"数学"}]})
            editor=TaskEditor(fx.window,fx.bridge);editor.present()
            editor.fields["platformId"].set_selected([key for key,_ in PLATFORMS[1:]].index("pta"))
            fx.wait(lambda:len(editor.registered_courses)==1)
            editor.course_choice.set_selected(1);editor.fields["title"].set_text("课程绑定任务")
            editor.save_button.emit("clicked");fx.wait(lambda:editor.saved_detail is not None)
            task=editor.saved_detail["view"]["task"];key=task["recordingCourseKey"]
            self.assertEqual(task["courseName"],"数学")
            manager=ManualCoursesWindow(fx.window,fx.bridge,"pta");manager.present()
            fx.wait(lambda:manager.page is not None)
            fields=manager.fields[key];fields["name"].set_text("高等数学")
            fields["save"].emit("clicked")
            fx.wait(lambda:fx.request("get-task",{"id":task["id"]})["view"]["task"]["courseName"]=="高等数学")
            fx.wait(lambda:manager.page["revision"]>1)
            manager.fields[key]["archive"].emit("clicked")
            fx.wait(lambda:fx.request("snapshot")["total"]==0)
            history=fx.request("get-task",{"id":task["id"]})["view"]["task"]
            self.assertEqual(history["recordingCourseKey"],key);self.assertEqual(history["courseName"],"高等数学")
            manager.destroy()

if __name__=="__main__":unittest.main()
