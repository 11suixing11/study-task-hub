from gi.repository import Gtk, Gio, GObject, Adw
from .ui import PLATFORMS, STATES, label, margins, on_result
from .task_details import format_task_detail

TASK_STATES = STATES[1:]

class TaskRow(GObject.Object):
    def __init__(self, view):
        super().__init__()
        self.view = view

class Dashboard(Gtk.Box):
    def __init__(self, parent, bridge):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        self.parent, self.bridge = parent, bridge
        self.generation = 0
        self.page = None
        self.disposed = False
        self.loading = False
        self.details_generation = 0
        self.selected_detail = None
        self.summary_label = label("正在读取本地任务…", "title-3")
        self.append(margins(self.summary_label, 16))
        filters = margins(Gtk.Box(spacing=8))
        self.search_entry = Gtk.SearchEntry(placeholder_text="搜索任务、课程或备注", hexpand=True)
        self.search_entry.connect("search-changed", lambda *_: self.refresh())
        self.state_filter = Gtk.DropDown.new_from_strings([text for _, text in STATES])
        self.state_filter.connect("notify::selected", lambda *_: self.refresh())
        self.sort_filter = Gtk.DropDown.new_from_strings(["优先级", "截止时间", "最近更新"])
        self.sort_filter.connect("notify::selected", lambda *_: self.refresh())
        for widget in (self.search_entry, self.state_filter, self.sort_filter):
            filters.append(widget)
        self.append(filters)
        self.error_label = margins(label("", "error"))
        self.error_label.set_visible(False)
        self.append(self.error_label)
        self.body = Gtk.Paned(orientation=Gtk.Orientation.HORIZONTAL, vexpand=True)
        sidebar = margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12))
        sidebar.set_size_request(155, -1)
        sidebar.append(label("任务来源", "heading"))
        self.platform_filter = Gtk.DropDown.new_from_strings([text for _, text in PLATFORMS])
        self.platform_filter.connect("notify::selected", lambda *_: self.refresh())
        sidebar.append(self.platform_filter)
        self.sources_label = label("", "dim-label")
        sidebar.append(self.sources_label)
        self.history_switch = Gtk.CheckButton(label="查看未选课程的历史")
        self.history_switch.connect("toggled", lambda *_: self.refresh())
        sidebar.append(self.history_switch)
        self.body.set_start_child(sidebar)
        self.body.set_resize_start_child(False)
        self.body.set_shrink_start_child(False)
        self.content = Adw.OverlaySplitView(sidebar_position=Gtk.PackType.END)
        self.content.set_min_sidebar_width(280)
        self.content.set_max_sidebar_width(360)
        self.content.set_sidebar_width_fraction(0.35)
        center = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.total_label = margins(label("", "dim-label"))
        center.append(self.total_label)
        self.task_model = Gio.ListStore.new(TaskRow)
        self.selection = Gtk.SingleSelection.new(self.task_model)
        self.selection.set_autoselect(False)
        self.selection.set_can_unselect(True)
        self.selection.connect("notify::selected-item", self.show_selection)
        factory = Gtk.SignalListItemFactory()
        factory.connect("setup", self.setup_row)
        factory.connect("bind", self.bind_row)
        self.list_view = Gtk.ListView.new(self.selection, factory)
        self.list_view.add_css_class("navigation-sidebar")
        self.list_view.connect("activate", lambda _, index: self.selection.set_selected(index))
        scrolled = Gtk.ScrolledWindow(vexpand=True, hexpand=True, child=self.list_view)
        scrolled.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.empty_label = margins(label("还没有任务。可以新增手动任务，或连接平台后确认课程。"))
        center.append(self.empty_label)
        center.append(scrolled)
        self.load_more_button = Gtk.Button(label="加载更多任务")
        self.load_more_button.connect("clicked", lambda *_: self.load_more())
        center.append(margins(self.load_more_button))
        self.content.set_content(center)
        self.details = margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12), 16)
        self.details.set_size_request(235, -1)
        self.back_button = Gtk.Button(label="返回任务清单", visible=False)
        self.back_button.connect("clicked", lambda *_: self.content.set_show_sidebar(False))
        self.details.append(self.back_button)
        self.content.connect("notify::collapsed", lambda *_: self.back_button.set_visible(self.content.get_collapsed()))
        self.detail_title = label("选择一个任务", "title-2")
        self.detail_body = label("查看时间、备注和任务状态。", "dim-label")
        self.details.append(self.detail_title)
        self.details.append(self.detail_body)
        self.edit_button = Gtk.Button(label="编辑任务", sensitive=False)
        self.edit_button.connect("clicked", lambda *_: self.edit_selected())
        self.details.append(self.edit_button)
        self.open_button = Gtk.Button(label="打开官方任务 ↗", sensitive=False)
        self.open_button.connect("clicked", lambda *_: self.open_selected())
        self.details.append(self.open_button)
        self.details.append(label("任务状态", "heading"))
        self.state_action = Gtk.DropDown.new_from_strings([text for _, text in TASK_STATES])
        self.state_action.set_sensitive(False)
        self.details.append(self.state_action)
        self.state_button = Gtk.Button(label="更新任务状态", sensitive=False)
        self.state_button.connect("clicked", lambda *_: self.change_state())
        self.details.append(self.state_button)
        self.delete_button = Gtk.Button(label="删除任务", sensitive=False)
        self.delete_button.add_css_class("destructive-action")
        self.delete_button.connect("clicked", lambda *_: self.confirm_delete())
        self.details.append(self.delete_button)
        self.content.set_sidebar(Gtk.ScrolledWindow(child=self.details, hscrollbar_policy=Gtk.PolicyType.NEVER))
        self.body.set_end_child(self.content)
        self.append(self.body)
        self.refresh()

    def query(self, offset=0):
        return {"platform": PLATFORMS[self.platform_filter.get_selected()][0],
            "state": STATES[self.state_filter.get_selected()][0], "search": self.search_entry.get_text(),
            "sort": ("priority", "deadline", "updated")[self.sort_filter.get_selected()],
            "offset": offset, "limit": 100, "includeUnselected": self.history_switch.get_active()}

    def refresh(self):
        if self.disposed:
            return
        self.generation += 1
        self._request_page(self.generation, 0)

    def load_more(self):
        if not self.loading and self.page and self.task_model.get_n_items() < self.page["filteredTotal"]:
            self._request_page(self.generation, self.task_model.get_n_items())

    def _request_page(self, generation, offset):
        self.loading = True
        self.load_more_button.set_sensitive(False)
        def success(page):
            if self.disposed or generation != self.generation:
                return
            if offset and (self.page is None or page["snapshotVersion"] != self.page["snapshotVersion"]):
                self.refresh()
                return
            self.loading = False
            self.error_label.set_visible(False)
            selected = self.selection.get_selected_item()
            selected_id = selected.view["task"]["id"] if selected else None
            if offset == 0:
                self.task_model.remove_all()
            self.page = page
            for view in page["items"]:
                self.task_model.append(TaskRow(view))
            if selected_id:
                for index in range(self.task_model.get_n_items()):
                    if self.task_model.get_item(index).view["task"]["id"] == selected_id:
                        self.selection.set_selected(index)
                        break
            counts = page["summary"]
            self.summary_label.set_text(f"待办 {counts['pending']}    24 小时内到期 {counts['dueSoon']}    逾期 {counts['overdue']}    已完成 {counts['completed']}")
            self.total_label.set_text(f"当前范围 {page['total']} 条 · 匹配 {page['filteredTotal']} 条 · 已显示 {self.task_model.get_n_items()} 条 · 隐藏历史 {page['hiddenTotal']} 条")
            self.sources_label.set_text("\n".join(f"{name}  {page['platformCounts'].get(key, 0)}" for key, name in PLATFORMS[1:] if page['platformCounts'].get(key, 0)))
            empty = self.task_model.get_n_items() == 0
            self.empty_label.set_visible(empty)
            self.empty_label.set_text("没有匹配的任务，请调整搜索或筛选。" if page["total"] else
                "未选课程的任务已保留，勾选“查看未选课程的历史”即可查看。" if page["hiddenTotal"] else
                "还没有任务。可以新增手动任务，或连接平台后确认课程。")
            self.load_more_button.set_visible(self.task_model.get_n_items() < page["filteredTotal"])
            self.load_more_button.set_sensitive(True)
        def failure(message):
            if generation == self.generation and not self.disposed:
                self.loading = False
                self.show_error(message)
                self.load_more_button.set_sensitive(True)
        on_result(self.bridge.request_async("snapshot", self.query(offset)), success, failure)

    def show_error(self, message):
        self.error_label.set_text(message)
        self.error_label.set_visible(True)

    def setup_row(self, _, item):
        box = margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6))
        box.title = label("", "heading")
        box.subtitle = label("", "dim-label")
        box.status = label()
        for widget in (box.title, box.subtitle, box.status):
            box.append(widget)
        item.set_child(box)

    def bind_row(self, _, item):
        box, view = item.get_child(), item.get_item().view
        task = view["task"]
        box.title.set_text(task["title"])
        box.subtitle.set_text(f"{dict(PLATFORMS).get(task['platformId'], task['platformId'])} · {task['courseName'] or '未分类课程'}")
        box.status.set_text(f"P{view['priority']} · {view['stateLabel']} · {view['deadlineLabel']}")

    def show_selection(self, *_):
        self.details_generation += 1
        generation = self.details_generation
        selected = self.selection.get_selected_item()
        self.selected_detail = None
        for button in (self.edit_button, self.open_button, self.state_button, self.delete_button):
            button.set_sensitive(False)
        self.state_action.set_sensitive(False)
        if not selected:
            self.detail_title.set_text("选择一个任务")
            self.detail_body.set_text("查看时间、备注和任务状态。")
            if self.content.get_collapsed():
                self.content.set_show_sidebar(False)
            return
        def success(detail):
            if generation != self.details_generation or self.disposed:
                return
            self.selected_detail = detail
            self.content.set_show_sidebar(True)
            for button in (self.edit_button, self.open_button, self.state_button, self.delete_button):
                button.set_sensitive(True)
            view, task = detail["view"], detail["view"]["task"]
            self.state_action.set_selected([state for state, _ in TASK_STATES].index(task["state"]))
            self.state_action.set_sensitive(True)
            self.detail_title.set_text(task["title"])
            self.detail_body.set_text(format_task_detail(detail))
        on_result(self.bridge.request_async("get-task", {"id": selected.view["task"]["id"]}), success, self.show_error)

    def edit_selected(self):
        if self.selected_detail:
            from .task_editor import TaskEditor
            TaskEditor(self.parent, self.bridge, self.selected_detail["view"]["task"]["id"]).present()

    def open_selected(self):
        if not self.selected_detail:
            return
        self.open_button.set_sensitive(False)
        def opened(value):
            self.open_button.set_sensitive(True)
            try:
                Gio.AppInfo.launch_default_for_uri(value["address"], None)
            except Exception:
                self.show_error("系统浏览器未能打开任务，请检查默认浏览器后重试。")
        def failure(message):
            self.open_button.set_sensitive(True)
            self.show_error(message)
        on_result(self.bridge.request_async("resolve-task", {"id":self.selected_detail["view"]["task"]["id"]}), opened, failure)

    def change_state(self):
        if not self.selected_detail:
            return
        task = self.selected_detail["view"]["task"]
        self.state_button.set_sensitive(False)
        def failure(message):
            self.state_button.set_sensitive(True)
            self.show_error(message)
        on_result(self.bridge.request_async("set-state", {"id": task["id"], "revision": task["revision"],
            "state": TASK_STATES[self.state_action.get_selected()][0]}), lambda _: self.refresh(), failure)

    def confirm_delete(self):
        if not self.selected_detail:
            return None
        task = dict(self.selected_detail["view"]["task"])
        dialog = Adw.MessageDialog(transient_for=self.parent, modal=True, heading="删除此任务？", body=task["title"])
        dialog.add_response("cancel", "取消")
        dialog.add_response("delete", "删除")
        dialog.set_response_appearance("delete", Adw.ResponseAppearance.DESTRUCTIVE)
        dialog.set_default_response("cancel")
        dialog.set_close_response("cancel")
        def response(_, name):
            if name == "delete":
                on_result(self.bridge.request_async("delete-task", {"id": task["id"], "revision": task["revision"]}), lambda _: self.refresh(), self.show_error)
        dialog.connect("response", response)
        dialog.present()
        return dialog

    def stop(self):
        self.disposed = True
        self.generation += 1
        self.details_generation += 1
