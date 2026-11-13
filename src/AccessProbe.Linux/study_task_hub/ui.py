import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Gtk, GLib
from .bridge import BackendError

PLATFORMS = [("all", "全部平台"), ("chaoxing", "学习通"), ("yuketang", "雨课堂"),
    ("changjiang", "长江雨课堂"), ("mooc", "中国大学 MOOC"), ("xuetang", "学堂在线"),
    ("zhihuishu", "智慧树"), ("ucampus", "U 校园"), ("pta", "PTA"), ("fif", "FiF")]
STATES = [("all", "全部状态"), ("pending", "待办"), ("completed", "已完成"),
    ("ignored", "已忽略"), ("returned", "被打回"), ("needsReview", "待核对")]

def label(text="", style=None):
    widget = Gtk.Label(label=text, xalign=0, wrap=True)
    if style:
        widget.add_css_class(style)
    return widget

def margins(widget, size=12):
    for side in ("top", "bottom", "start", "end"):
        getattr(widget, "set_margin_" + side)(size)
    return widget

def on_result(future, success, failure):
    def apply():
        try:
            value = future.result()
        except BackendError as error:
            failure(f"{error.code} · {error}")
        except Exception:
            failure("后台操作未完成，请重试。")
        else:
            success(value)
        return GLib.SOURCE_REMOVE
    future.add_done_callback(lambda _: GLib.idle_add(apply))
