"""Exercise actual Android back events on the dedicated, offline test emulator."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--serial", default="emulator-5560")
    parser.add_argument("--adb", default=str(Path(os.environ["LOCALAPPDATA"]) / "Android/Sdk/platform-tools/adb.exe"))
    parser.add_argument("--artifact-directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--package", choices=["cn.yuki.learningaggregator", "cn.yuki.learningaggregator.performance"],
                        default="cn.yuki.learningaggregator")
    args = parser.parse_args()
    if not re.fullmatch(r"emulator-\d+", args.serial):
        raise RuntimeError("Only the dedicated emulator is permitted.")
    package = args.package
    checks = []

    def adb(*values):
        result = subprocess.run([args.adb, "-s", args.serial, *values], capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=30)
        if result.returncode:
            raise RuntimeError(f"ADB command failed: {' '.join(values[:3])}: {result.stderr[:200]}")
        return result.stdout.strip()

    def check(value, label):
        if not value:
            raise AssertionError(label)
        checks.append(label)

    check(adb("shell", "getprop", "ro.kernel.qemu") == "1", "device is an emulator")
    check(adb("emu", "avd", "name").splitlines()[0] == "aggregator-install-test", "dedicated installation-test AVD")
    api = int(adb("shell", "getprop", "ro.build.version.sdk"))
    check(api >= 33, "modern back dispatcher is available for device verification")
    args.output.mkdir(parents=True, exist_ok=False)
    manifest = json.loads((args.artifact_directory / "package.json").read_text(encoding="utf-8-sig"))
    check(manifest["applicationId"] == package, "manifest identifies the requested application")
    apk = args.artifact_directory / f"聚合平台-{manifest['version']}-android.apk"
    digest = hashlib.sha256(apk.read_bytes()).hexdigest()
    check(digest == manifest["sha256"].lower(), "artifact matches its manifest")
    installed = adb("shell", "pm", "path", package).removeprefix("package:")
    check(bool(re.fullmatch(r"/data/app/[a-zA-Z0-9_/+~.=-]+/base\.apk", installed)), "installed package path is recognized")
    check(adb("shell", "sha256sum", installed).split()[0].lower() == digest, "installed APK is the final artifact")
    component = adb("shell", "cmd", "package", "resolve-activity", "--brief", package).splitlines()[-1]
    check(component.startswith(package + "/"), "launcher is the expected aggregator")
    adb("root")
    adb("wait-for-device")
    adb("shell", "svc", "wifi", "disable")
    adb("shell", "svc", "data", "disable")
    adb("shell", "input", "keyevent", "KEYCODE_WAKEUP")
    adb("shell", "wm", "dismiss-keyguard")
    remote = f"/data/user/0/{package}/files/synthetic-back-navigation-test"
    reports = {}

    def read_report(mode, minimum_events=0):
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline:
            try:
                value = json.loads(adb("shell", "cat", f"{remote}/back-{mode}.json"))
                if value.get("outcome") != "passed":
                    raise AssertionError(f"Native preparation failed: {mode}: {value}")
                if len(value.get("events", [])) >= minimum_events:
                    check(not value["officialNetworkAccessed"] and not value["productionDataAccessed"],
                          f"{mode} uses only isolated data")
                    return value
            except (json.JSONDecodeError, RuntimeError):
                pass
            time.sleep(.12)
        raise AssertionError(f"Back event report timed out: {mode}/{minimum_events}")

    def start(mode):
        adb("shell", "am", "force-stop", package)
        adb("shell", "rm", "-f", f"{remote}/back-{mode}.json")
        adb("shell", "am", "start", "-W", "-n", component, "--es", "synthetic_back_navigation_test", mode)
        report = read_report(mode)
        check(report["modernCallbackRegistered"], f"{mode} registers a modern system callback")
        return report

    def press(mode, count=1):
        adb("shell", "input", "keyevent", "KEYCODE_BACK")
        report = read_report(mode, count)
        check(report["events"][-1]["source"] == "dispatcher", f"{mode} key event uses Android dispatcher")
        return report

    def save(mode, report):
        reports[mode] = report
        (args.output / f"{mode}.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def foreground():
        return any(package in line for line in adb("shell", "dumpsys", "activity", "activities").splitlines()
                   if "mResumedActivity" in line or "topResumedActivity" in line)

    for mode, expected_root, parent in [("platforms", "tasks", False), ("settings", "tasks", False),
                                       ("platform", "platforms", False), ("detail", "tasks", False),
                                       ("courses", "platforms", False), ("notification", "tasks", False),
                                       ("deadline", "tasks", True), ("add", "tasks", False),
                                       ("login", "platforms", True)]:
        start(mode)
        report = press(mode)
        event = report["events"][-1]
        check(event["rootPage"] == expected_root and event["hasParent"] == parent,
              f"{mode} returns exactly one business level")
        check(event["alive"] and not event["finishing"] and not event["exitPending"],
              f"{mode} does not exit or arm a home exit")
        check(event["taskUnchanged"] and event["taskCount"] == 1, f"{mode} preserves task and unsaved edit")
        if mode == "login":
            check(not event["loginOpen"] and event["loginCancelled"] and event["checkCancelled"],
                  "login system back destroys WebView and cancels login/check lifetime")
        save(mode, report)

    start("home")
    report = press("home")
    check(report["events"][-1]["action"] == "PromptExit" and report["events"][-1]["alive"]
          and report["exitPending"] and foreground(), "one back on task home keeps the app in front and prompts")
    save("home", report)

    start("double")
    # One input process sends two separate down/up events with minimal overhead.
    adb("shell", "input", "keyevent", "KEYCODE_BACK", "KEYCODE_BACK")
    report = read_report("double", 2)
    check([event["action"] for event in report["events"][:2]] == ["PromptExit", "Exit"],
          "two actual back events confirm exit")
    check(not report["events"][-1]["alive"] and report["events"][-1]["finishing"],
          "double back finishes the activity")
    save("double", report)

    start("timeout")
    press("timeout")
    time.sleep(2.15)
    report = press("timeout", 2)
    check([event["action"] for event in report["events"][:2]] == ["PromptExit", "PromptExit"],
          "expired double-back window prompts again")
    save("timeout", report)

    start("lifecycle")
    press("lifecycle")
    adb("shell", "input", "keyevent", "KEYCODE_HOME")
    adb("shell", "am", "start", "-W", "-n", component)
    report = press("lifecycle", 2)
    check([event["action"] for event in report["events"][:2]] == ["PromptExit", "PromptExit"],
          "background and resume clear a pending home exit")
    save("lifecycle", report)

    start("toolbar")
    xml_remote = "/sdcard/synthetic-back-toolbar.xml"
    adb("shell", "uiautomator", "dump", xml_remote)
    xml = adb("shell", "cat", xml_remote)
    match = re.search(r'<node\b(?=[^>]*text="‹ 返回")(?=[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]")[^>]*>', xml)
    check(match is not None, "toolbar business back button is accessible")
    x1, y1, x2, y2 = map(int, match.groups())
    adb("shell", "input", "tap", str((x1+x2)//2), str((y1+y2)//2))
    report = read_report("toolbar", 1)
    event = report["events"][-1]
    check(event["source"] == "toolbar" and event["rootPage"] == "platforms" and event["hasParent"]
          and not event["loginOpen"] and event["loginCancelled"] and event["checkCancelled"],
          "toolbar uses the same business back and login cancellation as system back")
    save("toolbar", report)

    start("legacy")
    report = read_report("legacy", 1)
    check(report["events"][-1]["source"] == "legacy" and not report["events"][-1]["hasParent"]
          and report["events"][-1]["alive"], "legacy Activity callback shares the same parent behavior")
    save("legacy", report)

    overlays = adb("shell", "cmd", "overlay", "list", "android")
    original = next((name for name in re.findall(r"\[x\]\s+(\S+)", overlays)
                     if name.startswith("com.android.internal.systemui.navbar.")), None)
    gestural = "com.android.internal.systemui.navbar.gestural"
    check(gestural in overlays, "emulator includes the system gestural navigation overlay")
    try:
        adb("shell", "cmd", "overlay", "enable-exclusive", "--category", gestural)
        time.sleep(.5)
        start("detail")
        size = re.findall(r"(\d+)x(\d+)", adb("shell", "wm", "size"))[-1]
        width, height = map(int, size)
        adb("shell", "input", "swipe", "1", str(height//2), str(width//2), str(height//2), "350")
        report = read_report("detail", 1)
        event = report["events"][-1]
        check(event["source"] == "dispatcher" and event["action"] == "Parent" and event["alive"]
              and not event["hasParent"], "real edge-back gesture returns from task detail to task home")
        save("gesture-detail", report)
    finally:
        if original:
            adb("shell", "cmd", "overlay", "enable-exclusive", "--category", original)
        else:
            adb("shell", "cmd", "overlay", "disable", gestural)

    summary = {"schema": 1, "outcome": "passed", "apkSha256": digest, "api": api,
               "avd": "aggregator-install-test", "checks": len(checks), "results": checks,
               "reports": list(reports), "officialNetworkAccessed": False, "productionDataAccessed": False,
               "realAccountAcceptance": "pending", "visualAcceptance": "pending"}
    (args.output / "back-navigation.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"outcome": "passed", "checks": len(checks), "output": str(args.output)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
