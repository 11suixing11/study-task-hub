import unittest,concurrent.futures,datetime,sqlite3
from pathlib import Path
from linux_ui_helpers import TestAppFixture,run_until

class NotificationChecks(unittest.TestCase):
    def test_test_notification_failure_is_saved_and_visible(self):
        from study_task_hub.notification_window import NotificationWindow
        from study_task_hub.notifications import NotificationOutcome
        with TestAppFixture() as fx:
            class OfflineDesktop:
                def send(self,item):
                    future=concurrent.futures.Future();future.set_result(NotificationOutcome(False,"NOTIFY003","桌面不可用"));return future
            fx.application.notifier=OfflineDesktop()
            window=NotificationWindow(fx.window,fx.bridge);window.present()
            fx.wait(lambda:window.page is not None)
            window.test_button.emit("clicked")
            fx.wait(lambda:fx.request("get-notification-status")["test"] is not None)
            fx.wait(lambda:"NOTIFY003" in window.status.get_text())
            self.assertFalse(fx.request("get-notification-status")["test"]["accepted"])
            self.assertEqual(fx.request("snapshot")["total"],0)
            window.destroy()
    def test_scheduler_waits_for_desktop_result_before_acknowledging(self):
        from study_task_hub.scheduler import AppScheduler
        with TestAppFixture() as fx:
            fx.request("create-task",{"title":"未完成发送","platformId":"pta","courseName":"","deadlineKind":"known","deadlineIso":(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(hours=10)).isoformat(),"scoreText":"","notes":""})
            pending=[]
            def send(item):
                future=concurrent.futures.Future();pending.append(future);return future
            scheduler=AppScheduler(fx.bridge,send,clock=lambda:0)
            def states():
                with sqlite3.connect(Path(fx.temp.name)/"tasks.db") as connection:return connection.execute("SELECT state FROM reminders WHERE attempts>0").fetchall()
            try:
                scheduler.tick();fx.wait(lambda:len(pending)>0)
                fx.wait(lambda:len(states())>0)
                self.assertEqual(states(),[("processing",)],"Unfinished notification was already acknowledged")
                pending[0].set_result(False);fx.wait(lambda:not scheduler.notifications_busy)
                self.assertEqual(states(),[("scheduled",)])
            finally:scheduler.stop()

    def test_stop_releases_pending_delivery_and_ignores_late_ack(self):
        from study_task_hub.scheduler import AppScheduler
        with TestAppFixture() as fx:
            fx.request("create-task",{"title":"停止后的回执","platformId":"pta","courseName":"","deadlineKind":"known","deadlineIso":(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(hours=10)).isoformat(),"scoreText":"","notes":""})
            pending=concurrent.futures.Future();sent=[]
            scheduler=AppScheduler(fx.bridge,lambda item:sent.append(item) or pending,clock=lambda:0)
            scheduler.tick();fx.wait(lambda:len(sent)==1);scheduler.stop()
            def state():
                with sqlite3.connect(Path(fx.temp.name)/"tasks.db") as connection:return connection.execute("SELECT state FROM reminders WHERE id=?",(sent[0]["id"],)).fetchone()[0]
            fx.wait(lambda:state()=="scheduled")
            pending.set_result(True);fx.wait(lambda:not scheduler.notifications_busy)
            self.assertEqual(state(),"scheduled")

    def test_private_desktop_service_accepts_or_rejects_requests(self):
        from study_task_hub.notifications import DesktopNotifier
        from gi.repository import Gio,GLib
        import subprocess
        bus=subprocess.Popen(["dbus-daemon","--session","--nofork","--print-address=1"],stdout=subprocess.PIPE,text=True)
        address=bus.stdout.readline().strip()
        flags=Gio.DBusConnectionFlags.AUTHENTICATION_CLIENT|Gio.DBusConnectionFlags.MESSAGE_BUS_CONNECTION
        server=Gio.DBusConnection.new_for_address_sync(address,flags,None,None)
        client=Gio.DBusConnection.new_for_address_sync(address,flags,None,None)
        server.call_sync("org.freedesktop.DBus","/org/freedesktop/DBus","org.freedesktop.DBus","RequestName",GLib.Variant("(su)",("org.freedesktop.Notifications",0)),None,Gio.DBusCallFlags.NONE,1000,None)
        interface=Gio.DBusNodeInfo.new_for_xml('<node><interface name="org.freedesktop.Notifications"><method name="Notify"><arg type="s" direction="in"/><arg type="u" direction="in"/><arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="as" direction="in"/><arg type="a{sv}" direction="in"/><arg type="i" direction="in"/><arg type="u" direction="out"/></method></interface></node>').interfaces[0]
        reject=[False];received=[]
        def method(connection,sender,path,iface,name,params,invocation):
            received.append(params.unpack())
            if reject[0]:invocation.return_dbus_error("org.freedesktop.Notifications.Rejected","test rejection")
            else:invocation.return_value(GLib.Variant("(u)",(23,)))
        registration=server.register_object("/org/freedesktop/Notifications",interface,method,None,None)
        try:
            notifier=DesktopNotifier(client)
            result=notifier.send({"id":"test","title":"测试提醒","body":"本地测试"});run_until(result.done)
            self.assertTrue(result.result().accepted);self.assertEqual(result.result().desktop_id,23)
            self.assertEqual(received[0][3],"测试提醒")
            reject[0]=True
            result=notifier.send({"id":"test","title":"拒绝测试","body":"本地测试"});run_until(result.done)
            self.assertFalse(result.result().accepted);self.assertEqual(result.result().error_code,"NOTIFY004")
            server.call_sync("org.freedesktop.DBus","/org/freedesktop/DBus","org.freedesktop.DBus","ReleaseName",GLib.Variant("(s)",("org.freedesktop.Notifications",)),None,Gio.DBusCallFlags.NONE,1000,None)
            result=notifier.send({"id":"test","title":"服务不可用","body":"本地测试"});run_until(result.done)
            self.assertFalse(result.result().accepted);self.assertEqual(result.result().error_code,"NOTIFY003")
        finally:
            server.unregister_object(registration);client.close_sync(None);server.close_sync(None);bus.terminate();bus.wait(timeout=5);bus.stdout.close()

if __name__=="__main__":unittest.main()
