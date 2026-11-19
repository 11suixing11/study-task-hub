import unittest
from pathlib import Path
from linux_ui_helpers import TestAppFixture

class BackupChecks(unittest.TestCase):
    def test_gui_checks_backup_then_restores_only_after_confirmation(self):
        from study_task_hub.backup_window import BackupWindow
        with TestAppFixture() as fx:
            task=fx.request("create-task",{"title":"备份原任务","platformId":"pta","courseName":"","deadlineKind":"unknown","scoreText":"","notes":"原备注"})["view"]["task"]
            path=str(Path(fx.temp.name)/"gui.studybackup")
            window=BackupWindow(fx.window,fx.bridge);window.present()
            window.path_entry.set_text(path);window.password_entry.set_text("test-password-only")
            window.create_button.emit("clicked");fx.wait(lambda:window.preview is not None)
            fx.request("edit-task",{"id":task["id"],"revision":task["revision"],"input":{"title":task["title"],"platformId":"pta","courseName":"","deadlineKind":"unknown","scoreText":"","notes":"较新备注"}})
            window.inspect_button.emit("clicked");fx.wait(lambda:window.restore_button.get_sensitive())
            dialog=window.confirm_restore()
            self.assertEqual(fx.request("get-task",{"id":task["id"]})["view"]["task"]["notes"],"较新备注")
            dialog.emit("response","restore")
            fx.wait(lambda:window.restored is not None)
            self.assertEqual(fx.request("get-task",{"id":task["id"]})["view"]["task"]["notes"],"原备注")
            self.assertTrue(Path(window.restored["rollbackBackupPath"]).is_file())
            window.destroy()

if __name__=="__main__":unittest.main()
