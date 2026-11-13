import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import hashlib
from linux_test_paths import ROOT
from study_task_hub.bridge import BackendBridge

class PackageChecks(unittest.TestCase):
    def test_desktop_entry_has_the_application_id_used_by_gio(self):
        package=Path(os.environ.get("STUDY_TASK_HUB_TEST_DEB",ROOT/"artifacts/linux/study-task-hub_1.2.0~ubuntu2_amd64.deb"))
        with tempfile.TemporaryDirectory(prefix="study-task-hub-desktop-") as tmp:
            tree=Path(tmp)/"tree"
            subprocess.run(["dpkg-deb","--extract",str(package),str(tree)],check=True)
            code="import sys;sys.path.insert(0,sys.argv[1]);from study_task_hub.application import StudyTaskHubApplication;print(StudyTaskHubApplication(sys.argv[2],sys.argv[3]).get_application_id())"
            result=subprocess.run(["/usr/bin/python3","-c",code,str(tree/"opt/study-task-hub/ui"),str(tree/"opt/study-task-hub/backend/StudyTaskHub.LinuxBackend"),str(Path(tmp)/"data")],capture_output=True,text=True,timeout=10)
            self.assertEqual(result.returncode,0,result.stderr)
            desktop=tree/"usr/share/applications"/(result.stdout.strip()+".desktop")
            self.assertTrue(desktop.is_file(),"GNOME cannot resolve the desktop entry for the application's notification identity")
    def test_verified_ubuntu1_to_ubuntu2_upgrade_preserves_encrypted_data_and_profiles(self):
        current=Path(os.environ.get("STUDY_TASK_HUB_TEST_DEB",ROOT/"artifacts/linux/study-task-hub_1.2.0~ubuntu2_amd64.deb"))
        previous=ROOT/"artifacts/linux/history/1.2.0~ubuntu1/study-task-hub_1.2.0~ubuntu1_amd64.deb"
        old_report=json.loads((ROOT/"docs/releases/ubuntu-1.2.0~ubuntu1/verification.json").read_text())
        self.assertTrue(previous.is_file(),"Verified previous package is required for the upgrade test")
        self.assertEqual(hashlib.sha256(previous.read_bytes()).hexdigest(),old_report["sha256"])
        self.assertTrue(current.is_file(),"Ubuntu 2 package has not been built")
        old_version=subprocess.check_output(["dpkg-deb","--field",str(previous),"Version"],text=True).strip()
        new_version=subprocess.check_output(["dpkg-deb","--field",str(current),"Version"],text=True).strip()
        self.assertEqual(subprocess.run(["dpkg","--compare-versions",new_version,"gt",old_version]).returncode,0)
        with tempfile.TemporaryDirectory(prefix="study-task-hub-version-upgrade-") as tmp:
            system=Path(tmp)/"system";system.mkdir()
            data=Path(tmp)/"user-data"
            install=["fakeroot","dpkg","--root="+str(system),"--force-depends","--force-not-root"]
            subprocess.run(install+["--install",str(previous)],check=True,capture_output=True,text=True)
            self.assertTrue((system/"usr/share/applications/study-task-hub.desktop").is_file())
            executable=system/"opt/study-task-hub/backend/StudyTaskHub.LinuxBackend"
            input={"title":"旧版保留任务","platformId":"pta","courseName":"合成课程","deadlineKind":"known",
                "deadlineIso":"2026-11-26T17:45:37+08:00","link":"https://example.invalid/upgrade?signature=SYNTHETIC",
                "scoreText":"100","notes":"升级前的备注"}
            with self.backend(executable,data) as old:
                task=old.request_async("create-task",input).result(timeout=5)["view"]["task"]
                old.request_async("set-state",{"id":task["id"],"revision":1,"state":"completed"}).result(timeout=5)
                old.request_async("save-settings",{"settings":{"notificationsEnabled":False,"automaticSyncEnabled":False,"disclaimerSeen":True}}).result(timeout=5)
                old.request_async("select-courses",{"platform":"mooc","revision":0,"keys":[],"hideUnselected":False}).result(timeout=5)
                old.request_async("capture-session",{"platformId":"chaoxing","pageUri":"https://i.chaoxing.com/","cookies":[
                    {"name":"_uid","value":"910001","domain":".chaoxing.com","path":"/","secure":True,"httpOnly":True,"expiresUtc":None,"sameSite":"Strict"}]}).result(timeout=5)
            (data/"profiles").mkdir(mode=0o700)
            profile=data/"profiles/chaoxing";profile.mkdir(mode=0o700)
            marker=profile/"synthetic-profile.bin";marker.write_bytes(b"SYNTHETIC-PROFILE-ONLY");marker.chmod(0o600)
            before={str(p.relative_to(data)):hashlib.sha256(p.read_bytes()).hexdigest() for p in data.rglob("*") if p.is_file()}
            subprocess.run(install+["--install",str(current)],check=True,capture_output=True,text=True)
            self.assertTrue((system/"usr/share/applications/io.github.studyTaskHub.desktop").is_file())
            self.assertFalse((system/"usr/share/applications/study-task-hub.desktop").exists())
            after={str(p.relative_to(data)):hashlib.sha256(p.read_bytes()).hexdigest() for p in data.rglob("*") if p.is_file()}
            self.assertEqual(before,after,"Package upgrade changed user data before startup")
            installed=subprocess.check_output(["dpkg-query","--admindir="+str(system/"var/lib/dpkg"),"-W","-f=${Version}","study-task-hub"],text=True)
            self.assertEqual(installed,new_version)
            with self.backend(executable,data) as new:
                detail=new.request_async("get-task",{"id":task["id"]}).result(timeout=5)
                self.assertEqual(detail["link"],input["link"])
                self.assertEqual(detail["view"]["task"]["state"],"completed")
                self.assertEqual(detail["view"]["task"]["revision"],2)
                self.assertEqual(detail["view"]["task"]["deadlineUtc"],"2026-11-26T09:45:37+00:00")
                settings=new.request_async("get-settings").result(timeout=5)
                self.assertFalse(settings["notificationsEnabled"])
                self.assertFalse(settings["automaticSyncEnabled"])
                course=new.request_async("get-courses",{"platform":"mooc"}).result(timeout=5)
                self.assertTrue(course["selectionConfirmed"])
                self.assertFalse(course["hideUnselectedTasks"])
                session=new.request_async("get-session",{"platform":"chaoxing"}).result(timeout=5)
                self.assertEqual(session["cookies"][0]["value"],"910001")
                self.assertTrue(session["cookies"][0]["httpOnly"])
                changed=new.request_async("edit-task",{"id":task["id"],"revision":2,"input":dict(input,notes="升级后继续编辑")}).result(timeout=5)
                self.assertEqual(changed["view"]["task"]["revision"],3)
                self.assertEqual(changed["view"]["task"]["notes"],"升级后继续编辑")
            subprocess.run(install+["--remove","study-task-hub"],check=True,capture_output=True,text=True)
            self.assertFalse(executable.exists())
            self.assertTrue((data/"tasks.db").is_file())
            self.assertEqual(hashlib.sha256((data/"protection.key").read_bytes()).hexdigest(),before["protection.key"])
            self.assertEqual(marker.read_bytes(),b"SYNTHETIC-PROFILE-ONLY")

    @staticmethod
    def backend(executable,data):
        import contextlib
        @contextlib.contextmanager
        def opened():
            bridge=BackendBridge(executable,data)
            try:yield bridge
            finally:bridge.close()
        return opened()
    def test_deb_runs_backend_without_sdk_outside_repository(self):
        package=Path(os.environ.get("STUDY_TASK_HUB_TEST_DEB",ROOT/"artifacts/linux/study-task-hub_1.2.0~ubuntu2_amd64.deb"))
        self.assertTrue(package.is_file(),"Self-contained Debian package is not built")
        with tempfile.TemporaryDirectory(prefix="study-task-hub-package-") as tmp:
            tree=Path(tmp)/"tree"
            subprocess.run(["dpkg-deb","--extract",str(package),str(tree)],check=True)
            source_ui=ROOT/"src/AccessProbe.Linux"
            for source in source_ui.rglob("*.py"):
                packaged=tree/"opt/study-task-hub/ui"/source.relative_to(source_ui)
                self.assertTrue(packaged.is_file(),"Frontend file is missing from the package")
                self.assertEqual(hashlib.sha256(packaged.read_bytes()).digest(),hashlib.sha256(source.read_bytes()).digest(),"Packaged frontend does not match the tested source")
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
            self.assertTrue((tree/"usr/share/applications/io.github.studyTaskHub.desktop").is_file())
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
        package=Path(os.environ.get("STUDY_TASK_HUB_TEST_DEB",ROOT/"artifacts/linux/study-task-hub_1.2.0~ubuntu2_amd64.deb"))
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
