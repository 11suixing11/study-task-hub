from gi.repository import Gtk, Adw
from .ui import label, margins, on_result

class SettingsWindow(Adw.Window):
    def __init__(self, parent, bridge):
        super().__init__(transient_for=parent, modal=True, title="设置")
        self.bridge, self.settings = bridge, None
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
        save = Gtk.Button(label="保存设置", sensitive=False)
        save.add_css_class("suggested-action")
        body.append(save)
        toolbar.set_content(body)
        self.set_content(toolbar)
        def loaded(settings):
            self.settings = settings
            self.notifications.set_active(settings["notificationsEnabled"])
            self.automatic.set_active(settings["automaticSyncEnabled"])
            save.set_sensitive(True)
        def save_settings(*_):
            if self.settings is None:
                return
            settings = dict(self.settings, notificationsEnabled=self.notifications.get_active(),
                automaticSyncEnabled=self.automatic.get_active(), chaoxingAutomaticSyncEnabled=self.automatic.get_active())
            on_result(bridge.request_async("save-settings", {"settings":settings}), lambda _: self.close(), self.error.set_text)
        save.connect("clicked", save_settings)
        on_result(bridge.request_async("get-settings"), loaded, self.error.set_text)
