import datetime
import unittest
import importlib.util
from linux_ui_helpers import TestAppFixture

class SchedulerChecks(unittest.TestCase):
    def test_due_notification_is_acknowledged_once_and_stop_cancels_timer(self):
        self.assertIsNotNone(importlib.util.find_spec("study_task_hub.scheduler"), "Scheduler is not implemented")
        from study_task_hub.scheduler import AppScheduler
        with TestAppFixture() as fx:
            fx.request("create-task", {"title":"提醒任务", "platformId":"pta", "courseName":"", "deadlineKind":"known",
                "deadlineIso":(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(hours=10)).isoformat(), "notes":"", "scoreText":""})
            sent=[]
            clock=[0]
            scheduler=AppScheduler(fx.bridge,lambda item: sent.append(item) or True,clock=lambda:clock[0])
            scheduler.tick()
            fx.wait(lambda:len(sent)==1 and not scheduler.notifications_busy)
            clock[0]=61
            scheduler.tick()
            fx.wait(lambda:not scheduler.notifications_busy)
            self.assertEqual(len(sent),1)
            scheduler.stop()
            clock[0]=1900
            scheduler.tick()
            self.assertEqual(len(sent),1)
            self.assertIsNone(scheduler.source)

if __name__=="__main__":unittest.main()
