import ctypes
from pathlib import Path
import sys
import time
import os

root=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/"tests"))
from linux_ui_helpers import TestAppFixture, run_until
from gi.repository import Gtk, Adw
from study_task_hub.task_editor import TaskEditor
Gtk.init()
if os.environ.get("STUDY_TASK_HUB_LARGE_FONT") == "1":
    Gtk.Settings.get_default().set_property("gtk-font-name", "Sans 16")

def save_widget(widget, path):
    snapshot=Gtk.Snapshot.new()
    Gtk.WidgetPaintable.new(widget).snapshot(snapshot,widget.get_width(),widget.get_height())
    library=ctypes.CDLL("libgtk-4.so.1")
    library.gtk_snapshot_to_node.argtypes=[ctypes.c_void_p]
    library.gtk_snapshot_to_node.restype=ctypes.c_void_p
    library.gsk_renderer_render_texture.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p]
    library.gsk_renderer_render_texture.restype=ctypes.c_void_p
    library.gdk_texture_save_to_png.argtypes=[ctypes.c_void_p,ctypes.c_char_p]
    library.gdk_texture_save_to_png.restype=ctypes.c_int
    library.gsk_render_node_unref.argtypes=[ctypes.c_void_p]
    library.g_object_unref.argtypes=[ctypes.c_void_p]
    node=library.gtk_snapshot_to_node(hash(snapshot))
    if not node:raise RuntimeError("Widget snapshot is empty")
    renderer=widget.get_native().get_renderer()
    texture=library.gsk_renderer_render_texture(hash(renderer),node,None)
    try:
        if not library.gdk_texture_save_to_png(texture,str(path).encode()):raise RuntimeError("Screenshot could not be saved")
    finally:
        library.g_object_unref(texture)
        library.gsk_render_node_unref(node)

output=root/".local/ubuntu-desktop-work/screenshots"
output.mkdir(parents=True,exist_ok=True)
style=Adw.StyleManager.get_default()
original=style.get_color_scheme()
try:
    for theme,scheme in (("dark",Adw.ColorScheme.FORCE_DARK),("light",Adw.ColorScheme.FORCE_LIGHT)):
        style.set_color_scheme(scheme)
        with TestAppFixture(width=1000,height=700) as fx:
            for title,platform,course in (("线性代数 · 第三次作业","chaoxing","线性代数"),("数据结构测验","pta","数据结构"),("本周英语学习","ucampus","大学英语"),("课程报告","mooc","计算机导论")):
                fx.request("create-task",{"title":title,"platformId":platform,"courseName":course,"deadlineKind":"known","deadlineIso":"2026-11-14T20:00:00+08:00","notes":"请核对官网要求后提交。此记录仅用于隔离界面验收。","scoreText":""})
            fx.window.dashboard.refresh()
            fx.wait(lambda:fx.window.dashboard.task_model.get_n_items()==4)
            fx.window.dashboard.selection.set_selected(0)
            fx.wait(lambda:fx.window.dashboard.selected_detail is not None)
            settle=time.monotonic()+0.3
            run_until(lambda:time.monotonic()>settle)
            suffix=("-scale2" if fx.window.get_scale_factor()==2 else "")+("-large-font" if os.environ.get("STUDY_TASK_HUB_LARGE_FONT")=="1" else "")
            save_widget(fx.window,output/(theme+"-1000x700"+suffix+".png"))
            if theme=="dark":
                editor=TaskEditor(fx.window,fx.bridge)
                editor.fields["title"].set_text("新增学习任务")
                editor.present()
                run_until(lambda:editor.get_mapped() and editor.get_width()>0)
                settle=time.monotonic()+0.2;run_until(lambda:time.monotonic()>settle)
                save_widget(editor,output/"task-editor.png")
                editor.destroy()
            print(theme,fx.window.get_width(),fx.window.get_height(),"scale",fx.window.get_scale_factor())
finally:
    style.set_color_scheme(original)
