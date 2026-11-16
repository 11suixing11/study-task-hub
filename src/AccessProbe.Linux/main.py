#!/usr/bin/env python3
import argparse
import os
from pathlib import Path
import signal
import sys

def main():
    parser = argparse.ArgumentParser(description="聚合平台 Ubuntu 桌面版")
    parser.add_argument("--backend", type=Path)
    parser.add_argument("--data-root", type=Path)
    args = parser.parse_args()
    here = Path(__file__).resolve().parent
    backend = args.backend or here.parent / "backend" / "StudyTaskHub.LinuxBackend"
    data_root = args.data_root or Path(os.environ.get("XDG_DATA_HOME") or Path.home()/".local/share") / "study-task-hub"
    try:
        import gi
        gi.require_version("Gtk", "4.0")
        gi.require_version("Adw", "1")
        gi.require_version("WebKit", "6.0")
        from gi.repository import GLib
        from study_task_hub.application import StudyTaskHubApplication
    except (ImportError, ValueError):
        print("缺少桌面依赖。请安装 python3-gi、gir1.2-gtk-4.0、gir1.2-adw-1 和 gir1.2-webkit-6.0 后重试。", file=sys.stderr)
        return 1
    if not backend.is_file():
        print("未找到后台程序，请使用完整安装包或运行源码启动脚本。", file=sys.stderr)
        return 1
    app = StudyTaskHubApplication(backend, data_root)
    for signum in (signal.SIGTERM, signal.SIGINT):
        GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signum, lambda: app.request_close() or GLib.SOURCE_REMOVE)
    return app.run([sys.argv[0]])

if __name__ == "__main__":
    raise SystemExit(main())
