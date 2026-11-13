import time
import tempfile
import os
import uuid
import gi
from linux_test_paths import backend_path
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Gtk, Adw, GLib
from study_task_hub.bridge import BackendBridge

def run_until(predicate, timeout=5):
    deadline = time.monotonic() + timeout
    while not predicate():
        if time.monotonic() >= deadline:
            raise AssertionError("Timed out waiting for application behavior")
        while GLib.MainContext.default().pending():
            GLib.MainContext.default().iteration(False)
        time.sleep(0.005)

class TestAppFixture:
    def __init__(self, width=1200, height=800):
        from study_task_hub.application import StudyTaskHubApplication
        from study_task_hub.window import MainWindow
        Adw.init()
        self.temp = tempfile.TemporaryDirectory(prefix="study-task-hub-ui-")
        os.chmod(self.temp.name, 0o700)
        self.bridge = BackendBridge(backend_path(), self.temp.name)
        self.application = StudyTaskHubApplication(backend_path(), self.temp.name)
        self.application.set_application_id("io.github.studyTaskHub.Test" + uuid.uuid4().hex)
        self.application.register(None)
        self.window = MainWindow(self.application, self.bridge)
        self.window.set_default_size(width, height)
        self.window.present()
    def request(self, command, args=None):
        return self.bridge.request_async(command, args).result(timeout=5)
    def wait(self, predicate):
        run_until(predicate)
    def close(self):
        self.window.dashboard.stop()
        self.window.destroy()
        self.bridge.close()
        self.application.quit()
        self.temp.cleanup()
        while GLib.MainContext.default().pending():
            GLib.MainContext.default().iteration(False)
    def __enter__(self):
        return self
    def __exit__(self, *args):
        self.close()
