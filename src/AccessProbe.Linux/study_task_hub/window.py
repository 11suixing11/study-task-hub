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
        sync_button = Gtk.Button(label="同步", tooltip_text="采集已登录平台的已选课程")
        sync_button.connect("clicked", self.sync_all)
        header.pack_start(sync_button)
        self.add_button = Gtk.Button(label="新增任务")
        self.add_button.add_css_class("suggested-action")
        self.add_button.connect("clicked", self.new_task)
        header.pack_end(self.add_button)
        self.platform_button = Gtk.Button(label="平台与课程")
        self.platform_button.connect("clicked", self.open_platforms)
        header.pack_end(self.platform_button)
        settings = Gtk.Button(icon_name="emblem-system-symbolic", tooltip_text="设置")
        settings.connect("clicked", self.open_settings)
        header.pack_end(settings)
        self.restart_button = Gtk.Button(label="恢复连接", visible=False)
        self.restart_button.connect("clicked", lambda *_: self.restart_backend())
        header.pack_start(self.restart_button)
        self.dashboard = Dashboard(self, bridge)
        self.dashboard.error_label.connect("notify::label", lambda *_: self.restart_button.set_visible(self.dashboard.error_label.get_text().startswith(("IPC004", "IPC006"))))
        toolbar.add_top_bar(header)
        toolbar.set_content(self.dashboard)
        self.set_content(toolbar)
        breakpoint = Adw.Breakpoint.new(Adw.BreakpointCondition.parse("max-width: 1100sp"))
        breakpoint.add_setter(self.dashboard.content, "collapsed", True)
        self.add_breakpoint(breakpoint)
        self.connect("close-request", self.on_close)

    def on_close(self, *_):
        application = self.get_application()
        if application.window == self:
            application.request_close()
            return True
        self.dashboard.stop()
        return False

    def new_task(self, *_):
        from .task_editor import TaskEditor
        TaskEditor(self, self.bridge).present()

    def open_platforms(self, *_):
        from .platform_window import PlatformWindow
        PlatformWindow(self, self.bridge).present()

    def open_settings(self, *_):
        from .settings_window import SettingsWindow
        SettingsWindow(self, self.bridge).present()

    def sync_all(self, *_):
        from .ui import on_result
        from .sync_rules import can_sync_tasks
        def loaded(views):
            eligible = [view for view in views if can_sync_tasks(view)]
            if not eligible:
                self.dashboard.show_error("请先在“平台与课程”完成登录并确认课程。")
                return
            for view in eligible:
                on_result(self.bridge.request_async("start-sync", {"platform":view["id"],"catalogOnly":False}), lambda _: None, self.dashboard.show_error)
        on_result(self.bridge.request_async("platforms"), loaded, self.dashboard.show_error)

    def restart_backend(self):
        if not self.restart_button.get_sensitive() or self.get_application().closing:
            return
        from .bridge import BackendBridge
        from .ui import on_result
        import concurrent.futures
        import threading
        self.restart_button.set_sensitive(False)
        app = self.get_application()
        if app.scheduler:
            app.scheduler.stop()
        future = concurrent.futures.Future()
        def restart():
            self.bridge.close()
            if app.closing:
                future.set_exception(RuntimeError())
                return
            try:
                bridge = BackendBridge(app.backend_path, app.data_root)
                if app.closing:
                    bridge.close(timeout=0)
                    future.set_exception(RuntimeError())
                else:
                    future.set_result(bridge)
            except OSError:
                future.set_exception(RuntimeError())
        def success(bridge):
            if app.closing:
                bridge.close(timeout=0)
                return
            self.bridge = bridge
            self.dashboard.bridge = bridge
            self.dashboard.refresh()
            if app.window == self:
                app.bridge = bridge
                from .scheduler import AppScheduler
                app.scheduler = AppScheduler(bridge, app.notify, busy=app.has_open_dialog)
                from gi.repository import GLib
                bridge.subscribe_events(lambda event: GLib.idle_add(app.backend_event, event))
                for dialog in app.owned_dialogs():
                    if hasattr(dialog, "rebind_bridge"):
                        dialog.rebind_bridge(bridge)
                    elif hasattr(dialog, "bridge"):
                        dialog.bridge = bridge
            self.restart_button.set_sensitive(True)
        def failure(message):
            if app.closing:
                return
            self.restart_button.set_sensitive(True)
            self.dashboard.show_error(message)
        threading.Thread(target=restart, daemon=True).start()
        on_result(future, success, failure)
