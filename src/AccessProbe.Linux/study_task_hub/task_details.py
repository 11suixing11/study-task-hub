import datetime

def display_date(value):
    if not value:return "未记录"
    try:return datetime.datetime.fromisoformat(value.replace("Z","+00:00")).astimezone().strftime("%Y-%m-%d %H:%M:%S %z")
    except ValueError:return value

def format_task_detail(detail):
    view=detail["view"];task=view["task"]
    progress=task.get("progressPercent")
    lines=[task["courseName"] or "未分类课程",f"{view['stateLabel']} · {view['deadlineLabel']}",
        "进度："+(f"{progress:g}%" if isinstance(progress,(int,float)) else str(progress)+"%" if progress is not None else "未提供"),
        "成绩："+(task.get("scoreText") or "未提供"),"开放时间："+display_date(task.get("beginUtc")),"",task["notes"] or "暂无备注"]
    if task["source"]=="manual":lines.extend(["","来源：手动录入"])
    else:
        evidence={"platformDate":"平台完整日期","inferredYear":"年份推定","remainingTime":"平台剩余时长估算","none":"平台未提供"}
        lines.extend(["","平台依据", "原始状态："+(task.get("platformStatusText") or "未提供"),
            "状态依据："+({"explicit":"平台明确状态"}.get(task.get("platformStateEvidence"),task.get("platformStateEvidence") or "未提供")),
            "判定说明："+(task.get("automaticStatusExplanation") or "未提供"),
            "截止依据："+evidence.get(task.get("deadlineEvidence"),"未确认")])
        for field,title in [("platformListTimeText","列表时间"),("displayedTimeText","平台显示时间"),("detailTimeText","详情时间"),("automaticWindowText","自动读取时间范围"),("collectedLearningEvidence","学习进度依据")]:
            if task.get(field):lines.append(title+"："+task[field])
        for field,title in [("collectedAtUtc","采集时间"),("platformListTimeObservedAtUtc","列表观察时间"),("detailCheckedAtUtc","详情核对时间")]:
            if task.get(field):lines.append(title+"："+display_date(task[field]))
        names={"title":"任务名称","course":"课程名称","deadline":"截止时间","link":"官方链接","progress":"进度","priority":"优先级","begin":"开放时间","state":"状态","score":"成绩"}
        overrides=[names[key.strip()] for key in str(task.get("overrides","")).split(",") if key.strip() in names]
        lines.extend(["","人工修改："+("、".join(overrides) if overrides else "无，随平台同步")])
    if view.get("linkIssue"):lines.extend(["",view["linkIssue"]])
    return "\n".join(lines)
