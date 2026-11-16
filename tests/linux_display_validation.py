"""Measure packaged startup/query behavior and validate owned GTK windows on both outputs."""
import argparse
import concurrent.futures
import ctypes
import hashlib
import http.server
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import uuid

ROOT=Path(__file__).resolve().parents[1]

def run_until(predicate,timeout=8):
    from gi.repository import GLib
    deadline=time.monotonic()+timeout
    while not predicate():
        if time.monotonic()>=deadline:raise RuntimeError("Display validation timed out")
        while GLib.MainContext.default().pending():GLib.MainContext.default().iteration(False)
        time.sleep(0.005)

def save_widget(widget,path):
    from gi.repository import Gtk
    snapshot=Gtk.Snapshot.new()
    scale=widget.get_scale_factor()
    snapshot.scale(scale,scale)
    Gtk.WidgetPaintable.new(widget).snapshot(snapshot,widget.get_width(),widget.get_height())
    library=ctypes.CDLL("libgtk-4.so.1")
    library.gtk_snapshot_to_node.argtypes=[ctypes.c_void_p];library.gtk_snapshot_to_node.restype=ctypes.c_void_p
    library.gsk_renderer_render_texture.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p];library.gsk_renderer_render_texture.restype=ctypes.c_void_p
    library.gdk_texture_save_to_png.argtypes=[ctypes.c_void_p,ctypes.c_char_p];library.gdk_texture_save_to_png.restype=ctypes.c_int
    library.gsk_render_node_unref.argtypes=[ctypes.c_void_p];library.g_object_unref.argtypes=[ctypes.c_void_p]
    node=library.gtk_snapshot_to_node(hash(snapshot))
    if not node:raise RuntimeError("Empty widget snapshot")
    texture=library.gsk_renderer_render_texture(hash(widget.get_native().get_renderer()),node,None)
    try:
        if not library.gdk_texture_save_to_png(texture,str(path).encode()):raise RuntimeError("Screenshot save failed")
    finally:
        library.g_object_unref(texture);library.gsk_render_node_unref(node)

def gui_worker(ui,backend,data,output):
    started=float(os.environ.get("STUDY_TASK_HUB_BENCHMARK_START",time.monotonic()))
    sys.path.insert(0,str(ui))
    import gi
    gi.require_version("Gtk","4.0");gi.require_version("Adw","1");gi.require_version("Gdk","4.0")
    from gi.repository import Gdk
    from study_task_hub.application import StudyTaskHubApplication
    import study_task_hub.application as application_module
    assert Path(application_module.__file__).is_relative_to(ui)
    app=StudyTaskHubApplication(backend,data)
    app.set_application_id("io.github.studyTaskHub.DisplayCheck"+uuid.uuid4().hex)
    app.register(None);app.activate()
    report={}
    try:
        run_until(lambda:app.window and app.window.get_mapped() and app.window.dashboard.page is not None)
        report["packagedColdStartupSeconds"]=round(time.monotonic()-started,4)
        assert app.window.dashboard.page["total"]==5000
        app.window.set_title("聚合平台 · 隔离显示验证")
        display=Gdk.Display.get_default()
        report["displayType"]=display.__class__.__name__
        monitors=display.get_monitors()
        all_monitors=[monitors.get_item(i) for i in range(monitors.get_n_items())]
        report["availableScales"]=[m.get_scale_factor() for m in all_monitors]
        one=next((m for m in all_monitors if m.get_scale_factor()==1),None)
        two=next((m for m in all_monitors if m.get_scale_factor()==2),None)
        if one is None or two is None:
            report["mixedScale"]={"status":"not_run","reason":"Requires actual 1x and 2x outputs"}
        else:
            transitions=[]
            for monitor,expected in ((one,1),(two,2)):
                app.window.fullscreen_on_monitor(monitor)
                run_until(lambda:app.window.get_scale_factor()==expected and app.window.is_fullscreen())
                until=time.monotonic()+0.3;run_until(lambda:time.monotonic()>until)
                path=output/f"dashboard-{expected}x.png"
                save_widget(app.window,path)
                transitions.append({"scale":app.window.get_scale_factor(),"logicalWidth":app.window.get_width(),"logicalHeight":app.window.get_height(),"screenshot":path.name})
            class Fixture(http.server.BaseHTTPRequestHandler):
                def do_GET(self):
                    body="""<!doctype html><meta charset='utf-8'><title>WebKit 2x fixture</title>
                    <body style='background:#f6f7f9;font-family:sans-serif;padding:32px'>
                    <h1>本地 WebKit 显示验证</h1><p>仅用于隔离测试，没有访问正式账号。</p>
                    <div style='background:#1f6feb;color:white;padding:48px;font-size:24px'>2× 输出渲染</div></body>""".encode()
                    self.send_response(200);self.send_header("Content-Type","text/html; charset=utf-8");self.end_headers();self.wfile.write(body)
                def log_message(self,*_):pass
            server=http.server.ThreadingHTTPServer(("127.0.0.1",0),Fixture)
            thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
            address=f"http://127.0.0.1:{server.server_port}/"
            def policy(uri,frame=False):
                value=concurrent.futures.Future();value.set_result({"allowed":uri.startswith(address),"metadata":False,"upgrade":None});return value
            from study_task_hub.login_window import LoginWindow
            login=LoginWindow(app.window,app.bridge,{"id":"chaoxing","name":"本地渲染测试","loginUri":address},policy_request=policy)
            try:
                login.present();login.fullscreen_on_monitor(two)
                run_until(lambda:login.get_scale_factor()==2 and login.web.get_title()=="WebKit 2x fixture",15)
                until=time.monotonic()+0.5;run_until(lambda:time.monotonic()>until)
                webshot=output/"webkit-2x.png";save_widget(login,webshot)
                from PIL import Image
                with Image.open(webshot) as image:
                    sample=image.convert("RGB").resize((150,100))
                    colors=[sample.getpixel((x,y)) for y in range(100) for x in range(150)]
                    blue=sum(abs(r-31)<10 and abs(g-111)<10 and abs(b-235)<10 for r,g,b in colors)
                    assert blue>len(colors)//100,"WebKit fixture loaded without rendering its visible content"
                    report["webkit2x"]={"status":"passed","windowScale":login.get_scale_factor(),"imageWidth":image.width,"imageHeight":image.height,"blueCoverageSamples":blue,"screenshot":webshot.name}
            finally:
                login.finish_close();server.shutdown();server.server_close();thread.join()
            app.window.fullscreen_on_monitor(one)
            run_until(lambda:app.window.get_scale_factor()==1)
            transitions.append({"scale":app.window.get_scale_factor(),"returnedToFirstOutput":True})
            report["mixedScale"]={"status":"passed","mechanism":"GTK fullscreen_on_monitor on owned windows, actual Wayland output changes","transitions":transitions}
    finally:
        app.request_close();run_until(lambda:app.window is None,7)
    print(json.dumps(report,ensure_ascii=False),flush=True)

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--package",type=Path,default=ROOT/"artifacts/linux/study-task-hub_1.2.0~ubuntu5_amd64.deb")
    parser.add_argument("--output",type=Path,default=ROOT/"artifacts/linux/verification/display")
    parser.add_argument("--worker",nargs=4,metavar=("UI","BACKEND","DATA","OUTPUT"))
    args=parser.parse_args()
    if args.worker:
        gui_worker(*map(Path,args.worker));return
    args.package=args.package.resolve();args.output=args.output.resolve();args.output.mkdir(parents=True,exist_ok=True)
    sys.path.insert(0,str(ROOT/"src/AccessProbe.Linux"))
    from study_task_hub.bridge import BackendBridge
    with tempfile.TemporaryDirectory(prefix="study-task-hub-display-") as temporary:
        tree=Path(temporary)/"tree";data=Path(temporary)/"data"
        subprocess.run(["dpkg-deb","--extract",str(args.package),str(tree)],check=True)
        backend=tree/"opt/study-task-hub/backend/StudyTaskHub.LinuxBackend"
        bridge=BackendBridge(backend,data)
        try:
            bridge.request_async("create-task",{"title":"隔离性能测试","platformId":"pta","courseName":"测试课程","deadlineKind":"unknown","scoreText":"","notes":""}).result(timeout=5)
            bridge.request_async("save-settings",{"settings":{"notificationsEnabled":False,"automaticSyncEnabled":False}}).result(timeout=5)
        finally:bridge.close()
        with sqlite3.connect(data/"tasks.db") as connection:
            cursor=connection.execute("SELECT * FROM tasks")
            names=[item[0] for item in cursor.description];template=list(cursor.fetchone())
            connection.execute("DELETE FROM tasks")
            rows=[]
            for index in range(5000):
                row=template.copy();identity=uuid.uuid4().hex;row[names.index("id")]=identity
                payload=json.loads(template[names.index("payload")])
                payload["Id"]=identity
                payload["Title"]=f"隔离性能测试 {index+1}"
                payload["Notes"]="合成内容"*1000
                row[names.index("payload")]=json.dumps(payload,ensure_ascii=False)
                rows.append(row)
            columns=','.join('"'+name+'"' for name in names)
            connection.executemany(f"INSERT INTO tasks({columns}) VALUES({','.join('?' for _ in names)})",rows)
        samples=[]
        started=time.monotonic();bridge=BackendBridge(backend,data)
        try:
            for index in range(3):
                query_started=started if index==0 else time.monotonic()
                page=bridge.request_async("snapshot",{"limit":100}).result(timeout=10)
                assert page["total"]==5000 and len(page["items"])==100
                samples.append(round(time.monotonic()-query_started,4))
        finally:bridge.close()
        environment={k:v for k,v in os.environ.items() if not k.startswith("DOTNET_") and k not in ("NUGET_PACKAGES","GI_TYPELIB_PATH","PYTHONPATH","PYTHONHOME","LD_LIBRARY_PATH","GTK_PATH","GIO_EXTRA_MODULES")}
        environment["PATH"]="/usr/bin:/bin"
        environment["STUDY_TASK_HUB_BENCHMARK_START"]=str(time.monotonic())
        worker=subprocess.run(["/usr/bin/python3",str(Path(__file__).resolve()),"--worker",str(tree/"opt/study-task-hub/ui"),str(backend),str(data),str(args.output)],capture_output=True,text=True,env=environment,timeout=50)
        (args.output/"worker.log").write_text(worker.stderr)
        if worker.returncode:raise RuntimeError(worker.stderr)
        report=json.loads(worker.stdout.strip().splitlines()[-1])
        report.update({"package":args.package.name,"sha256":hashlib.sha256(args.package.read_bytes()).hexdigest(),"dataset":{"tasks":5000,"notesCharactersPerTask":4000,"links":0},"snapshotSeconds":samples,"startupTargetSeconds":3,"queryTargetSeconds":2,"startupTargetMet":report["packagedColdStartupSeconds"]<3,"queryTargetMet":max(samples)<2,"officialNetworkAccessed":False,"productionDataAccessed":False})
        (args.output/"validation.json").write_text(json.dumps(report,ensure_ascii=False,indent=2)+"\n")
        print(json.dumps(report,ensure_ascii=False))

if __name__=="__main__":main()
