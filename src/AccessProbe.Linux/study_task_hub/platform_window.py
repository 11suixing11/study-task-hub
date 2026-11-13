from pathlib import Path
import shutil
from gi.repository import Gtk, Adw, GLib
from .ui import label, margins, on_result, recovery_button

class PlatformWindow(Adw.Window):
    def __init__(self, parent, bridge):
        super().__init__(transient_for=parent, modal=True, title="平台与课程")
        self.parent, self.bridge = parent, bridge
        self.views = []
        self.active_jobs = {}
        self.early_events = {}
        self.last_result = None
        self.disposed = False
        self.set_default_size(820, 720)
        toolbar = Adw.ToolbarView()
        toolbar.add_top_bar(Adw.HeaderBar())
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        self.status = margins(label("登录后先确认课程，再采集任务。各平台实际覆盖范围以官网和读取结果为准。", "dim-label"))
        body.append(self.status)
        body.append(margins(recovery_button(self, self.status)))
        self.rows = margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=16), 20)
        body.append(Gtk.ScrolledWindow(child=self.rows, vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER))
        toolbar.set_content(body)
        self.set_content(toolbar)
        self.connect("close-request", self.on_close)
        self.unsubscribe = bridge.subscribe_events(lambda event: self.receive_event(event, bridge))
        self.refresh()

    def refresh(self):
        requested_bridge = self.bridge
        on_result(requested_bridge.request_async("platforms"),
            lambda views: self.render(views) if self.bridge is requested_bridge else None,
            lambda message: self.status.set_text(message) if self.bridge is requested_bridge and not self.disposed else None)

    def rebind_bridge(self, bridge):
        if self.disposed:
            return
        self.unsubscribe()
        self.bridge = bridge
        self.active_jobs.clear()
        self.early_events.clear()
        self.last_result = None
        self.unsubscribe = bridge.subscribe_events(lambda event: self.receive_event(event, bridge))
        self.status.set_text("连接已恢复，请重新发起读取。")
        self.refresh()

    def render(self, views):
        if self.disposed:
            return
        self.views = views
        while child := self.rows.get_first_child():
            self.rows.remove(child)
        for view in views:
            card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
            card.append(label(view["name"], "title-3"))
            if not view["loginUri"]:
                card.append(label("手动模式 · 可在首页新增或查看历史任务。", "dim-label"))
                self.rows.append(card)
                continue
            card.append(label(view["session"]["description"], "dim-label"))
            recording = view["recording"]
            refresh = recording.get("lastCatalogRefresh")
            catalog_text = "课程目录尚未刷新" if not refresh else "课程刷新：" + refresh["outcome"] + " · " + "、".join(refresh["problemCodes"])
            card.append(label(f"{catalog_text}\n已选 {len(recording['selectedCourseKeys'])} 门课程 · 最近任务采集：{view['taskSyncOutcome']}", "dim-label"))
            actions = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, max_children_per_line=5, min_children_per_line=2)
            for title, action in (("连接官网", lambda _, v=view: self.connect_platform(v)),
                ("刷新课程", lambda _, p=view["id"]: self.start_sync(p, True)),
                ("选择课程", lambda _, p=view["id"]: self.pick_courses(p)),
                ("采集任务", lambda _, p=view["id"]: self.start_sync(p, False)),
                ("停止读取", lambda _, p=view["id"]: self.stop_platform(p)),
                ("清除登录", lambda _, p=view["id"]: self.clear_login(p))):
                button = Gtk.Button(label=title)
                button.connect("clicked", action)
                actions.insert(button, -1)
            card.append(actions)
            self.rows.append(card)
            self.rows.append(Gtk.Separator())

    def connect_platform(self, view):
        from .login_window import LoginWindow
        LoginWindow(self.parent, self.bridge, view).present()

    def pick_courses(self, platform):
        from .course_picker import CoursePicker
        from .sync_rules import has_recorded_official_courses
        def saved(recording):
            self.refresh()
            self.parent.dashboard.refresh()
            if has_recorded_official_courses(recording):
                self.start_sync(platform, False)
        CoursePicker(self, self.bridge, platform, saved=saved).present()

    def start_sync(self, platform, catalog_only=False):
        if platform in self.active_jobs:
            self.status.set_text("该平台正在读取，请先停止当前操作。")
            return
        self.active_jobs[platform] = None
        self.status.set_text("正在开始课程读取…" if catalog_only else "正在开始任务采集…")
        requested_bridge = self.bridge
        def started(result):
            if self.disposed or self.bridge is not requested_bridge:
                return
            self.active_jobs[platform] = result["jobId"]
            if event := self.early_events.pop(result["jobId"], None):
                self.apply_event(event)
        def failed(message):
            if self.disposed or self.bridge is not requested_bridge:
                return
            self.active_jobs.pop(platform, None)
            self.status.set_text(message)
        on_result(self.bridge.request_async("start-sync", {"platform":platform,"catalogOnly":catalog_only}), started, failed)

    def stop_platform(self, platform):
        job_id = self.active_jobs.get(platform)
        if job_id:
            on_result(self.bridge.request_async("cancel-sync", {"jobId":job_id}), lambda _: self.status.set_text("正在停止读取…"), self.status.set_text)

    def receive_event(self, event, source=None):
        GLib.idle_add(self.apply_event, event, source)

    def apply_event(self, event, source=None):
        if self.disposed or source is not None and source is not self.bridge:
            return GLib.SOURCE_REMOVE
        payload = event["payload"]
        if self.active_jobs.get(payload["platform"]) != payload["jobId"]:
            if payload["platform"] in self.active_jobs:
                if len(self.early_events) >= 32:
                    self.early_events.clear()
                self.early_events[payload["jobId"]] = event
            elif event["event"] == "job-finished":
                self.refresh()
                self.parent.dashboard.refresh()
            return GLib.SOURCE_REMOVE
        if event["event"] == "progress":
            self.status.set_text(payload["message"])
        elif event["event"] == "job-finished":
            self.active_jobs.pop(payload["platform"], None)
            self.last_result = payload
            error = payload.get("error")
            self.status.set_text(f"{error['code']} · {error['message']}" if error else payload["result"]["message"])
            self.refresh()
            self.parent.dashboard.refresh()
        return GLib.SOURCE_REMOVE

    def clear_login(self, platform):
        dialog = Adw.MessageDialog(transient_for=self, modal=True, heading="清除此平台的登录？", body="会移除保存的会话与浏览器登录环境。任务和课程记录保留。")
        dialog.add_response("cancel", "取消")
        dialog.add_response("clear", "清除登录")
        dialog.set_response_appearance("clear", Adw.ResponseAppearance.DESTRUCTIVE)
        dialog.set_close_response("cancel")
        def response(_, name):
            if name != "clear":
                return
            def cleared(_):
                path = Path(self.parent.get_application().data_root) / "profiles" / platform
                try:
                    if path.is_symlink():
                        raise OSError
                    if path.exists():
                        shutil.rmtree(path)
                except OSError:
                    self.status.set_text("会话已清除，浏览器目录清理失败；请检查数据目录权限。")
                self.refresh()
            on_result(self.bridge.request_async("clear-session", {"platform":platform}), cleared, self.status.set_text)
        dialog.connect("response", response)
        dialog.present()

    def on_close(self, *_):
        self.disposed = True
        self.unsubscribe()
        return False
