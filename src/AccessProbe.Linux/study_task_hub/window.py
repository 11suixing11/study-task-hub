import gi
gi.require_version("Gtk","4.0")
gi.require_version("Adw","1")
from gi.repository import Adw, Gtk
from .dashboard import Dashboard

class MainWindow(Adw.ApplicationWindow):
    def __init__(self, application, bridge):
        super().__init__(application=application)
        self.bridge = bridge
        self.set_title("聚合平台")
        self.set_default_size(1200, 800)
        self.set_size_request(1000, 700)
        toolbar = Adw.ToolbarView()
        header = Adw.HeaderBar()
        header.set_title_widget(Adw.WindowTitle(title="聚合平台", subtitle="学习任务"))
        self.refresh_button = Gtk.Button(icon_name="view-refresh-symbolic", tooltip_text="刷新本地任务")
        self.refresh_button.connect("clicked", lambda *_: self.dashboard.refresh())
        header.pack_start(self.refresh_button)
        self.add_button = Gtk.Button(label="新增任务")
        self.add_button.add_css_class("suggested-action")
        self.add_button.connect("clicked", self.new_task)
        header.pack_end(self.add_button)
        self.dashboard = Dashboard(self, bridge)
        toolbar.add_top_bar(header)
        toolbar.set_content(self.dashboard)
        self.set_content(toolbar)
        self.connect("close-request", self.on_close)

    def on_close(self, *_):
        self.dashboard.stop()
        return False

    def new_task(self, *_):
        from .task_editor import TaskEditor
        TaskEditor(self, self.bridge).present()
