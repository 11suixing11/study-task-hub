from pathlib import Path
import datetime,time
from gi.repository import Gtk, Adw, GLib
from .ui import label,margins,on_result,recovery_button
from .task_details import display_date

class BackupWindow(Adw.Window):
    def __init__(self,parent,bridge):
        super().__init__(transient_for=parent,modal=True,title="备份与恢复")
        self.parent,self.bridge=parent,bridge;self.preview=None;self.restored=None;self.closed=False;self.busy=False;self.restoring=False
        self.set_default_size(700,520)
        toolbar=Adw.ToolbarView();toolbar.add_top_bar(Adw.HeaderBar())
        body=margins(Gtk.Box(orientation=Gtk.Orientation.VERTICAL,spacing=12))
        body.append(label("备份包含任务、课程、设置和已保存登录，使用密码加密。请妥善保管密码；恢复会替换全部本地数据，并先保存回退副本。"))
        body.append(label("备份文件"))
        self.path_entry=Gtk.Entry(text=str(Path(parent.get_application().data_root)/("backup-"+datetime.datetime.now().strftime("%Y%m%d-%H%M%S")+".studybackup")))
        body.append(self.path_entry)
        browse=Gtk.Box(spacing=8)
        for title,save in [("选择保存位置",True),("选择已有备份",False)]:
            button=Gtk.Button(label=title);button.connect("clicked",lambda _,s=save:self.choose_file(s));browse.append(button)
        body.append(browse);body.append(label("密码（至少 8 字符，不会保存）"))
        self.password_entry=Gtk.Entry(visibility=False,max_length=1024,input_purpose=Gtk.InputPurpose.PASSWORD)
        body.append(self.password_entry)
        self.preview_label=label("先检查已有备份的内容，再确认恢复。","dim-label");body.append(self.preview_label)
        self.status=label("");body.append(self.status);body.append(recovery_button(self,self.status))
        actions=Gtk.Box(spacing=8)
        self.create_button=Gtk.Button(label="创建加密备份");self.inspect_button=Gtk.Button(label="检查备份内容");self.restore_button=Gtk.Button(label="恢复已检查的备份",sensitive=False)
        for button in (self.create_button,self.inspect_button,self.restore_button):actions.append(button)
        self.create_button.connect("clicked",lambda *_:self.check("create-backup"))
        self.inspect_button.connect("clicked",lambda *_:self.check("inspect-backup"))
        self.restore_button.connect("clicked",lambda *_:self.confirm_restore())
        self.path_entry.connect("changed",self.invalidate);self.password_entry.connect("changed",self.invalidate)
        body.append(actions);toolbar.set_content(body);self.set_content(toolbar);self.connect("close-request",self.on_close)

    def invalidate(self,*_):self.preview=None;self.restore_button.set_sensitive(False)

    def sensitive(self,enabled):
        for widget in (self.path_entry,self.password_entry,self.create_button,self.inspect_button):widget.set_sensitive(enabled)
        self.restore_button.set_sensitive(enabled and self.preview is not None)

    def check(self,command):
        if self.busy:return
        bridge=self.bridge;args={"path":self.path_entry.get_text().strip(),"password":self.password_entry.get_text()}
        self.busy=True;self.sensitive(False);self.status.set_text("正在加密备份…" if command=="create-backup" else "正在校验密码、文件和数据库…")
        def checked(preview):
            if self.closed or bridge is not self.bridge:return
            self.busy=False;self.preview=preview;self.sensitive(True)
            self.preview_label.set_text(f"创建时间：{display_date(preview['createdAtUtc'])}\n任务：{preview['taskCount']} 条 · 已保存会话：{preview['sessionCount']} 个")
            self.status.set_text("备份已创建。" if command=="create-backup" else "校验通过，可确认恢复。")
        def failed(message):
            if self.closed or bridge is not self.bridge:return
            self.busy=False;self.preview=None;self.sensitive(True);self.status.set_text(message)
        on_result(bridge.request_async(command,args),checked,failed)

    def confirm_restore(self):
        if self.preview is None or self.busy:return
        preview=self.preview;args={"path":preview["path"],"password":self.password_entry.get_text(),"fingerprint":preview["fingerprint"]}
        dialog=Adw.MessageDialog(transient_for=self,modal=True,heading="用备份替换全部本地数据？",
            body=f"备份创建于 {display_date(preview['createdAtUtc'])}，包含 {preview['taskCount']} 条任务和 {preview['sessionCount']} 个会话。将关闭其他编辑和登录窗口，停止同步，并保存当前数据的回退副本。浏览器缓存将移到回退目录。")
        dialog.add_response("cancel","取消");dialog.add_response("restore","确认恢复")
        dialog.set_response_appearance("restore",Adw.ResponseAppearance.DESTRUCTIVE);dialog.set_close_response("cancel")
        dialog.connect("response",lambda _,response:self.restore(args) if response=="restore" else None);dialog.present();return dialog

    def restore(self,args):
        self.busy=self.restoring=True;self.sensitive(False);self.status.set_text("正在停止读取并保存回退数据…")
        app=self.parent.get_application();self.previous_scheduler=app.scheduler
        if app.scheduler:app.scheduler.stop()
        self.parent.set_sensitive(False)
        windows=Gtk.Window.get_toplevels();logins=[]
        for index in range(windows.get_n_items()):
            window=windows.get_item(index);ancestor=window.get_transient_for()
            while ancestor and ancestor!=self.parent:ancestor=ancestor.get_transient_for()
            if ancestor!=self.parent or window==self:continue
            if hasattr(window,"close_with_save") and not window.closed:window.close_with_save();logins.append(window)
            else:window.close()
        deadline=time.monotonic()+5;bridge=self.bridge
        def ready():
            active=[w for w in logins if not w.closed]
            if active and time.monotonic()<deadline:return GLib.SOURCE_CONTINUE
            for login in active:login.finish_close("保存等待已超时；原会话保留。")
            on_result(bridge.request_async("restore-backup",args),lambda result:self.finish_restore(result,None),lambda message:self.finish_restore(None,message))
            return GLib.SOURCE_REMOVE
        GLib.timeout_add(50,ready)

    def finish_restore(self,result,message):
        self.busy=self.restoring=False;self.parent.set_sensitive(True);self.preview=None;self.sensitive(True)
        app=self.parent.get_application()
        if self.previous_scheduler:
            from .scheduler import AppScheduler
            app.scheduler=AppScheduler(self.bridge,app.notify,busy=app.has_open_dialog)
        if result:
            self.restored=result;self.password_entry.set_text("")
            self.status.set_text("恢复完成。\n回退备份："+result["rollbackBackupPath"]+"\n原浏览器环境："+result["rollbackDirectory"])
            self.parent.dashboard.refresh()
        else:self.status.set_text(message)

    def choose_file(self,save):
        chooser=Gtk.FileChooserNative.new("保存加密备份" if save else "选择加密备份",self,Gtk.FileChooserAction.SAVE if save else Gtk.FileChooserAction.OPEN,"选择","取消")
        if save:chooser.set_current_name(Path(self.path_entry.get_text()).name)
        def response(dialog,result):
            if result==Gtk.ResponseType.ACCEPT and dialog.get_file():self.path_entry.set_text(dialog.get_file().get_path())
            dialog.destroy()
        chooser.connect("response",response);chooser.show()

    def rebind_bridge(self,bridge):
        if self.closed or self.restoring:return
        self.bridge=bridge;self.busy=False;self.invalidate();self.sensitive(True)

    def on_close(self,*_):
        if self.restoring:return True
        self.closed=True;self.password_entry.set_text("");return False
