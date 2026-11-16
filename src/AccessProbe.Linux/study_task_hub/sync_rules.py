def has_recorded_official_courses(recording):
    if not recording["selectionConfirmed"]:
        return False
    selected=set(recording["selectedCourseKeys"])
    return any(course["available"] and (recording["recordAllCourses"] or course["key"] in selected)
        for course in recording["courses"])

def can_sync_tasks(view):
    return bool(view["loginUri"] and view["session"]["canReuse"] and has_recorded_official_courses(view["recording"]))
