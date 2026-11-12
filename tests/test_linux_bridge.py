import unittest
import tempfile
import os
from linux_test_paths import backend_path
from study_task_hub.bridge import BackendBridge, BackendError

class BridgeChecks(unittest.TestCase):
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
