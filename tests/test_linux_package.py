import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from linux_test_paths import ROOT

class PackageChecks(unittest.TestCase):
    def test_deb_runs_backend_without_sdk_outside_repository(self):
        package=Path(os.environ.get("STUDY_TASK_HUB_TEST_DEB",ROOT/"artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb"))
        self.assertTrue(package.is_file(),"Self-contained Debian package is not built")
        with tempfile.TemporaryDirectory(prefix="study-task-hub-package-") as tmp:
            tree=Path(tmp)/"tree"
            subprocess.run(["dpkg-deb","--extract",str(package),str(tree)],check=True)
            metadata=subprocess.check_output(["dpkg-deb","--field",str(package),"Architecture"],text=True).strip()
            self.assertEqual(metadata,"amd64")
            env={k:v for k,v in os.environ.items() if not k.startswith("DOTNET_") and k not in
                ("NUGET_PACKAGES","GI_TYPELIB_PATH","PYTHONPATH","PYTHONHOME","LD_LIBRARY_PATH","GTK_PATH","GIO_EXTRA_MODULES")}
            env["PATH"]="/usr/bin:/bin"
            backend=tree/"opt/study-task-hub/backend/StudyTaskHub.LinuxBackend"
            self.assertTrue((backend.parent/"libcoreclr.so").is_file())
            request=json.dumps({"version":1,"id":1,"command":"create-task","args":{"title":"包内任务","platformId":"pta","courseName":"","deadlineKind":"unknown","scoreText":"","notes":"独立运行"}},ensure_ascii=False)+"\n"+json.dumps({"version":1,"id":2,"command":"snapshot","args":{}})+"\n"
            result=subprocess.run([str(backend),"--data-root",str(Path(tmp)/"data")],input=request,text=True,capture_output=True,cwd=tmp,env=env,timeout=15)
            self.assertEqual(result.returncode,0,result.stderr)
            replies=[json.loads(line) for line in result.stdout.splitlines()]
            self.assertTrue(replies[0]["ok"],replies[0])
            self.assertEqual(replies[1]["result"]["total"],1,replies)
            self.assertEqual(replies[1]["result"]["items"][0]["task"]["title"],"包内任务")
            self.assertTrue((tree/"usr/share/applications/study-task-hub.desktop").is_file())
            self.assertTrue((tree/"usr/share/icons/hicolor/512x512/apps/study-task-hub.png").is_file())
            launcher=tree/"usr/bin/study-task-hub"
            process=subprocess.Popen([str(launcher),"--data-root",str(Path(tmp)/"gui")],cwd=tmp,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
            try:
                import time
                deadline=time.monotonic()+10
                while not (Path(tmp)/"gui/tasks.db").exists() and process.poll() is None and time.monotonic()<deadline:
                    time.sleep(0.05)
                self.assertIsNone(process.poll(),"Packaged launcher exited before opening the application")
                self.assertTrue((Path(tmp)/"gui/tasks.db").is_file())
                time.sleep(0.5)
            finally:
                process.terminate()
                output,errors=process.communicate(timeout=12)
            self.assertEqual(process.returncode,0,errors)

    def test_install_upgrade_remove_in_isolated_prefix_preserves_user_data(self):
        package=Path(os.environ.get("STUDY_TASK_HUB_TEST_DEB",ROOT/"artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb"))
        self.assertTrue(package.is_file(),"Package is not built")
        with tempfile.TemporaryDirectory(prefix="study-task-hub-install-") as tmp:
            target=Path(tmp)/"system"
            user=Path(tmp)/"user-data"
            target.mkdir()
            command=["fakeroot","dpkg","--root="+str(target),"--force-depends","--force-not-root"]
            subprocess.run(command+["--install",str(package)],check=True,capture_output=True,text=True)
            backend=target/"opt/study-task-hub/backend/StudyTaskHub.LinuxBackend"
            request=json.dumps({"version":1,"id":1,"command":"create-task","args":{"title":"升级保留任务","platformId":"pta","courseName":"","deadlineKind":"unknown","scoreText":"","notes":"保留数据"}})+"\n"
            result=subprocess.run([str(backend),"--data-root",str(user)],input=request,text=True,capture_output=True,timeout=10)
            self.assertTrue(json.loads(result.stdout)["ok"])
            key=(user/"protection.key").read_bytes()
            subprocess.run(command+["--install",str(package)],check=True,capture_output=True,text=True)
            result=subprocess.run([str(backend),"--data-root",str(user)],input=json.dumps({"version":1,"id":1,"command":"snapshot","args":{}})+"\n",text=True,capture_output=True,timeout=10)
            self.assertEqual(json.loads(result.stdout)["result"]["items"][0]["task"]["notes"],"保留数据")
            self.assertEqual((user/"protection.key").read_bytes(),key)
            subprocess.run(command+["--remove","study-task-hub"],check=True,capture_output=True,text=True)
            self.assertFalse(backend.exists())
            self.assertTrue((user/"tasks.db").is_file())
            self.assertEqual((user/"protection.key").read_bytes(),key)

if __name__=="__main__":unittest.main()
