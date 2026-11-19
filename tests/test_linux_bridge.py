import unittest
import tempfile
import os
import time
from pathlib import Path
from linux_test_paths import backend_path
from study_task_hub.bridge import BackendBridge, BackendError

class BridgeChecks(unittest.TestCase):
    def test_close_deadline_does_not_add_a_second_termination_wait(self):
        with tempfile.TemporaryDirectory(prefix="study-task-hub-exit-") as root:
            script=Path(root)/"stubborn-backend"
            script.write_text("#!/usr/bin/python3\nimport signal,time,sys\nfrom pathlib import Path\nsignal.signal(signal.SIGTERM,lambda *_:None)\nPath(sys.argv[-1], 'ready').write_text('ready')\ntime.sleep(30)\n")
            script.chmod(0o700)
            bridge=BackendBridge(script,root)
            try:
                deadline=time.monotonic()+3
                while not (Path(root)/"ready").exists() and time.monotonic()<deadline:time.sleep(0.005)
                self.assertTrue((Path(root)/"ready").exists())
                started=time.monotonic()
                bridge.close(timeout=0.1)
                self.assertLess(time.monotonic()-started,0.7,"Shutdown exceeded its caller's deadline")
                self.assertIsNotNone(bridge.process.poll())
            finally:
                if bridge.process.poll() is None:bridge.process.kill()
                bridge.close(timeout=0)
    def test_large_request_does_not_block_when_backend_is_not_reading(self):
        with tempfile.TemporaryDirectory(prefix="study-task-hub-pipe-") as root:
            script=Path(root)/"blocked-backend"
            script.write_text("#!/usr/bin/python3\nimport time\ntime.sleep(2)\n",encoding="utf-8")
            script.chmod(0o700)
            bridge=BackendBridge(script, root)
            try:
                started=time.monotonic()
                future=bridge.request_async("capture-session", {"value":"x"*1024*1024})
                self.assertLess(time.monotonic()-started,0.5,"UI request blocked on the subprocess pipe")
                bridge.process.terminate()
                with self.assertRaises(BackendError):
                    future.result(timeout=3)
            finally:
                bridge.close()
    def test_real_backend_roundtrip_persistence_and_error(self):
        with tempfile.TemporaryDirectory(prefix="study-task-hub-check-") as root:
            os.chmod(root, 0o700)
            bridge = BackendBridge(backend_path(), root)
            try:
                self.assertEqual(bridge.request_async("snapshot").result(timeout=5)["total"], 0)
                item = bridge.request_async("create-task", {"title":"桥接任务", "platformId":"chaoxing", "courseName":"数学", "deadlineKind":"unknown", "scoreText":"", "notes":"保留输入"}).result(timeout=5)
                task_id = item["view"]["task"]["id"]
                with self.assertRaises(BackendError) as error:
                    bridge.request_async("unknown").result(timeout=5)
                self.assertEqual(error.exception.code, "IPC002")
            finally:
                bridge.close()
            restarted = BackendBridge(backend_path(), root)
            try:
                self.assertEqual(restarted.request_async("get-task", {"id":task_id}).result(timeout=5)["view"]["task"]["notes"], "保留输入")
                restarted.process.terminate()
                restarted.process.wait(timeout=5)
                with self.assertRaises(BackendError):
                    restarted.request_async("snapshot").result(timeout=5)
            finally:
                restarted.close()

if __name__ == "__main__":
    unittest.main()
