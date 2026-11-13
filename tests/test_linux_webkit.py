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
