import datetime
import unittest
import importlib.util
from linux_ui_helpers import TestAppFixture

class SchedulerChecks(unittest.TestCase):
    def test_official_course_sync_qualification(self):
        import copy
        from study_task_hub.sync_rules import can_sync_tasks
        with TestAppFixture() as fx:
            view=next(v for v in fx.request("platforms") if v["id"]=="chaoxing")
            view["session"]["canReuse"]=True
            view["recording"].update(selectionConfirmed=True,recordAllCourses=False,selectedCourseKeys=["official"])
            view["recording"]["courses"]=[{"key":"official","name":"高等数学","detail":"当前课程","available":True}]
            self.assertTrue(can_sync_tasks(view))
            for label,changes,want in [
                ("unselected",{"selectedCourseKeys":[]},False),
                ("record all",{"recordAllCourses":True,"selectedCourseKeys":[]},True),
                ("not reviewed",{"selectionConfirmed":False},False),
                ("history",{"courses":[{"key":"official","name":"历史课程","detail":"已结课","available":False}]},False)]:
                with self.subTest(label=label):
                    candidate=copy.deepcopy(view);candidate["recording"].update(changes)
                    self.assertEqual(can_sync_tasks(candidate),want)
            view["session"]["canReuse"]=False
            self.assertFalse(can_sync_tasks(view))
            view["session"]["canReuse"]=True;view["loginUri"]=None
            self.assertFalse(can_sync_tasks(view))
    def test_manual_only_course_selection_does_not_start_official_sync(self):
        import concurrent.futures
        from study_task_hub.scheduler import AppScheduler
        with TestAppFixture() as fx:
            fx.request("capture-session",{"platformId":"chaoxing","pageUri":"https://i.chaoxing.com/","cookies":[
                {"name":"_uid","value":"900001","domain":".chaoxing.com","path":"/","secure":True,"httpOnly":True,"expiresUtc":None,"sameSite":"Strict"}]})
            plan=fx.request("get-course-plan",{"platform":"chaoxing"})
            fx.request("save-course-selection",{"platform":"chaoxing","revision":plan["revision"],"semester":plan["semester"],"keys":[],"customCourses":[{"name":"仅手动课程","selected":True}]})
            attempts=[]
            class BlockOfficialNetwork:
                def request_async(self,command,args=None):
                    if command=="start-sync":
                        attempts.append(args);future=concurrent.futures.Future();future.set_result(None);return future
                    return fx.bridge.request_async(command,args)
            scheduler=AppScheduler(BlockOfficialNetwork(),lambda _:True,clock=lambda:0)
            try:
                scheduler.tick()
                fx.wait(lambda:not scheduler.sync_busy and not scheduler.notifications_busy)
                self.assertEqual(attempts,[],"Automatic synchronization attempted to collect a manual-only platform")
            finally:scheduler.stop()
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
