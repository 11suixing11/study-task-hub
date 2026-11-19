from concurrent.futures import Future
from dataclasses import dataclass
from gi.repository import Gio,GLib

@dataclass(frozen=True)
class NotificationOutcome:
    accepted:bool
    error_code:str|None=None
    message:str=""
    desktop_id:int|None=None

class DesktopNotifier:
    def __init__(self,connection=None):self.connection=connection

    def send(self,item):
        future=Future()
        def send_on(connection):
            args=GLib.Variant("(susssasa{sv}i)",("聚合平台",0,"io.github.studyTaskHub",item["title"],GLib.markup_escape_text(item["body"]),[],
                {"desktop-entry":GLib.Variant("s","io.github.studyTaskHub"),"urgency":GLib.Variant("y",1)},-1))
            def finished(conn,result):
                try:
                    identifier=conn.call_finish(result).unpack()[0]
                    outcome=NotificationOutcome(True,message="桌面通知服务已接收；实际显示请核对系统通知设置。",desktop_id=identifier)
                except GLib.Error as error:
                    remote=Gio.DBusError.get_remote_error(error) or ""
                    unavailable=remote in ("org.freedesktop.DBus.Error.ServiceUnknown","org.freedesktop.DBus.Error.NameHasNoOwner","org.freedesktop.DBus.Error.Disconnected")
                    outcome=NotificationOutcome(False,"NOTIFY003" if unavailable else "NOTIFY004",
                        "桌面通知服务不可用，请恢复桌面会话后重试。" if unavailable else "桌面通知服务拒绝请求或响应超时，请检查系统通知设置后重试。")
                if not future.done():future.set_result(outcome)
            connection.call("org.freedesktop.Notifications","/org/freedesktop/Notifications","org.freedesktop.Notifications","Notify",args,
                GLib.VariantType.new("(u)"),Gio.DBusCallFlags.NONE,5000,None,finished)
        if self.connection:send_on(self.connection)
        else:
            def connected(_,result):
                try:self.connection=Gio.bus_get_finish(result);send_on(self.connection)
                except GLib.Error:future.set_result(NotificationOutcome(False,"NOTIFY003","无法连接桌面通知服务，请恢复桌面会话后重试。"))
            Gio.bus_get(Gio.BusType.SESSION,None,connected)
        return future
