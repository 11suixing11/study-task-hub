from gi.repository import Gtk, Adw
from .ui import label, margins, on_result

class CoursePicker(Adw.Window):
    def __init__(self, parent, bridge, platform, saved=None):
        super().__init__(transient_for=parent, modal=True, title="选择需要记录的课程")
        self.bridge, self.platform, self.saved = bridge, platform, saved
        self.set_default_size(660, 620)
        self.recording = None
        self.choices = {}
        toolbar = Adw.ToolbarView()
        toolbar.add_top_bar(Adw.HeaderBar())
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        body.append(margins(label("勾选课程后保存。取消勾选会保留历史任务，并停止该课程的采集和提醒。")))
        self.rows = margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12))
        body.append(Gtk.ScrolledWindow(child=self.rows, vexpand=True))
        self.error_label = margins(label("", "error"))
        body.append(self.error_label)
        self.save_button = Gtk.Button(label="保存课程选择", sensitive=False)
        self.save_button.add_css_class("suggested-action")
        self.save_button.connect("clicked", lambda *_: self.save())
        body.append(margins(self.save_button))
        toolbar.set_content(body)
        self.set_content(toolbar)
        on_result(bridge.request_async("get-courses", {"platform":platform}), self.load, self.error_label.set_text)

    def load(self, value):
        self.recording = value
        for course in value["courses"]:
            choice = Gtk.CheckButton(label=course["name"] + (" · 暂不可用" if not course["available"] else ""))
            choice.set_active(course["key"] in value["selectedCourseKeys"] or value["recordAllCourses"])
            self.choices[course["key"]] = choice
            self.rows.append(choice)
            self.rows.append(label(course["detail"], "dim-label"))
        if not value["courses"]:
            self.rows.append(label("尚未读取到课程。请完成登录后刷新课程目录；读取未完整时不能解释为没有课程。"))
        self.save_button.set_sensitive(True)

    def save(self):
        if not self.recording:
            return
        self.save_button.set_sensitive(False)
        def success(_):
            if self.saved:
                self.saved()
            self.close()
        def failure(message):
            self.error_label.set_text(message)
            self.save_button.set_sensitive(True)
        on_result(self.bridge.request_async("select-courses", {"platform":self.platform,"revision":self.recording["revision"],
            "keys":[key for key, choice in self.choices.items() if choice.get_active()],"hideUnselected":True}), success, failure)
