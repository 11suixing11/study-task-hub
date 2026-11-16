from gi.repository import Gtk, Adw
from .ui import label, margins, on_result, recovery_button

class ManualCoursesWindow(Adw.Window):
    def __init__(self,parent,bridge,platform):
        super().__init__(transient_for=parent,modal=True,title="自定义课程管理")
        self.parent,self.bridge,self.platform=parent,bridge,platform
        self.page=None;self.fields={};self.closed=False;self.generation=0
        self.set_default_size(700,650)
        toolbar=Adw.ToolbarView();toolbar.add_top_bar(Adw.HeaderBar())
        body=margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL,spacing=12))
        body.append(label("改名和调整学期会保留任务关联。归档会停止课程提醒，历史任务可在首页开启“包含未选课程”查看。"))
        add=Gtk.Button(label="添加自定义课程");add.connect("clicked",self.add_course);body.append(add)
        self.rows=Gtk.Box(orientation=Gtk.Orientation.VERTICAL,spacing=16)
        body.append(Gtk.ScrolledWindow(child=self.rows,vexpand=True,hscrollbar_policy=Gtk.PolicyType.NEVER))
        self.error=label("","error");body.append(self.error);body.append(recovery_button(self,self.error))
        toolbar.set_content(body);self.set_content(toolbar)
        self.connect("close-request",self.on_close);self.refresh()

    def refresh(self):
        self.generation+=1;generation=self.generation;bridge=self.bridge
        on_result(bridge.request_async("get-manual-courses",{"platform":self.platform}),
            lambda page:self.render(page) if not self.closed and generation==self.generation and bridge is self.bridge else None,
            lambda message:self.error.set_text(message) if not self.closed and bridge is self.bridge else None)

    def render(self,page):
        self.page=page;self.fields={}
        while child:=self.rows.get_first_child():self.rows.remove(child)
        if not page["courses"]:self.rows.append(label("尚无自定义课程，可添加后在任务编辑器直接选择。","dim-label"))
        for course in page["courses"]:
            card=Gtk.Box(orientation=Gtk.Orientation.VERTICAL,spacing=8)
            card.append(label(f"{'已归档 · ' if course['archived'] else ''}{course['name']} · {course['taskCount']} 条历史任务","heading"))
            name=Gtk.Entry(text=course["name"],max_length=200)
            year=Gtk.SpinButton.new_with_range(1980,2100,1);year.set_value(course["semester"]["yearStart"])
            term=Gtk.DropDown.new_from_strings(["上学期 / 秋季","下学期 / 春季"]);term.set_selected(0 if course["semester"]["term"]=="autumn" else 1)
            card.append(label("课程名称"));card.append(name)
            period=Gtk.Box(spacing=8);period.append(label("学年起始年份"));period.append(year);period.append(term);card.append(period)
            actions=Gtk.Box(spacing=8)
            save=Gtk.Button(label="保存名称与学期");archive=Gtk.Button(label="恢复课程" if course["archived"] else "归档课程")
            remove=Gtk.Button(label="移除空课程",sensitive=course["taskCount"]==0)
            self.fields[course["key"]]={"name":name,"year":year,"term":term,"save":save,"archive":archive}
            save.connect("clicked",lambda _,c=course:self.edit(c,c["archived"]))
            archive.connect("clicked",lambda _,c=course:self.edit(c,not c["archived"]))
            remove.connect("clicked",lambda _,c=course:self.confirm_remove(c))
            for button in (save,archive,remove):actions.append(button)
            card.append(actions);self.rows.append(card);self.rows.append(Gtk.Separator())

    def edit(self,course,archived):
        fields=self.fields[course["key"]]
        args={"platform":self.platform,"key":course["key"],"revision":self.page["revision"],"name":fields["name"].get_text(),
            "semester":{"yearStart":fields["year"].get_value_as_int(),"term":("autumn","spring")[fields["term"].get_selected()]},"archived":archived}
        self.mutate("edit-manual-course",args)

    def confirm_remove(self,course):
        dialog=Adw.MessageDialog(transient_for=self,modal=True,heading="移除空课程？",body=f"将移除“{course['name']}”及其选课记录。该课程没有历史任务。")
        dialog.add_response("cancel","取消");dialog.add_response("remove","移除课程")
        dialog.set_response_appearance("remove",Adw.ResponseAppearance.DESTRUCTIVE);dialog.set_close_response("cancel")
        revision=self.page["revision"]
        dialog.connect("response",lambda _,response:self.mutate("remove-manual-course",{"platform":self.platform,"key":course["key"],"revision":revision}) if response=="remove" else None)
        dialog.present();return dialog

    def mutate(self,command,args):
        bridge=self.bridge;self.rows.set_sensitive(False)
        def saved(page):
            if self.closed or bridge is not self.bridge:return
            self.rows.set_sensitive(True);self.error.set_text("");self.render(page);self.parent.dashboard.refresh()
        def failed(message):
            if self.closed or bridge is not self.bridge:return
            self.rows.set_sensitive(True);self.error.set_text(message)
        on_result(bridge.request_async(command,args),saved,failed)

    def add_course(self,*_):
        from .course_picker import CoursePicker
        def saved(_):self.refresh();self.parent.dashboard.refresh()
        CoursePicker(self,self.bridge,self.platform,saved=saved).present()

    def rebind_bridge(self,bridge):
        if self.closed:return
        self.bridge=bridge;self.rows.set_sensitive(True);self.refresh()

    def on_close(self,*_):self.closed=True;return False
