from gi.repository import Gtk, Adw
from .ui import label, margins, on_result, recovery_button

class SettingsWindow(Adw.Window):
    def __init__(self, parent, bridge):
        super().__init__(transient_for=parent, modal=True, title="设置")
        self.bridge, self.settings = bridge, None
        self.closed = False
        self.connect("close-request", self.on_close)
        self.set_default_size(560, 420)
        toolbar = Adw.ToolbarView()
        toolbar.add_top_bar(Adw.HeaderBar())
        body = margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=16), 20)
        self.notifications = Gtk.CheckButton(label="发送任务提醒")
        self.automatic = Gtk.CheckButton(label="应用运行时每 30 分钟采集已选课程")
        body.append(self.notifications)
        body.append(self.automatic)
        body.append(label("数据保存在：", "heading"))
        location = label(str(parent.get_application().data_root), "dim-label")
        location.set_selectable(True)
        body.append(location)
        self.error = label("", "error")
        body.append(self.error)
        body.append(recovery_button(self, self.error))
        save = Gtk.Button(label="保存设置", sensitive=False)
        save.add_css_class("suggested-action")
        self.save_button = save
        body.append(save)
        toolbar.set_content(body)
        self.set_content(toolbar)
        save.connect("clicked", self.save)
        self.load_settings()

    def load_settings(self):
        requested_bridge = self.bridge
        def loaded(settings):
            if self.closed or self.bridge is not requested_bridge:
                return
            self.settings = settings
            self.notifications.set_active(settings["notificationsEnabled"])
            self.automatic.set_active(settings["automaticSyncEnabled"])
            self.save_button.set_sensitive(True)
        on_result(requested_bridge.request_async("get-settings"), loaded,
            lambda message: self.error.set_text(message) if not self.closed and self.bridge is requested_bridge else None)

    def save(self, *_):
        if self.closed or self.settings is None:
            return
        requested_bridge = self.bridge
        settings = dict(self.settings, notificationsEnabled=self.notifications.get_active(),
            automaticSyncEnabled=self.automatic.get_active(), chaoxingAutomaticSyncEnabled=self.automatic.get_active())
        self.save_button.set_sensitive(False)
        def saved(_):
            if not self.closed and self.bridge is requested_bridge:
                self.close()
        def failed(message):
            if not self.closed and self.bridge is requested_bridge:
                self.error.set_text(message)
                self.save_button.set_sensitive(True)
        on_result(requested_bridge.request_async("save-settings", {"settings":settings}), saved, failed)

    def rebind_bridge(self, bridge):
        if self.closed:
            return
        self.bridge = bridge
        self.error.set_text("")
        self.save_button.set_sensitive(self.settings is not None)
        if self.settings is None:
            self.load_settings()

    def on_close(self, *_):
        self.closed = True
        return False
