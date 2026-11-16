import concurrent.futures
import datetime
import gi
gi.require_version("Soup", "3.0")
from gi.repository import Soup, GLib

def read_cookies(manager, cancellable=None):
    future = concurrent.futures.Future()
    def ready(manager, result):
        try:
            cookies = manager.get_all_cookies_finish(result)
            values = []
            for cookie in cookies:
                expires = cookie.get_expires()
                values.append({"name": cookie.get_name(), "value": cookie.get_value(),
                    "domain": cookie.get_domain(), "path": cookie.get_path(),
                    "secure": cookie.get_secure(), "httpOnly": cookie.get_http_only(),
                    "expiresUtc": expires.format_iso8601() if expires else None,
                    "sameSite": {Soup.SameSitePolicy.NONE:"None", Soup.SameSitePolicy.LAX:"Lax", Soup.SameSitePolicy.STRICT:"Strict"}[cookie.get_same_site_policy()]})
            if not future.done():
                future.set_result(sorted(values, key=lambda c: (c["domain"], c["path"], c["name"])))
        except Exception as error:
            if not future.done():
                future.set_exception(error)
    manager.get_all_cookies(cancellable, ready)
    return future

def restore_cookies(manager, values, cancellable=None):
    future = concurrent.futures.Future()
    cookies = []
    for value in values:
        cookie = Soup.Cookie.new(value["name"], value["value"], value["domain"], value["path"], -1)
        cookie.set_secure(value["secure"])
        cookie.set_http_only(value["httpOnly"])
        cookie.set_same_site_policy({"None":Soup.SameSitePolicy.NONE,"Lax":Soup.SameSitePolicy.LAX,"Strict":Soup.SameSitePolicy.STRICT}.get(value.get("sameSite"), Soup.SameSitePolicy.NONE))
        if value.get("expiresUtc"):
            date = datetime.datetime.fromisoformat(value["expiresUtc"].replace("Z", "+00:00"))
            cookie.set_expires(GLib.DateTime.new_from_unix_utc(int(date.timestamp())))
        cookies.append(cookie)
    def ready(manager, result):
        try:
            manager.replace_cookies_finish(result)
            future.set_result(True)
        except Exception as error:
            future.set_exception(error)
    manager.replace_cookies(cookies, cancellable, ready)
    return future
