import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gio

class StudyTaskHubApplication(Adw.Application):
    def __init__(self, backend_path, data_root):
        super().__init__(application_id="io.github.studyTaskHub", flags=Gio.ApplicationFlags.NON_UNIQUE)
        self.backend_path=backend_path
        self.data_root=data_root
