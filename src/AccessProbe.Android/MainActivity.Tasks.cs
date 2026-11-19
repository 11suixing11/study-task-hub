using AccessProbe;
using Android.Widget;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private Task mobileTaskDetailLoad = Task.CompletedTask;
    private Task mobileTaskWrite = Task.CompletedTask;
    private bool mobileTaskSaving;
    private string? mobileShownTaskId;

    private void TaskDetail(string id, string? completionMessage = null)
    {
        mobileShownTaskId = null;
        taskEditorOpen = true; SetMobileNotificationEditing(false);
        Layout("任务详情", "正在读取本机任务…", Dashboard);
        const string loadingMessage = "正在读取任务…";
        status.Text = completionMessage ?? loadingMessage;
        var currentStore = store; var expectedRevision = viewRevision; var originPage = page;
        mobileTaskDetailLoad = LoadDetail();
        async Task LoadDetail()
        {
            try
            {
                var task = await Task.Run(() => currentStore.Get(id));
                if (!alive || viewRevision != expectedRevision || !ReferenceEquals(page, originPage)) return;
                if (task is null) { Dashboard(); status.Text = "任务已更新或删除，请在清单中查看最新任务。"; return; }
                var message = status.Text;
                BuildTaskDetail(task);
                if (message != loadingMessage) status.Text = message;
            }
            catch (Exception error)
            {
                if (!alive || viewRevision != expectedRevision || !ReferenceEquals(page, originPage)) return;
                status.Text = error is TaskValidationException validation ? validation.Message : "任务暂时无法读取，请返回清单后重试。";
                Button(page, "重新读取任务", () => TaskDetail(id), false);
            }
        }
    }

    private void BuildTaskDetail(TaskItem task)
    {
        var id = task.Id;
        taskEditorOpen = true; SetMobileNotificationEditing(false);
        Layout(task.Title, PlatformCatalog.Name(task.PlatformId) + " · " + task.CourseName, Dashboard);
        mobileShownTaskId = task.Id;
        var info = Card(page); Text(info, TaskRules.StateName(task) + " · " + Deadline(task), 17, true); Text(info, task.PlatformStatusText, 14);
        Text(info, task.AutomaticStatusExplanation, 14); Text(info, task.DisplayedTimeText, 13); Text(info, task.Notes, 14);
        if (TaskRules.HasPlatformAttention(task))
        {
            Text(info, "官网要求已变化，你的人工状态已保留。请核对是否需要重做。", 14, true);
            Button(info, "已核对官网要求", () =>
            {
                var currentStore = store;
                mobileTaskWrite = SaveMobileTaskAsync(() => currentStore.AcknowledgePlatformAttention(task.Id, task.Revision, DateTimeOffset.UtcNow),
                    _ => TaskDetail(id), "正在保存官网核对结果…");
            }, false);
        }
        Text(info, "优先打开官方 App；无法直达时进入 App 首页，并保留网页入口。", 12);
        Button(info, "打开官方任务", async () => await OpenTask(id, true));
        Button(info, "打开任务网页", async () => await OpenTask(id, false), false);
        Button(info, "打开官方课程入口", async () => await OpenCourseFallback(id), false);
        Button(info, "刷新任务状态", async () => await RefreshTask(id), false);
        Button(info, "修改或确认截止时间", () => EditTaskDeadline(id), false);
        foreach (var state in new[] {TaskState.Pending, TaskState.Completed, TaskState.Ignored, TaskState.NeedsReview})
            Button(info, "个人标记为" + StateText(state), () =>
            {
                var currentStore = store;
                mobileTaskWrite = SaveMobileTaskAsync(() => currentStore.SetState(task.Id, task.Revision, state, DateTimeOffset.UtcNow),
                    _ => Dashboard(), "正在保存个人标记…");
            }, false);
    }

    private async Task SaveMobileTaskAsync(Func<TaskItem> write, Action<TaskItem> saved, string progress)
    {
        if (mobileTaskSaving) { status.Text = "任务正在保存，请稍候。"; return; }
        mobileTaskSaving = true;
        var expectedRevision = viewRevision; var originPage = page;
        var wasEditing = mobileNotificationEditing;
        SetMobileNotificationEditing(true); status.Text = progress;
        bool CurrentView() => alive && viewRevision == expectedRevision && ReferenceEquals(page, originPage);
        try
        {
            var task = await Task.Run(write);
            ScheduleReminders();
            if (CurrentView()) { SetMobileNotificationEditing(false); saved(task); }
        }
        catch (Exception error)
        {
            if (CurrentView()) status.Text = error is TaskValidationException validation ? validation.Message : "任务未能保存，请稍后重试。";
        }
        finally
        {
            mobileTaskSaving = false;
            if (CurrentView()) SetMobileNotificationEditing(wasEditing);
            SchedulePendingMobileNotificationTask();
        }
    }

}
