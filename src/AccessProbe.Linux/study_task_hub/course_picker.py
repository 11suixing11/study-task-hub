import uuid
from gi.repository import Gtk, Adw
from .ui import label, margins, on_result, recovery_button

GROUPS = [("current", "本学期优先"), ("selected", "保留你的选择"), ("review", "待审查"),
    ("manual", "自定义课程"), ("history", "其他学期与历史课程")]

class CoursePicker(Adw.Window):
    def __init__(self, parent, bridge, platform, saved=None):
        super().__init__(transient_for=parent, modal=True, title="按学期选择课程")
        self.bridge, self.platform, self.saved = bridge, platform, saved
        self.set_default_size(760, 760)
        self.set_size_request(580, 560)
        self.plan = self.recording = self.semester = None
        self.years = []
        self.choices, self.search_rows, self.group_rows = {}, {}, {}
        self.custom_drafts, self.draft_choices = {}, {}
        self.closed = self.loading = self.configuring = False
        self.generation = 0
        self.connect("close-request", self.on_close)
        toolbar = Adw.ToolbarView()
        toolbar.add_top_bar(Adw.HeaderBar())
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        body.append(margins(label("先按本科常用学期预勾选，再审查确认。上学期为 9 月至次年 1 月，下学期为 2–8 月；可切换学年和学期。")))
        controls = margins(Gtk.Box(spacing=8))
        self.year_filter = Gtk.DropDown.new_from_strings([])
        self.term_filter = Gtk.DropDown.new_from_strings(["上学期 / 秋季", "下学期 / 春季"])
        self.year_filter.connect("notify::selected", self.change_semester)
        self.term_filter.connect("notify::selected", self.change_semester)
        controls.append(self.year_filter)
        controls.append(self.term_filter)
        reload_button = Gtk.Button(label="重新加载课程")
        reload_button.connect("clicked", lambda *_: self.reload())
        controls.append(reload_button)
        body.append(controls)
        self.search_entry = Gtk.SearchEntry(placeholder_text="搜索课程名称、说明或推荐理由")
        self.search_entry.connect("search-changed", lambda *_: self.apply_search())
        body.append(margins(self.search_entry))
        actions = margins(Gtk.Box(spacing=8))
        recommended = Gtk.Button(label="采用本学期建议")
        recommended.connect("clicked", lambda *_: self.apply_recommendations())
        actions.append(recommended)
        clear = Gtk.Button(label="取消全部勾选")
        clear.connect("clicked", lambda *_: [choice.set_active(False) for choice in self.choices.values()])
        actions.append(clear)
        self.summary = label("正在生成选课建议…", "heading")
        actions.append(self.summary)
        body.append(actions)
        self.catalog_label = margins(label("", "dim-label"))
        body.append(self.catalog_label)
        self.rows = margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14))
        body.append(Gtk.ScrolledWindow(child=self.rows, vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER))
        custom_box = margins(Gtk.Box(spacing=8))
        self.custom_name_entry = Gtk.Entry(placeholder_text="补充未列出的手动课程", hexpand=True, max_length=200)
        self.add_custom_button = Gtk.Button(label="添加课程")
        self.add_custom_button.connect("clicked", lambda *_: self.add_custom())
        custom_box.append(self.custom_name_entry)
        custom_box.append(self.add_custom_button)
        body.append(custom_box)
        self.error_label = margins(label("", "error"))
        body.append(self.error_label)
        body.append(margins(recovery_button(self, self.error_label)))
        footer = margins(Gtk.Box(spacing=8))
        cancel = Gtk.Button(label="取消", hexpand=True)
        cancel.connect("clicked", lambda *_: self.close())
        self.save_button = Gtk.Button(label="确认选择并采集", sensitive=False, hexpand=True)
        self.save_button.add_css_class("suggested-action")
        self.save_button.connect("clicked", lambda *_: self.save())
        footer.append(cancel)
        footer.append(self.save_button)
        body.append(footer)
        toolbar.set_content(body)
        self.set_content(toolbar)
        self.reload()

    def period_key(self):
        return (self.semester["yearStart"], self.semester["term"]) if self.semester else None

    def remember_draft(self):
        if self.semester:
            self.draft_choices[self.period_key()] = {key: choice.get_active() for key, choice in self.choices.items()}

    def change_semester(self, *_):
        if self.configuring or not self.semester or self.year_filter.get_selected() >= len(self.years):
            return
        self.remember_draft()
        semester = {"yearStart": self.years[self.year_filter.get_selected()], "term": ("autumn", "spring")[self.term_filter.get_selected()]}
        self.request_plan(semester)

    def reload(self):
        if self.closed:
            return
        self.remember_draft()
        self.request_plan(self.semester)

    def request_plan(self, semester):
        self.generation += 1
        generation, requested_bridge = self.generation, self.bridge
        self.loading = True
        self.rows.set_sensitive(False)
        self.save_button.set_sensitive(False)
        args = {"platform": self.platform}
        if semester:
            args["semester"] = {"yearStart": semester["yearStart"], "term": semester["term"]}
        def loaded(plan):
            if self.closed or generation != self.generation or requested_bridge is not self.bridge:
                return
            self.loading = False
            self.rows.set_sensitive(True)
            self.error_label.set_text("")
            self.plan = self.recording = plan
            self.semester = plan["semester"]
            self.configuring = True
            year = self.semester["yearStart"]
            self.years = list(range(max(1980,year-4),min(2100,year+3)+1))
            self.year_filter.set_model(Gtk.StringList.new([f"{y}-{y+1}学年" for y in self.years]))
            self.year_filter.set_selected(self.years.index(year))
            self.term_filter.set_selected(0 if self.semester["term"] == "autumn" else 1)
            self.configuring = False
            self.catalog_label.set_text(plan["catalogMessage"] + ("" if plan["catalogComplete"] else " · 目录尚未完整确认，请审查"))
            self.render()
            self.save_button.set_sensitive(True)
        def failed(message):
            if not self.closed and generation == self.generation and requested_bridge is self.bridge:
                self.loading = False
                self.rows.set_sensitive(True)
                self.error_label.set_text(message)
        on_result(requested_bridge.request_async("get-course-plan", args), loaded, failed)

    def render(self):
        if not self.plan:
            return
        while child := self.rows.get_first_child():
            self.rows.remove(child)
        self.choices, self.search_rows, self.group_rows = {}, {}, {}
        overrides = self.draft_choices.get(self.period_key(), {})
        groups = {key: [] for key, _ in GROUPS}
        for suggestion in self.plan["courses"]:
            course = suggestion["course"]
            groups[suggestion["group"]].append((course["key"], course["name"], course["detail"], suggestion["reason"], overrides.get(course["key"], suggestion["preselected"])))
        for draft in self.custom_drafts.get(self.period_key(), []):
            groups["manual"].append((draft["key"], draft["name"], "自定义手动课程", "审查确认后保存，可用于手动任务", overrides.get(draft["key"], True)))
        for group, title in GROUPS:
            entries = groups[group]
            if not entries:
                continue
            box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
            if group == "history":
                container = Gtk.Expander(label=f"{title} · {len(entries)} 门", child=box)
            else:
                container = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
                container.append(label(f"{title} · {len(entries)} 门", "title-3"))
                container.append(box)
            self.group_rows[group] = (container, [])
            self.rows.append(container)
            for key, name, detail, reason, checked in entries:
                row = margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4), 8)
                choice = Gtk.CheckButton(child=label(name, "heading"))
                choice.set_active(checked)
                choice.connect("toggled", lambda *_: self.update_summary())
                row.append(choice)
                row.append(label(detail + " · " + reason, "dim-label"))
                box.append(row)
                self.choices[key] = choice
                self.search_rows[key] = (row, (name+" "+detail+" "+reason).casefold())
                self.group_rows[group][1].append(key)
        if not self.choices:
            self.rows.append(label("尚未读取到课程。可先添加手动课程，或完成登录并刷新课程目录。"))
        self.update_summary()
        self.apply_search()

    def apply_search(self):
        query = self.search_entry.get_text().strip().casefold()
        for row, text in self.search_rows.values():
            row.set_visible(not query or query in text)
        for container, keys in self.group_rows.values():
            container.set_visible(any(self.search_rows[key][0].get_visible() for key in keys))
            if isinstance(container, Gtk.Expander):
                container.set_expanded(bool(query))

    def update_summary(self):
        self.summary.set_text(f"已勾选 {sum(choice.get_active() for choice in self.choices.values())} / {len(self.choices)} 门")

    def apply_recommendations(self):
        if self.plan:
            self.draft_choices.pop(self.period_key(), None)
            self.render()

    def add_custom(self):
        if not self.semester or self.loading:
            return
        name = self.custom_name_entry.get_text().strip()
        names = [suggestion["course"]["name"] for suggestion in self.plan["courses"] if suggestion["course"]["detail"].startswith("自定义 · ")]
        names.extend(draft["name"] for draft in self.custom_drafts.get(self.period_key(), []))
        if not name or len(name) > 200 or name.casefold() in [value.casefold() for value in names]:
            self.error_label.set_text("课程名称不能为空、超过 200 字或与手动课程重复。")
            return
        self.remember_draft()
        self.custom_drafts.setdefault(self.period_key(), []).append({"key":"draft:"+uuid.uuid4().hex, "name":name})
        self.custom_name_entry.set_text("")
        self.error_label.set_text("")
        self.search_entry.set_text("")
        self.render()

    def save(self):
        if self.closed or self.loading or not self.plan:
            return
        self.save_button.set_sensitive(False)
        requested_bridge, generation = self.bridge, self.generation
        payload = {"platform":self.platform, "revision":self.plan["revision"], "semester":{"yearStart":self.semester["yearStart"], "term":self.semester["term"]},
            "keys":[key for key,choice in self.choices.items() if not key.startswith("draft:") and choice.get_active()],
            "reviewedKeys":[suggestion["course"]["key"] for suggestion in self.plan["courses"]],
            "customCourses":[{"name":draft["name"],"selected":self.choices[draft["key"]].get_active()} for draft in self.custom_drafts.get(self.period_key(), [])]}
        def success(recording):
            if self.closed or requested_bridge is not self.bridge or generation != self.generation:
                return
            if self.saved:
                self.saved(recording)
            self.close()
        def failure(message):
            if not self.closed and requested_bridge is self.bridge and generation == self.generation:
                self.error_label.set_text(message)
                self.save_button.set_sensitive(True)
        on_result(requested_bridge.request_async("save-course-selection", payload), success, failure)

    def rebind_bridge(self, bridge):
        self.bridge = bridge
        self.reload()

    def on_close(self, *_):
        self.closed = True
        self.generation += 1
        return False
