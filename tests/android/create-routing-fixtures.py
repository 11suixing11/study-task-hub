"""Build offline Android intent targets for the dedicated installation-test AVD."""
import argparse
import subprocess
from pathlib import Path
from zipfile import ZipFile


def run(arguments):
    result = subprocess.run([str(value) for value in arguments], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode:
        raise RuntimeError(f"Fixture tool failed: {Path(arguments[0]).name}\n{result.stdout}\n{result.stderr}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--jdk", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=True)
    build_tools = args.sdk / "build-tools/36.0.0"
    android = args.sdk / "platforms/android-36/android.jar"
    key = root / "synthetic.keystore"
    if not key.exists():
        run([args.jdk / "bin/keytool.exe", "-genkeypair", "-keystore", key, "-alias", "synthetic",
             "-storepass", "synthetic-local-only", "-keypass", "synthetic-local-only", "-keyalg", "RSA", "-keysize", "2048",
             "-validity", "3650", "-dname", "CN=Offline Android Routing Fixture", "-noprompt"])
    for mode in ("app_task", "app_home", "browser"):
        directory = root / mode
        classes = directory / "classes"
        dex = directory / "dex"
        resources = directory / "res"
        for path in (directory, classes, dex, resources):
            path.mkdir(parents=True, exist_ok=True)
        package = "cn.yuki.syntheticbrowser" if mode == "browser" else "com.chaoxing.mobile"
        label = "隔离网页路由测试" if mode == "browser" else "隔离任务路由测试"
        filters = '<intent-filter><action android:name="android.intent.action.MAIN"/><category android:name="android.intent.category.LAUNCHER"/></intent-filter>'
        if mode == "browser":
            filters += '<intent-filter><action android:name="android.intent.action.MAIN"/><category android:name="android.intent.category.APP_BROWSER"/><category android:name="android.intent.category.DEFAULT"/></intent-filter>'
        if mode != "app_home":
            host = '' if mode == "browser" else ' android:host="mooc1.chaoxing.com"'
            filters += '<intent-filter><action android:name="android.intent.action.VIEW"/><category android:name="android.intent.category.BROWSABLE"/><category android:name="android.intent.category.DEFAULT"/><data android:scheme="https"' + host + '/></intent-filter>'
        manifest = directory / "AndroidManifest.xml"
        manifest.write_text(f'''<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{package}" android:versionCode="1" android:versionName="1.0">
<uses-sdk android:minSdkVersion="28" android:targetSdkVersion="36"/>
<application android:label="{label}" android:theme="@android:style/Theme.Material.Light.NoActionBar">
<activity android:name=".TestActivity" android:exported="true">{filters}</activity>
</application></manifest>''', encoding="utf-8")
        source = directory / "TestActivity.java"
        source.write_text(f'''package {package};
public final class TestActivity extends android.app.Activity {{
  @Override public void onCreate(android.os.Bundle state) {{
    super.onCreate(state);
    android.widget.TextView view = new android.widget.TextView(this);
    view.setText("{label}\\n模式：{mode}\\n仅接收合成 Intent；无网络权限。\\n自动返回聚合平台。");
    view.setTextSize(20); view.setPadding(24,80,24,24); setContentView(view);
    new android.os.Handler(android.os.Looper.getMainLooper()).postDelayed(() -> finish(), 500);
  }}
}}''', encoding="utf-8")
        run([args.jdk / "bin/javac.exe", "--release", "8", "-encoding", "UTF-8", "-classpath", android, "-d", classes, source])
        run([build_tools / "d8.bat", "--min-api", "28", "--lib", android, "--output", dex,
             classes / package.replace(".", "/") / "TestActivity.class"])
        unsigned = directory / "unsigned.apk"
        run([build_tools / "aapt.exe", "package", "-f", "-M", manifest, "-S", resources, "-I", android, "-F", unsigned])
        with ZipFile(unsigned, "a") as apk:
            apk.write(dex / "classes.dex", "classes.dex")
        signed = directory / "routing-fixture.apk"
        run([build_tools / "apksigner.bat", "sign", "--ks", key, "--ks-key-alias", "synthetic", "--ks-pass", "pass:synthetic-local-only",
             "--key-pass", "pass:synthetic-local-only", "--out", signed, unsigned])
        print(f"Built offline intent fixture: {mode}")


if __name__ == "__main__":
    main()
