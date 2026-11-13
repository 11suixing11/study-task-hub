import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gio, Gtk, GLib
import threading
import time
from .bridge import BackendBridge
from .scheduler import AppScheduler

class StudyTaskHubApplication(Adw.Application):
    def __init__(self, backend_path, data_root):
        super().__init__(application_id="io.github.studyTaskHub", flags=Gio.ApplicationFlags.NON_UNIQUE)
        self.backend_path=backend_path
        self.data_root=data_root
        self.bridge = None
        self.window = None
        self.scheduler = None
        self.closing = False
        self.close_source = None

    def do_activate(self):
        if self.window:
            self.window.present()
            return
        from .window import MainWindow
        try:
            self.bridge = BackendBridge(self.backend_path, self.data_root)
            self.window = MainWindow(self, self.bridge)
            self.bridge.subscribe_events(lambda event: GLib.idle_add(self.backend_event, event))
            self.scheduler = AppScheduler(self.bridge, self.notify, busy=self.has_open_dialog)
            self.window.present()
            self.scheduler.tick()
        except OSError:
            dialog = Adw.MessageDialog(heading="无法启动聚合平台", body="后台程序或依赖不可用，请重新安装完整应用。")
            dialog.add_response("close", "关闭")
            dialog.connect("response", lambda *_: self.quit())
            dialog.present()

    def backend_event(self, event):
        if self.window and not self.closing and event["event"] == "job-finished":
            self.window.dashboard.refresh()
        return GLib.SOURCE_REMOVE

    def owned_dialogs(self):
        windows = Gtk.Window.get_toplevels()
        result = []
        for index in range(windows.get_n_items()):
            window = windows.get_item(index)
            ancestor = window.get_transient_for()
            while ancestor and ancestor != self.window:
                ancestor = ancestor.get_transient_for()
            if ancestor == self.window and window.get_visible():
                result.append(window)
        return result

    def has_open_dialog(self):
        return bool(self.owned_dialogs()) or self.closing

    def notify(self, item):
        notification = Gio.Notification.new(item["title"])
        notification.set_body(item["body"])
        self.send_notification(item["id"], notification)
        return True

    def request_close(self):
        if self.closing:
            return
        self.closing = True
        close_deadline = time.monotonic() + 5
        if self.scheduler:
            self.scheduler.stop()
        if self.window:
            self.window.set_sensitive(False)
            self.window.dashboard.stop()
        for dialog in self.owned_dialogs():
            if hasattr(dialog, "close_with_save"):
                dialog.close_with_save()
            else:
                dialog.close()
        def finish():
            if self.bridge:
                self.bridge.close(timeout=max(0,close_deadline-time.monotonic()))
            GLib.idle_add(self.finish_close)
        # GTK callbacks remain on the main thread while jobs and IPC drain.
        def await_logins():
            logins = [w for w in self.owned_dialogs() if hasattr(w, "close_with_save") and not w.closed]
            if logins and time.monotonic() < close_deadline:
                return GLib.SOURCE_CONTINUE
            for login in logins:
                login.finish_close("退出保存等待已超时；原会话保留，请重新确认登录。")
            threading.Thread(target=finish, daemon=True).start()
            self.close_source = None
            return GLib.SOURCE_REMOVE
        self.close_source = GLib.timeout_add(50, await_logins)

    def finish_close(self):
        if self.close_source:
            GLib.source_remove(self.close_source)
            self.close_source = None
        if self.window:
            self.window.destroy()
            self.window = None
        self.quit()
        return GLib.SOURCE_REMOVE

    def do_shutdown(self):
        if self.scheduler:
            self.scheduler.stop()
        if self.bridge:
            self.bridge.close()
        Adw.Application.do_shutdown(self)
