import datetime
import zoneinfo
from decimal import Decimal, InvalidOperation
from gi.repository import Gtk, Adw
from .ui import PLATFORMS, label, margins, on_result

def local_zone():
    with open("/etc/localtime", "rb") as stream:
        return zoneinfo.ZoneInfo.from_file(stream)

def parse_local_datetime(text, zone):
    naive = datetime.datetime.strptime(text, "%Y-%m-%d %H:%M")
    candidates = [naive.replace(tzinfo=zone, fold=fold) for fold in (0, 1)]
    valid = [value for value in candidates if value.astimezone(datetime.timezone.utc).astimezone(zone).replace(tzinfo=None) == naive]
    if not valid or len({value.utcoffset() for value in valid}) != 1:
        raise ValueError("此本地时间处于夏令时切换区间，请选择明确时间。")
    return valid[0].isoformat(timespec="seconds")

class TaskEditor(Adw.Window):
    def __init__(self, parent, bridge, task_id=None):
        super().__init__(transient_for=parent, modal=True, title="编辑任务" if task_id else "新增任务")
        self.parent, self.bridge, self.task_id = parent, bridge, task_id
        self.set_default_size(640, 700)
        self.zone = local_zone()
        self.loaded = task_id is None
        self.saved_detail = None
        self.revision = None
        self.original_dates = {}
        self.original_text = {}
        self.closed = False
        self.busy = False
        self.fields = {}
        self.connect("close-request", self.on_close)
        toolbar = Adw.ToolbarView()
        toolbar.add_top_bar(Adw.HeaderBar())
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        form = margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10), 20)
        form.append(label("任务信息", "title-3"))
        for key, title in (("title", "任务名称"), ("courseName", "课程名称")):
            self.add_entry(form, key, title)
        self.fields["platformId"] = Gtk.DropDown.new_from_strings([name for _, name in PLATFORMS[1:]])
        self.add_field(form, "来源平台", self.fields["platformId"])
        form.append(label("时间安排", "title-3"))
        self.fields["deadlineKind"] = Gtk.DropDown.new_from_strings(["截止时间未知", "明确截止时间", "无截止时间"])
        self.add_field(form, "截止类型", self.fields["deadlineKind"])
        self.add_entry(form, "begin", "开放时间", "yyyy-MM-dd HH:mm，可留空")
        self.add_entry(form, "deadline", "截止时间", "yyyy-MM-dd HH:mm，本地时间")
        self.fields["confirmDeadline"] = Gtk.CheckButton(label="已核对官网截止时间")
        form.append(self.fields["confirmDeadline"])
        self.fields["deadlineKind"].connect("notify::selected", self.update_deadline)
        self.update_deadline()
        form.append(label("补充信息", "title-3"))
        self.add_entry(form, "link", "官方任务链接", "完整 HTTPS 地址，可留空")
        self.add_entry(form, "progressPercent", "进度百分比", "0–100，可留空")
        self.add_entry(form, "scoreText", "成绩", "可留空")
        self.fields["priorityOverride"] = Gtk.DropDown.new_from_strings(["自动优先级", "P1", "P2", "P3", "P4"])
        self.add_field(form, "优先级", self.fields["priorityOverride"])
        notes = Gtk.TextView(wrap_mode=Gtk.WrapMode.WORD_CHAR)
        self.fields["notes"] = notes
        form.append(label("备注"))
        form.append(Gtk.ScrolledWindow(child=notes, min_content_height=100))
        body.append(Gtk.ScrolledWindow(child=form, vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER))
        self.error_label = margins(label("", "error"))
        self.error_label.set_visible(False)
        body.append(self.error_label)
        actions = margins(Gtk.Box(spacing=12), 16)
        cancel = Gtk.Button(label="取消", hexpand=True)
        cancel.connect("clicked", lambda *_: self.close())
        self.save_button = Gtk.Button(label="保存任务", hexpand=True, sensitive=self.loaded)
        self.save_button.add_css_class("suggested-action")
        self.save_button.connect("clicked", lambda *_: self.save())
        actions.append(cancel)
        actions.append(self.save_button)
        body.append(actions)
        toolbar.set_content(body)
        self.set_content(toolbar)
        if task_id:
            on_result(bridge.request_async("get-task", {"id": task_id}), self.load, self.show_error)

    def add_field(self, form, title, widget):
        form.append(label(title))
        form.append(widget)

    def add_entry(self, form, key, title, placeholder=None):
        self.fields[key] = Gtk.Entry(placeholder_text=placeholder)
        self.add_field(form, title, self.fields[key])

    def update_deadline(self, *_):
        known = self.fields["deadlineKind"].get_selected() == 1
        self.fields["deadline"].set_sensitive(known)
        self.fields["confirmDeadline"].set_sensitive(known)

    def load(self, detail):
        if self.closed:
            return
        task = detail["view"]["task"]
        self.revision = task["revision"]
        for key in ("title", "courseName", "scoreText"):
            self.fields[key].set_text(task[key])
        self.fields["notes"].get_buffer().set_text(task["notes"])
        self.fields["link"].set_text(detail.get("link") or "")
        self.fields["progressPercent"].set_text(str(task["progressPercent"]) if task.get("progressPercent") is not None else "")
        keys = [key for key, _ in PLATFORMS[1:]]
        self.fields["platformId"].set_selected(keys.index(task["platformId"]))
        self.fields["platformId"].set_sensitive(task["source"] == "manual")
        self.fields["deadlineKind"].set_selected(("unknown", "known", "none").index(task["deadlineKind"]))
        self.fields["priorityOverride"].set_selected(task.get("priorityOverride") or 0)
        for field, key in (("begin", "beginUtc"), ("deadline", "deadlineUtc")):
            if task.get(key):
                date = datetime.datetime.fromisoformat(task[key].replace("Z", "+00:00"))
                self.fields[field].set_text(date.astimezone(self.zone).strftime("%Y-%m-%d %H:%M"))
                self.original_dates[field] = task[key]
                self.original_text[field] = self.fields[field].get_text()
        self.loaded = True
        self.save_button.set_sensitive(True)

    def input(self):
        fields = self.fields
        kind = ("unknown", "known", "none")[fields["deadlineKind"].get_selected()]
        raw_progress = fields["progressPercent"].get_text().strip()
        progress = None
        if raw_progress:
            try:
                value = Decimal(raw_progress)
                if not value.is_finite() or not 0 <= value <= 100:
                    raise InvalidOperation
                progress = float(value)
            except InvalidOperation:
                raise ValueError("进度必须是 0–100 的百分比。")
        buffer = fields["notes"].get_buffer()
        def date_input(field):
            text = fields[field].get_text().strip()
            if not text:
                return None
            if text == self.original_text.get(field):
                return self.original_dates[field]
            return parse_local_datetime(text, self.zone)
        return {"title": fields["title"].get_text(), "platformId": PLATFORMS[fields["platformId"].get_selected()+1][0],
            "courseName": fields["courseName"].get_text(), "deadlineKind": kind,
            "beginIso": date_input("begin"),
            "deadlineIso": date_input("deadline") if kind == "known" else None,
            "link": fields["link"].get_text().strip() or None, "progressPercent": progress,
            "scoreText": fields["scoreText"].get_text(), "notes": buffer.get_text(buffer.get_start_iter(), buffer.get_end_iter(), True),
            "priorityOverride": fields["priorityOverride"].get_selected() or None,
            "confirmDeadline": kind == "known" and fields["confirmDeadline"].get_active()}

    def save(self):
        if self.busy or not self.loaded or self.closed:
            return
        try:
            value = self.input()
        except ValueError as error:
            self.show_error(str(error) if "格式" in str(error) or "进度" in str(error) or "夏令时" in str(error) else "请使用 yyyy-MM-dd HH:mm 填写完整时间。")
            return
        self.busy = True
        self.save_button.set_sensitive(False)
        self.error_label.set_visible(False)
        command, args = ("create-task", value) if self.task_id is None else ("edit-task", {"id": self.task_id, "revision": self.revision, "input": value})
        def success(detail):
            self.busy = False
            self.saved_detail = detail
            self.parent.dashboard.refresh()
            self.close()
        on_result(self.bridge.request_async(command, args), success, self.show_error)

    def show_error(self, message):
        if self.closed:
            return
        self.busy = False
        self.error_label.set_text(message)
        self.error_label.set_visible(True)
        self.save_button.set_sensitive(self.loaded)

    def on_close(self, *_):
        self.closed = True
        return False
