from gi.repository import Gtk,Adw
from .ui import label,margins,on_result,recovery_button
from .task_details import display_date

class NotificationWindow(Adw.Window):
    def __init__(self,parent,bridge):
        super().__init__(transient_for=parent,modal=True,title="通知状态与测试")
        self.parent,self.bridge=parent,bridge;self.closed=False;self.page=None;self.testing=False
        self.set_default_size(720,650)
        toolbar=Adw.ToolbarView();toolbar.add_top_bar(Adw.HeaderBar())
        body=margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL,spacing=12))
        body.append(label("桌面服务接收请求后会记录“已接收”。是否显示由系统通知设置、免打扰模式等决定，请结合屏幕上的提醒核对。"))
        actions=Gtk.Box(spacing=8)
        self.test_button=Gtk.Button(label="发送测试提醒");self.test_button.connect("clicked",self.test_notification)
        retry=Gtk.Button(label="立即重试失败项");retry.connect("clicked",self.retry)
        refresh=Gtk.Button(label="刷新状态");refresh.connect("clicked",lambda *_:self.refresh())
        for button in (self.test_button,retry,refresh):actions.append(button)
        body.append(actions);self.status=label("");body.append(self.status);body.append(recovery_button(self,self.status))
        self.history=label("");self.history.set_selectable(True)
        body.append(Gtk.ScrolledWindow(child=self.history,vexpand=True,hscrollbar_policy=Gtk.PolicyType.NEVER))
        toolbar.set_content(body);self.set_content(toolbar);self.connect("close-request",self.on_close);self.refresh()

    def refresh(self):
        bridge=self.bridge
        def loaded(page):
            if self.closed or bridge is not self.bridge:return
            self.page=page;lines=["任务提醒："+("已启用" if page["enabled"] else "已关闭")]
            if page["test"]:
                result=page["test"];lines.append("最近测试："+("桌面已接收" if result["accepted"] else "发送失败 · "+result["errorCode"])+" · "+display_date(result["atUtc"]))
            if not page["history"]:lines.append("暂无提醒记录")
            for item in page["history"]:
                state="桌面已接收" if item["state"]=="requested" else "正在发送" if item["state"]=="processing" else "失败，等待重试" if item["errorCode"] else "等待计划时间"
                lines.extend(["",item["title"]+" · "+item["kind"],state+f" · 尝试 {item['attempts']} 次",
                    "接收时间："+display_date(item["acceptedAtUtc"])])
                if item["errorCode"]:lines.append("错误："+item["errorCode"])
            self.history.set_text("\n".join(lines))
        on_result(bridge.request_async("get-notification-status"),loaded,lambda message:self.status.set_text(message) if not self.closed and bridge is self.bridge else None)

    def test_notification(self,*_):
        if self.testing:return
        self.testing=True;self.test_button.set_sensitive(False);bridge=self.bridge
        app=self.parent.get_application()
        def received(outcome):
            if self.closed or bridge is not self.bridge:return
            args={"accepted":outcome.accepted,"errorCode":outcome.error_code}
            def saved(_):
                if self.closed or bridge is not self.bridge:return
                self.testing=False;self.test_button.set_sensitive(True)
                self.status.set_text(("桌面已接收，请检查是否显示。" if outcome.accepted else outcome.error_code+" · "+outcome.message));self.refresh()
            on_result(bridge.request_async("record-notification-test",args),saved,lambda message:self.failed(message))
        on_result(app.notify({"id":"study-task-hub-test","title":"聚合平台测试提醒","body":"这是一条测试提醒，用于核对系统通知是否显示。"}),received,lambda message:self.failed(message))

    def failed(self,message):
        if self.closed:return
        self.testing=False;self.test_button.set_sensitive(True);self.status.set_text(message)

    def retry(self,*_):
        bridge=self.bridge
        def retried(count):
            if self.closed or bridge is not self.bridge:return
            self.status.set_text(f"已安排 {count} 条失败提醒重试。")
            app=self.parent.get_application()
            if app.scheduler:app.scheduler.retry_notifications()
            self.refresh()
        on_result(bridge.request_async("retry-notifications"),retried,self.failed)

    def rebind_bridge(self,bridge):
        if self.closed:return
        self.bridge=bridge;self.testing=False;self.test_button.set_sensitive(True);self.refresh()

    def on_close(self,*_):self.closed=True;return False
