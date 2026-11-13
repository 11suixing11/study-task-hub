import time
from gi.repository import GLib
from .ui import on_result
from .sync_rules import can_sync_tasks

class AppScheduler:
    def __init__(self, bridge, send_notification, clock=time.monotonic, busy=lambda:False):
        self.bridge, self.send_notification, self.clock, self.busy = bridge, send_notification, clock, busy
        self.stopped = False
        self.notifications_busy = False
        self.sync_busy = False
        self.last_notifications = clock() - 60
        self.last_sync = clock() - 1800
        self.claims = []
        self.source = GLib.timeout_add_seconds(60, self.tick)

    def tick(self):
        if self.stopped:
            return GLib.SOURCE_REMOVE
        now = self.clock()
        if not self.notifications_busy and now - self.last_notifications >= 60:
            self.last_notifications = now
            self.notifications_busy = True
            on_result(self.bridge.request_async("take-notifications"), self.deliver, lambda _: self.notification_done())
        if not self.sync_busy and not self.busy() and now - self.last_sync >= 1800:
            self.last_sync = now
            self.sync_busy = True
            on_result(self.bridge.request_async("get-settings"), self.settings_loaded, lambda _: self.sync_done())
        return GLib.SOURCE_CONTINUE

    def notification_done(self, *_):
        self.notifications_busy = False
        self.claims = []

    def deliver(self, items):
        self.claims = [item["id"] for item in items]
        if self.stopped:
            self.bridge.request_async("release-notifications", {"ids":self.claims})
            self.notification_done()
            return
        if not items:
            self.notification_done()
            return
        outcomes = {True:[], False:[]}
        for item in items:
            try:
                success = bool(self.send_notification(item))
            except Exception:
                success = False
            outcomes[success].append(item["id"])
        remaining = sum(bool(ids) for ids in outcomes.values())
        def complete(_):
            nonlocal remaining
            remaining -= 1
            if remaining == 0:
                self.notification_done()
        for success, ids in outcomes.items():
            if ids:
                on_result(self.bridge.request_async("ack-notifications", {"ids":ids,"success":success}), complete, complete)

    def settings_loaded(self, settings):
        if self.stopped or self.busy() or not settings["automaticSyncEnabled"]:
            self.sync_done()
            return
        on_result(self.bridge.request_async("platforms"), self.platforms_loaded, lambda _: self.sync_done())

    def platforms_loaded(self, views):
        if not self.stopped and not self.busy():
            for view in views:
                if can_sync_tasks(view):
                    self.bridge.request_async("start-sync", {"platform":view["id"],"catalogOnly":False})
        self.sync_done()

    def sync_done(self, *_):
        self.sync_busy = False

    def stop(self):
        if self.stopped:
            return
        self.stopped = True
        if self.source:
            GLib.source_remove(self.source)
            self.source = None
        if self.claims:
            self.bridge.request_async("release-notifications", {"ids":self.claims})
            self.claims = []
