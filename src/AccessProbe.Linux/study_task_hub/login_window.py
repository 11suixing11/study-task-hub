import concurrent.futures
import os
from pathlib import Path
import gi
gi.require_version("WebKit", "6.0")
from gi.repository import WebKit, Gtk, Adw, Gio, GLib
from .ui import label, margins, on_result, recovery_button
from .bridge import BackendError
from .cookie_adapter import read_cookies, restore_cookies

MOOC_SCRIPT = "(() => { const u = window.webUser; return u ? String(u.id || '') : ''; })()"
TOKEN_SCRIPT = "(() => { const read = w => { try { const l=w.location; if (l.origin==='https://onlineweb.zhihuishu.com' && l.pathname==='/onlinestuh5' || l.origin==='https://onlinestuh5.zhihuishu.com' && l.pathname==='/') return w.localStorage.getItem('token'); } catch {} return null; }; return read(window) || Array.from(window.frames).map(read).find(Boolean) || ''; })()"

def private_directory(path):
    if path.is_symlink():
        raise BackendError("SESSION001", "登录目录不可使用符号链接。")
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(path, 0o700)

class LoginWindow(Adw.Window):
    def __init__(self, parent, bridge, platform_view, policy_request=None):
        super().__init__(transient_for=parent, modal=True, title="连接" + platform_view["name"])
        self.parent, self.bridge, self.platform = parent, bridge, platform_view["id"]
        self.login_uri = platform_view["loginUri"]
        self.set_default_size(1000, 760)
        self.closing = False
        self.closed = False
        self.busy = False
        self.active_capture = None
        self.close_timer = None
        self.cancellable = Gio.Cancellable()
        self.popups = []
        self.upgrades = set()
        self.policy_request = policy_request or (lambda address, frame=False: self.bridge.request_async("platform-policy", {"platform":self.platform, "address":address, "frame":frame}))
        root = Path(parent.get_application().data_root) / "profiles"
        private_directory(root)
        self.profile_path = root / self.platform
        private_directory(self.profile_path)
        cache = self.profile_path / "cache"
        private_directory(cache)
        self.network = WebKit.NetworkSession.new(str(self.profile_path), str(cache))
        self.network.set_tls_errors_policy(WebKit.TLSErrorsPolicy.FAIL)
        self.manager = self.network.get_cookie_manager()
        self.manager.set_persistent_storage(str(self.profile_path / "cookies.db"), WebKit.CookiePersistentStorage.SQLITE)
        self.web = WebKit.WebView(network_session=self.network)
        self.web.get_settings().set_javascript_can_open_windows_automatically(True)
        self.configure_web(self.web)
        self.web.connect("create", self.create_popup)
        toolbar = Adw.ToolbarView()
        toolbar.add_top_bar(Adw.HeaderBar())
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.status = margins(label("请在官网完成登录；确认后保存会话并读取课程。", "dim-label"))
        body.append(self.status)
        body.append(margins(recovery_button(self, self.status)))
        self.web.set_vexpand(True)
        body.append(self.web)
        controls = margins(Gtk.Box(spacing=8))
        home = Gtk.Button(label="返回平台登录页")
        home.connect("clicked", lambda *_: self.web.load_uri(platform_view["loginUri"]))
        self.capture_button = Gtk.Button(label="登录完成，保存并读取课程", hexpand=True)
        self.capture_button.add_css_class("suggested-action")
        self.capture_button.connect("clicked", lambda *_: self.save_clicked())
        controls.append(home)
        controls.append(self.capture_button)
        body.append(controls)
        toolbar.set_content(body)
        self.set_content(toolbar)
        self.connect("close-request", self.close_with_save)
        self.restore_login()

    def restore_login(self):
        requested_bridge = self.bridge
        def restored(session):
            if self.closed or self.bridge is not requested_bridge:
                return
            if session:
                def ready(_):
                    if not self.closed and self.bridge is requested_bridge:
                        self.web.load_uri(self.login_uri)
                on_result(restore_cookies(self.manager, session["cookies"], self.cancellable), ready, self.restore_failed)
            else:
                self.web.load_uri(self.login_uri)
        on_result(requested_bridge.request_async("get-session", {"platform":self.platform}), restored,
            lambda message: self.restore_failed(message) if self.bridge is requested_bridge else None)

    def rebind_bridge(self, bridge):
        if self.closed:
            return
        if self.active_capture is not None and not self.active_capture.done():
            self.active_capture.set_exception(BackendError("IPC004", "连接已恢复，请重新确认保存登录。"))
        self.active_capture = None
        self.bridge = bridge
        self.busy = False
        self.capture_button.set_sensitive(True)
        self.status.set_text("连接已恢复，请重新确认保存登录。")
        if self.web.get_uri() in (None, "about:blank"):
            self.restore_login()

    def restore_failed(self, message):
        if not self.closed:
            self.status.set_text(message)

    def configure_web(self, web):
        web.connect("decide-policy", self.decide_policy)
        web.connect("load-failed-with-tls-errors", self.tls_failed)
        web.connect("load-failed", lambda *_: self.load_failed())

    def load_failed(self):
        if not self.closed and not self.status.get_text().startswith("已阻止"):
            self.status.set_text("官网登录页未能读取，请检查网络后重试；原登录和任务保留。")
        return False

    def tls_failed(self, *_):
        self.status.set_text("证书校验失败，已停止加载。请检查网络和系统时间后重试。")
        return True

    def decide_policy(self, web, decision, kind):
        if self.closed or self.closing:
            decision.ignore()
            return True
        if kind == WebKit.PolicyDecisionType.RESPONSE:
            address = decision.get_response().get_uri()
            frame = not decision.is_main_frame_main_resource()
        else:
            address = decision.get_navigation_action().get_request().get_uri()
            frame = kind == WebKit.PolicyDecisionType.NAVIGATION_ACTION
        def success(value):
            if self.closed or self.closing:
                decision.ignore()
            elif value["allowed"]:
                decision.use()
            elif value.get("upgrade") and len(self.upgrades) < 4 and value["upgrade"] not in self.upgrades:
                decision.ignore()
                self.upgrades.add(value["upgrade"])
                web.load_uri(value["upgrade"])
            else:
                decision.ignore()
                self.status.set_text("已阻止不属于本平台的页面或不安全跳转。")
        on_result(self.policy_request(address, frame), success, lambda _: decision.ignore())
        return True

    def create_popup(self, web, action):
        popup_web = WebKit.WebView(related_view=web)
        self.configure_web(popup_web)
        popup = Adw.Window(transient_for=self, modal=True, title="官方登录验证")
        popup.set_default_size(680, 640)
        popup.set_content(popup_web)
        popup_web.connect("close", lambda *_: popup.close())
        self.popups.append((popup, popup_web))
        popup.present()
        return popup_web

    def evaluate(self, script):
        future = concurrent.futures.Future()
        def ready(web, result):
            try:
                value = web.evaluate_javascript_finish(result).to_string()
                if len(value.encode("utf-8")) > 16384:
                    raise BackendError("SESSION001", "官网会话字段超出限制。")
                future.set_result(value)
            except Exception as error:
                future.set_exception(error)
        self.web.evaluate_javascript(script, -1, None, None, self.cancellable, ready)
        return future

    def capture_session(self):
        if self.active_capture is not None and not self.active_capture.done():
            return self.active_capture
        result = concurrent.futures.Future()
        self.active_capture = result
        if self.closed:
            result.set_exception(BackendError("SESSION001", "登录窗口已关闭，旧保存结果已丢弃。"))
            return result
        address = self.web.get_uri() or ""
        def guarded(callback):
            def apply(value):
                if not self.closed and not result.done() and self.active_capture is result:
                    callback(value)
            return apply
        def failed(message):
            if not result.done():
                if message.startswith(("IPC004", "IPC006")):
                    code,_,detail = message.partition(" · ")
                    result.set_exception(BackendError(code, detail))
                else:
                    result.set_exception(BackendError("SESSION001", message))
        def cookies_before(cookies):
            if not cookies:
                failed("未取得有效登录，原会话保留。")
                return
            def allowed(policy):
                if not policy["allowed"]:
                    failed("请回到对应官网保存登录。")
                    return
                metadata = self.evaluate(MOOC_SCRIPT if self.platform == "mooc" else TOKEN_SCRIPT) if policy["metadata"] else concurrent.futures.Future()
                if not policy["metadata"]:
                    metadata.set_result("")
                def metadata_ready(value):
                    def cookies_after(after):
                        if after != cookies or address != self.web.get_uri():
                            failed("读取期间官网登录发生变化，请重试。")
                            return
                        def fingerprint(binding):
                            payload={"platformId":self.platform,"pageUri":address,"cookies":after,
                                "accountId":value or None if self.platform=="mooc" else None,
                                "token":value or None if self.platform=="zhihuishu" else None,
                                "tokenBinding":binding if self.platform=="zhihuishu" and value else None}
                            def saved(info):
                                if not result.done():
                                    result.set_result(info)
                            on_result(self.bridge.request_async("capture-session", payload), guarded(saved), failed)
                        on_result(self.bridge.request_async("cookie-fingerprint", {"platform":self.platform,"cookies":after}), guarded(fingerprint), failed)
                    on_result(read_cookies(self.manager,self.cancellable), guarded(cookies_after), failed)
                on_result(metadata, guarded(metadata_ready), failed)
            on_result(self.policy_request(address), guarded(allowed), failed)
        on_result(read_cookies(self.manager,self.cancellable), guarded(cookies_before), failed)
        return result

    def save_clicked(self):
        if self.busy or self.closed:
            return
        self.busy = True
        self.capture_button.set_sensitive(False)
        self.status.set_text("正在保存并校验官网登录…")
        capture = self.capture_session()
        def saved(_):
            if self.closed or self.closing or self.active_capture is not capture:
                return
            self.busy = False
            self.capture_button.set_sensitive(True)
            self.status.set_text("登录已保存，正在读取课程目录…")
            on_result(self.bridge.request_async("start-sync", {"platform":self.platform,"catalogOnly":True}), lambda _: self.status.set_text("课程读取已开始，可关闭此窗口后在平台管理查看。"), failed)
        def failed(message):
            if self.closed or self.closing or self.active_capture is not capture:
                return
            self.busy = False
            self.capture_button.set_sensitive(True)
            self.status.set_text(message)
        on_result(capture, saved, failed)

    def close_with_save(self, *_):
        if self.closed:
            return False
        if self.closing:
            return True
        self.closing = True
        self.web.stop_loading()
        self.status.set_text("正在保存登录，最多等待 5 秒…")
        self.close_timer = GLib.timeout_add(5000, lambda: self.finish_close("保存超时；原会话保留，请重开连接窗口再保存。"))
        on_result(self.capture_session(), lambda _: self.finish_close(), self.finish_close)
        return True

    def finish_close(self, message=None):
        if self.closed:
            return GLib.SOURCE_REMOVE
        self.closed = True
        if self.active_capture is not None and not self.active_capture.done():
            self.active_capture.set_exception(BackendError("SESSION001", "登录窗口已关闭，旧保存结果已丢弃。"))
        self.cancellable.cancel()
        if self.close_timer:
            GLib.source_remove(self.close_timer)
            self.close_timer = None
        for popup, _ in self.popups:
            popup.destroy()
        if message:
            self.parent.dashboard.show_error(message)
        self.destroy()
        return GLib.SOURCE_REMOVE
