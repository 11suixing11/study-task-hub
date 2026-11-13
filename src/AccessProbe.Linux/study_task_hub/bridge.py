import concurrent.futures
import json
import subprocess
import threading
import queue
import time

class BackendError(Exception):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)

class BackendBridge:
    def __init__(self, backend_path, data_root):
        self._pending = {}
        self._events = []
        self._lock = threading.RLock()
        self._write_lock = threading.Lock()
        self._next_id = 0
        self._closed = False
        self._outgoing = queue.Queue(maxsize=32)
        self.process = subprocess.Popen([str(backend_path), "--data-root", str(data_root)], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
        self._reader = threading.Thread(target=self._read, daemon=True)
        self._stderr = threading.Thread(target=self._drain, daemon=True)
        self._writer = threading.Thread(target=self._write, daemon=True)
        self._reader.start()
        self._stderr.start()
        self._writer.start()
    def request_async(self, command, args=None):
        future = concurrent.futures.Future()
        with self._lock:
            if self._closed or self.process.poll() is not None:
                future.set_exception(BackendError("IPC004", "后台已断开，请重新启动后台。"))
                return future
            self._next_id += 1
            request_id = self._next_id
            self._pending[request_id] = future
        payload = (json.dumps({"version":1, "id":request_id, "command":command, "args":args or {}}, ensure_ascii=False) + "\n").encode("utf-8")
        if len(payload) > 2 * 1024 * 1024:
            with self._lock:
                self._pending.pop(request_id, None)
            future.set_exception(BackendError("IPC003", "请求超过大小限制。"))
            return future
        try:
            self._outgoing.put_nowait(payload)
        except queue.Full:
            with self._lock:
                self._pending.pop(request_id, None)
            future.set_exception(BackendError("IPC003", "后台请求过多，请等待当前操作后重试。"))
        return future
    def _write(self):
        while True:
            payload = self._outgoing.get()
            if payload is None or self._closed:
                return
            try:
                view = memoryview(payload)
                while view:
                    count = self.process.stdin.write(view)
                    if not count:
                        raise BrokenPipeError
                    view = view[count:]
            except (OSError, ValueError):
                self._fail_all()
                return
    def subscribe_events(self, callback):
        self._events.append(callback)
        def unsubscribe():
            if callback in self._events:
                self._events.remove(callback)
        return unsubscribe
    def _read(self):
        try:
            while line := self.process.stdout.readline(16 * 1024 * 1024 + 2):
                if len(line) > 16 * 1024 * 1024 + 1 or not line.endswith(b"\n"):
                    break
                message = json.loads(line)
                if message.get("version") != 1:
                    break
                if "event" in message:
                    for callback in tuple(self._events):
                        try:
                            callback(message)
                        except Exception:
                            continue
                    continue
                with self._lock:
                    future = self._pending.pop(message.get("id"), None)
                if future and not future.done():
                    if message.get("ok"):
                        future.set_result(message.get("result"))
                    else:
                        error = message.get("error", {})
                        future.set_exception(BackendError(error.get("code", "IPC006"), error.get("message", "后台操作失败。")))
        except (ValueError, OSError):
            pass
        finally:
            self._fail_all()
    def _drain(self):
        try:
            while self.process.stderr.read(8192):
                pass  # Diagnostics are not replayed into UI or logs.
        except (OSError, ValueError):
            pass
    def _fail_all(self):
        with self._lock:
            self._closed = True
            pending, self._pending = self._pending, {}
        for future in pending.values():
            if not future.done():
                future.set_exception(BackendError("IPC004", "后台已断开，请重新启动后台；未保存的输入仍保留。"))
    def close(self, timeout=5):
        deadline = time.monotonic() + timeout
        if self.process.poll() is None:
            try:
                self.request_async("shutdown").result(timeout=max(0, deadline-time.monotonic()))
            except Exception:
                pass
            try:
                self.process.stdin.close()
                self.process.wait(timeout=max(0, deadline-time.monotonic()))
            except (subprocess.TimeoutExpired, OSError, ValueError):
                self.process.terminate()
                try:
                    self.process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait()
        self._fail_all()
        try:
            self._outgoing.put_nowait(None)
        except queue.Full:
            pass
        self._writer.join(timeout=1)
        self._reader.join(timeout=1)
        self._stderr.join(timeout=1)
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            stream.close()
