using Android.Widget;
using AccessProbe;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private Task mobileTaskEditorLoad = Task.CompletedTask;

    private void EditTaskDeadline(string id)
    {
        taskEditorOpen = true; SetMobileNotificationEditing(true);
        Layout("确认截止时间", "正在读取任务…", () => TaskDetail(id));
        var expectedRevision = viewRevision; var originPage = page; var currentStore = store;
        mobileTaskEditorLoad = LoadEditor();
        async Task LoadEditor()
        {
            try
            {
                var task = await Task.Run(() => currentStore.Get(id));
                if (!alive || viewRevision != expectedRevision || !ReferenceEquals(page, originPage)) return;
                if (task is null) { Dashboard(); status.Text = "任务已更新或删除，请在清单中查看。"; return; }
                BuildDeadlineEditor(task);
            }
            catch (Exception error)
            {
                if (!alive || viewRevision != expectedRevision || !ReferenceEquals(page, originPage)) return;
                status.Text = error is TaskValidationException validation ? validation.Message : "任务暂时无法读取，请返回后重试。";
            }
        }
    }

    private void BuildDeadlineEditor(TaskItem task)
    {
        var id = task.Id;
        taskEditorOpen = true; SetMobileNotificationEditing(true); Layout("确认截止时间", task.Title, () => TaskDetail(id));
        Text(page, TaskRules.DeadlineName(task) + " · " + TaskRules.Confidence(task), 13);
        var kind = new Spinner(this); kind.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, ["待核对", "明确截止时间", "无截止时间"]);
        kind.SetSelection((int)task.DeadlineKind); page.AddView(kind);
        var text = new EditText(this) { Hint = "yyyy-MM-dd HH:mm", Text = task.DeadlineUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "" }; page.AddView(text);
        Text(page, "填写手机本地时间。保存明确时间后，才会安排截止提醒。", 13);
        Button(page, "保存确认", () =>
        {
            try
            {
                var deadlineKind = (DeadlineKind)Math.Max(0, kind.SelectedItemPosition);
                var date = deadlineKind == DeadlineKind.Known ? TaskRules.ParseEditedLocalTime(text.Text ?? "", task.DeadlineUtc) : null;
                var draft = new TaskDraft(task.Title, task.PlatformId, task.CourseName, deadlineKind, task.BeginUtc, date,
                    task.Link, task.ProgressPercent, task.ScoreText, task.Notes, task.PriorityOverride) { ConfirmDeadline = true };
                var currentStore = store;
                mobileTaskWrite = SaveMobileTaskAsync(() => currentStore.Edit(id, task.Revision, draft, DateTimeOffset.UtcNow),
                    _ => TaskDetail(id, "截止时间已确认。"), "正在保存截止时间…");
            }
            catch (TaskValidationException error) { status.Text = error.Message; }
        });
    }
}
