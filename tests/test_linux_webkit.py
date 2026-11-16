import unittest
import importlib.util
import http.server
import threading
import tempfile
import ssl
import subprocess
import concurrent.futures
import gi
gi.require_version("WebKit", "6.0")
from gi.repository import WebKit, Gtk
from linux_ui_helpers import TestAppFixture, run_until

class Handler(http.server.BaseHTTPRequestHandler):
    paths=[]
    def do_GET(self):
        self.paths.append(self.path)
        body=b"<html><title>Local login fixture</title><body>Fixture only"+(b"<iframe src='/frame'></iframe>" if self.path=="/" else b"")+b"</body></html>"
        self.send_response(200)
        self.send_header("Content-Type", "text/html")
        self.send_header("Set-Cookie", "fixture=private; HttpOnly; SameSite=Strict; Path=/")
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *_):
        pass

class WebKitChecks(unittest.TestCase):
    def test_default_navigation_policy_uses_recovered_backend(self):
        from study_task_hub.login_window import LoginWindow
        from study_task_hub.bridge import BackendBridge
        with TestAppFixture() as fx:
            class DelayedInitialRestore:
                def request_async(self,command,args=None):
                    if command=="get-session":return concurrent.futures.Future()
                    return fx.bridge.request_async(command,args)
            login=LoginWindow(fx.window,DelayedInitialRestore(),{"id":"chaoxing","name":"Policy fixture","loginUri":"https://i.chaoxing.com/"})
            login.present()
            fx.bridge.close()
            recovered=BackendBridge(fx.application.backend_path,fx.temp.name)
            try:
                login.bridge=recovered
                policy=login.policy_request("https://i.chaoxing.com/")
                self.assertIsNone(policy.exception(timeout=5),"Navigation policy kept the disconnected backend")
                self.assertTrue(policy.result()["allowed"])
            finally:
                login.finish_close();recovered.close()
    def test_closed_login_discards_delayed_cookie_fingerprint(self):
        from study_task_hub.login_window import LoginWindow
        from gi.repository import GLib
        import time
        server=http.server.ThreadingHTTPServer(("127.0.0.1",0),Handler)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        address=f"http://127.0.0.1:{server.server_port}/"
        def policy(uri,frame=False):
            future=concurrent.futures.Future();future.set_result({"allowed":uri.startswith(address),"upgrade":None,"metadata":False});return future
        try:
            with TestAppFixture() as fx:
                requests=[];pending=[]
                class DelayedBinding:
                    def request_async(self,command,args=None):
                        requests.append(command)
                        actual=fx.bridge.request_async(command,args)
                        if command=="cookie-fingerprint":
                            held=concurrent.futures.Future();pending.append((held,actual));return held
                        return actual
                login=LoginWindow(fx.window,DelayedBinding(),{"id":"chaoxing","name":"Delayed fixture","loginUri":address},policy_request=policy)
                login.present();run_until(lambda:login.web.get_title()=="Local login fixture",20)
                result=login.capture_session()
                run_until(lambda:len(pending)==1)
                login.finish_close("fixture timeout")
                pending[0][0].set_result(pending[0][1].result(timeout=5))
                until=time.monotonic()+0.15;run_until(lambda:time.monotonic()>until)
                self.assertEqual(requests.count("capture-session"),0,"Closed login submitted a stale credential snapshot")
                self.assertTrue(result.done(),"Closed capture was left pending")
        finally:
            server.shutdown();server.server_close();thread.join()

    def test_close_reuses_inflight_capture_instead_of_capturing_twice(self):
        from study_task_hub.login_window import LoginWindow
        import time
        server=http.server.ThreadingHTTPServer(("127.0.0.1",0),Handler)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        address=f"http://127.0.0.1:{server.server_port}/"
        def policy(uri,frame=False):
            future=concurrent.futures.Future();future.set_result({"allowed":uri.startswith(address),"upgrade":None,"metadata":False});return future
        try:
            with TestAppFixture() as fx:
                pending=[]
                class DelayedBinding:
                    def request_async(self,command,args=None):
                        actual=fx.bridge.request_async(command,args)
                        if command=="cookie-fingerprint":
                            held=concurrent.futures.Future();pending.append((held,actual));return held
                        return actual
                login=LoginWindow(fx.window,DelayedBinding(),{"id":"chaoxing","name":"Single capture fixture","loginUri":address},policy_request=policy)
                login.present();run_until(lambda:login.web.get_title()=="Local login fixture",20)
                login.save_clicked();run_until(lambda:len(pending)==1)
                login.close_with_save()
                until=time.monotonic()+0.15;run_until(lambda:time.monotonic()>until)
                try:
                    self.assertEqual(len(pending),1,"Closing started a second credential capture")
                finally:
                    login.finish_close()
        finally:
            server.shutdown();server.server_close();thread.join()

    def test_real_cookie_scope_navigation_and_private_profile(self):
        self.assertIsNotNone(importlib.util.find_spec("study_task_hub.login_window"), "WebKit login is not implemented")
        from study_task_hub.login_window import LoginWindow
        from study_task_hub.cookie_adapter import read_cookies
        server=http.server.ThreadingHTTPServer(("127.0.0.1",0),Handler)
        Handler.paths=[]
        thread=threading.Thread(target=server.serve_forever,daemon=True)
        thread.start()
        address=f"http://127.0.0.1:{server.server_port}/"
        def policy(uri, frame=False):
            value=concurrent.futures.Future()
            value.set_result({"allowed":uri.startswith(address),"upgrade":None,"metadata":False})
            return value
        try:
            with TestAppFixture() as fx:
                login=LoginWindow(fx.window,fx.bridge,{"id":"chaoxing","name":"Fixture","loginUri":address},policy_request=policy)
                login.present()
                run_until(lambda:login.web.get_title()=="Local login fixture",20)
                future=read_cookies(login.manager)
                run_until(future.done)
                cookie=next(c for c in future.result() if c["name"]=="fixture")
                self.assertEqual(cookie["domain"],"127.0.0.1")
                self.assertEqual(cookie["path"],"/")
                self.assertTrue(cookie["httpOnly"])
                self.assertEqual(cookie["sameSite"],"Strict")
                run_until(lambda:"/frame" in Handler.paths,10)
                login.evaluate("window.open('"+address+"'); 'opened';")
                run_until(lambda:bool(login.popups),10)
                run_until(lambda:login.popups[0][1].get_title()=="Local login fixture",10)
                login.web.load_uri("https://example.invalid/")
                run_until(lambda:login.status.get_text().startswith("已阻止"))
                self.assertNotEqual(login.web.get_title(),"example.invalid")
                fx.request("capture-session",{"platformId":"chaoxing","pageUri":"https://i.chaoxing.com/","cookies":[{"name":"_uid","value":"900001","domain":"i.chaoxing.com","path":"/","secure":True,"httpOnly":True,"expiresUtc":None,"sameSite":"Strict"}]})
                login.close_with_save()
                run_until(lambda:login.closed,6)
                self.assertEqual(fx.request("get-session",{"platform":"chaoxing"})["cookies"][0]["value"],"900001")
        finally:
            server.shutdown();server.server_close();thread.join()

    def test_untrusted_tls_certificate_is_rejected(self):
        from study_task_hub.login_window import LoginWindow
        with tempfile.TemporaryDirectory(prefix="study-task-hub-tls-") as tmp:
            from pathlib import Path
            key,cert=Path(tmp)/"key.pem",Path(tmp)/"cert.pem"
            subprocess.run(["openssl","req","-x509","-newkey","rsa:2048","-nodes","-keyout",str(key),"-out",str(cert),"-days","1","-subj","/CN=localhost"],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            server=http.server.ThreadingHTTPServer(("127.0.0.1",0),Handler)
            context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(cert,key)
            server.socket=context.wrap_socket(server.socket,server_side=True)
            thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
            address=f"https://127.0.0.1:{server.server_port}/"
            def policy(uri,frame=False):
                future=concurrent.futures.Future();future.set_result({"allowed":uri.startswith(address),"metadata":False,"upgrade":None});return future
            try:
                with TestAppFixture() as fx:
                    login=LoginWindow(fx.window,fx.bridge,{"id":"chaoxing","name":"TLS Fixture","loginUri":address},policy_request=policy)
                    login.present()
                    run_until(lambda:"证书校验失败" in login.status.get_text(),20)
                    self.assertNotEqual(login.web.get_title(),"Local login fixture")
                    login.finish_close()
            finally:
                server.shutdown();server.server_close();thread.join()

if __name__=="__main__":unittest.main()
