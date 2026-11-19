from gi.repository import Gtk, Adw, GLib
from .ui import label, margins, on_result, recovery_button
from .task_details import display_date

OUTCOMES={"completed":"已完整读取","partial":"部分读取，需核对","failed":"读取失败","cancelled":"已停止","login_required":"需要重新登录"}
METRICS={"Courses":"发现课程","SelectedCourses":"已选官方课程","CoursesRead":"已读课程","Tasks":"任务","Added":"新增","Updated":"更新","TasksWithDeadline":"含截止时间","TasksWithLink":"含官方入口","StatusNeedsReview":"状态待核对","Requests":"请求数","CatalogPages":"目录页数","DuplicateRows":"重复记录","FullTaskListsCovered":"任务列表覆盖","LearningUnitListsCovered":"学习单元覆盖","CourseListCovered":"目录覆盖"}
METRICS.update({"DetailsSaved":"已保存详情","DetailsDeferred":"延期核对详情","DetailsUnavailable":"详情不可读取","DetailsRequireAction":"需要官网操作的详情","DetailsRequirePreview":"需要预览的详情",
    "LoginRequired":"需要重新登录","UnsupportedRows":"暂未支持的记录","UnsupportedCourses":"暂未支持的课程","ApplicationsDiscovered":"发现应用","SharedCourseReminders":"共享课提醒",
    "ReminderListCovered":"提醒列表覆盖","FlipCourses":"翻转课","SharedCourses":"共享课","SmartCourses":"智慧课程","AgentCourses":"AI 智课","SmartOtherTasks":"其他智慧课程任务",
    "AccountVerified":"账号归属已确认","RoomExamTasks":"机房考试","OfflineExamTasks":"线下考试","MakeupExamTasks":"补考","UnidentifiedExamRows":"未识别考试","TaskListCoursesRead":"已读取任务列表的课程",
    "TeachingDirectoryFallbackCourses":"教学目录补充课程","LearningUnits":"学习单元","LearningUnitsWithDeadline":"含截止的学习单元","RequiredLearningUnitsIncomplete":"未完成必学单元",
    "SpocCourses":"学校专属课程","NewExamTasks":"新版考试任务"})

def report_lines(report,title):
    if report is None:return [title,"尚无读取结果"]
    lines=[title+" · "+OUTCOMES.get(report["outcome"],report["outcome"]),
        "开始："+display_date(report["startedAtUtc"]),"结束："+display_date(report["finishedAtUtc"]),
        "上次完整成功："+display_date(report.get("lastSuccessfulAtUtc")),"覆盖范围："+report["scope"],
        "本轮任务："+str(report["tasksObserved"])+" 条", "汇总"]
    lines.extend(METRICS.get(key,key)+"："+({"True":"已确认","False":"未确认"}.get(value,value)) for key,value in report["metrics"].items())
    lines.append("全部问题")
    lines.extend(f"{p['code']} · {p['course'] or '平台读取'}\n{p['message']}" for p in report["problems"])
    if not report["problems"]:lines.append("未记录问题")
    lines.append("课程覆盖与本地记录")
    for c in report["courses"]:
        lines.extend([c["name"]+" · "+c["coverage"],f"本地 {c['localTasks']} 条 · 本轮 {c['observedTasks']} 条 · {'已选择' if c['selected'] else '未选择'}",
            "上次完整成功："+display_date(c.get("lastSuccessfulAtUtc"))])
    return lines

class SyncDetailsWindow(Adw.Window):
    def __init__(self,parent,bridge,platform,retry=None):
        super().__init__(transient_for=parent,modal=True,title="同步详情")
        self.parent,self.bridge,self.platform,self.retry=parent,bridge,platform,retry
        self.page=None;self.report_text="";self.closed=False;self.generation=0
        self.set_default_size(740,720)
        toolbar=Adw.ToolbarView();toolbar.add_top_bar(Adw.HeaderBar())
        body=margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL,spacing=12))
        actions=Gtk.Box(spacing=8)
        for title,catalog in [("重新刷新课程",True),("重新采集任务",False)]:
            button=Gtk.Button(label=title);button.connect("clicked",lambda _,c=catalog:self.start(c));actions.append(button)
        body.append(actions)
        body.append(label("本地条数包含历史记录；部分读取时，没有新任务不能说明课程为空。","dim-label"))
        self.report_label=label("");self.report_label.set_selectable(True)
        body.append(Gtk.ScrolledWindow(child=self.report_label,vexpand=True,hscrollbar_policy=Gtk.PolicyType.NEVER))
        self.error=label("","error");body.append(self.error);body.append(recovery_button(self,self.error))
        toolbar.set_content(body);self.set_content(toolbar)
        self.unsubscribe=bridge.subscribe_events(lambda event:self.receive(event,bridge))
        self.connect("close-request",self.on_close);self.refresh()

    def refresh(self):
        self.generation+=1;generation=self.generation;bridge=self.bridge
        def loaded(page):
            if self.closed or bridge is not self.bridge or generation!=self.generation:return
            self.page=page;self.report_text="\n".join(report_lines(page["catalog"],"课程目录刷新")+[""]+report_lines(page["tasks"],"任务采集"))
            self.report_label.set_text(self.report_text)
        on_result(bridge.request_async("get-sync-reports",{"platform":self.platform}),loaded,
            lambda message:self.error.set_text(message) if not self.closed and bridge is self.bridge else None)

    def start(self,catalog):
        if self.retry:self.retry(self.platform,catalog);return
        on_result(self.bridge.request_async("start-sync",{"platform":self.platform,"catalogOnly":catalog}),lambda _:self.error.set_text("读取已开始，结束后更新报告。"),self.error.set_text)

    def receive(self,event,source):
        if event["event"]=="job-finished" and event["payload"]["platform"]==self.platform:
            GLib.idle_add(lambda:self.refresh() if not self.closed and self.bridge is source else None)

    def rebind_bridge(self,bridge):
        if self.closed:return
        self.unsubscribe();self.bridge=bridge
        self.unsubscribe=bridge.subscribe_events(lambda event:self.receive(event,bridge));self.refresh()

    def on_close(self,*_):self.closed=True;self.unsubscribe();return False
