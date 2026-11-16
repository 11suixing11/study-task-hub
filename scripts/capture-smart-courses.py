from pathlib import Path
import datetime
import json
import sqlite3
import sys
import time

root=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/"tests"))
from linux_ui_helpers import TestAppFixture,run_until
from linux_display_validation import save_widget
from study_task_hub.course_picker import CoursePicker
from gi.repository import Adw

output=root/"artifacts/linux/verification/smart-courses"
output.mkdir(parents=True,exist_ok=True)
style=Adw.StyleManager.get_default();original=style.get_color_scheme()
style.set_color_scheme(Adw.ColorScheme.FORCE_DARK)
try:
    with TestAppFixture() as fx:
        now=datetime.datetime.now(datetime.timezone.utc).isoformat()
        courses=[{"Key":"pta:"+format(index,"x")*64,"Name":name,"Detail":detail,"Available":True} for index,name,detail in
            ((1,"2026-2027学年第一学期 高等数学","本校本科课程"),(2,"2026-2027学年第一学期 中国近现代史纲要","本校本科课程"),
             (3,"大学英语 III","正在上课"),(4,"通识选修课","平台未注明学期"),(5,"2026-2027学年第二学期 大学物理","本校本科课程"),
             (6,"2025-2026学年第二学期 线性代数","历史开课期"),(7,"计算机基础","已结课"))]
        fx.request("get-courses",{"platform":"pta"})
        value={"Revision":0,"SelectionConfirmed":False,"RecordAllCourses":False,"HideUnselectedTasks":True,"SelectedCourseKeys":[],"Courses":courses,
            "CatalogComplete":True,"CatalogCheckedAtUtc":now,"LastCatalogRefresh":{"CheckedAtUtc":now,"Outcome":"completed","ProblemCodes":[]}}
        with sqlite3.connect(fx.temp.name+"/tasks.db") as connection:
            connection.execute("INSERT OR REPLACE INTO settings(key,value) VALUES(?,?)",("course-recording:pta",json.dumps(value,ensure_ascii=False)))
        picker=CoursePicker(fx.window,fx.bridge,"pta")
        picker.set_default_size(760,900)
        picker.present();fx.wait(lambda:len(picker.choices)==7)
        picker.custom_name_entry.set_text("政治经济学（手动补充）");picker.add_custom_button.emit("clicked")
        until=time.monotonic()+0.25;run_until(lambda:time.monotonic()>until)
        save_widget(picker,output/"semester-recommendations.png")
        print(picker.semester["label"],picker.summary.get_text(),picker.get_width(),picker.get_height())
        picker.close()
finally:
    style.set_color_scheme(original)
