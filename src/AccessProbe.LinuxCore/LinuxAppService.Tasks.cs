using System.Globalization;
using System.Text.RegularExpressions;

namespace AccessProbe.Linux;
public sealed partial class LinuxAppService
{
    public TaskPage QueryTasks(TaskQuery query)
    {
        EnsureOpen();
        if(query.Offset<0 || query.Limit is <1 or >100 || query.Sort is not ("priority" or "deadline" or "updated"))
            throw new TaskValidationException("TASK001","筛选或分页参数无效。");
        if(query.Platform is not (null or "" or "all") && !PlatformCatalog.Sites.Any(p=>p.Id==query.Platform))
            throw new TaskValidationException("TASK001","请选择有效平台。");
        TaskState? state=query.State=="all" ? null : ParseState(query.State);
        var search=query.Search??"";
        long version; IReadOnlyList<TaskItem> all; IReadOnlyList<TaskItem> visible;
        do
        {
            version=Interlocked.Read(ref snapshotVersion); all=store.LoadTasks(); visible=store.LoadRecordedTasks();
        } while(version!=Interlocked.Read(ref snapshotVersion));
        var scope=query.IncludeUnselected ? all : visible; var now=utcNow();
        var counts=new DashboardCounts(scope.Count(t=>t.State==TaskState.Pending),scope.Count(t=>t.State==TaskState.Completed),
            scope.Count(t=>t.State==TaskState.Ignored),scope.Count(t=>t.State==TaskState.Returned),scope.Count(t=>t.State==TaskState.NeedsReview),
            scope.Count(t=>TaskRules.IsActive(t)&&TaskRules.HasUsableDeadline(t)&&t.DeadlineUtc>now&&t.DeadlineUtc<=now.AddHours(24)),
            scope.Count(t=>TaskRules.IsActive(t)&&TaskRules.HasUsableDeadline(t)&&t.DeadlineUtc<=now));
        var filtered=scope.Where(t=>(query.Platform is null or "" or "all" || t.PlatformId==query.Platform)
            && (state is null || t.State==state) && (search.Length==0 || new[]{t.Title,t.CourseName,t.Notes}.Any(s=>s.Contains(search,StringComparison.OrdinalIgnoreCase)))).ToArray();
        var rows=filtered.Select(t=>ToView(t,now)).ToArray();
        IOrderedEnumerable<LinuxTaskView> sorted=query.Sort switch
        {
            "deadline"=>rows.OrderBy(t=>t.Task.DeadlineUtc??DateTimeOffset.MaxValue),
            "updated"=>rows.OrderByDescending(t=>t.Task.UpdatedAtUtc),
            _=>rows.OrderBy(t=>t.Priority).ThenBy(t=>t.Task.DeadlineUtc??DateTimeOffset.MaxValue)
        };
        return new(version,query.Offset,scope.Count,filtered.Length,all.Count-visible.Count,counts,
            sorted.ThenBy(t=>t.Task.Id,StringComparer.Ordinal).Skip(query.Offset).Take(query.Limit).ToArray(),
            PlatformCatalog.Sites.ToDictionary(p=>p.Id,p=>scope.Count(t=>t.PlatformId==p.Id)));
    }

    public TaskDetail GetTask(string id) { EnsureOpen(); return ToDetail(store.Get(id)??throw new TaskValidationException("TASK004","任务已不存在，请刷新清单。")); }
    public TaskDetail CreateTask(TaskInput input) { EnsureOpen(); return ToDetail(store.Create(ToDraft(input),utcNow())); }
    public TaskDetail EditTask(string id,int revision,TaskInput input) { EnsureOpen(); return ToDetail(store.Edit(id,revision,ToDraft(input),utcNow())); }
    public TaskDetail SetTaskState(string id,int revision,string state) { EnsureOpen(); return ToDetail(store.SetState(id,revision,ParseState(state),utcNow())); }
    public void DeleteTask(string id,int revision) { EnsureOpen(); store.Delete(id,revision); }
    private static TaskState ParseState(string value) => value switch
    {
        "pending"=>TaskState.Pending,"completed"=>TaskState.Completed,"ignored"=>TaskState.Ignored,
        "returned"=>TaskState.Returned,"needsReview"=>TaskState.NeedsReview,
        _=>throw new TaskValidationException("TASK001","任务状态无效。")
    };
    private static TaskDraft ToDraft(TaskInput input)
    {
        if(input is null || input.Title is null || input.PlatformId is null)throw new TaskValidationException("TASK001","任务字段不完整。");
        var kind=input.DeadlineKind switch {"unknown"=>DeadlineKind.Unknown,"known"=>DeadlineKind.Known,"none"=>DeadlineKind.None,
            _=>throw new TaskValidationException("TIME001","截止时间种类无效。")};
        return new(input.Title,input.PlatformId,input.CourseName??"",kind,ParseDate(input.BeginIso),ParseDate(input.DeadlineIso),
            string.IsNullOrEmpty(input.Link)?null:input.Link,input.ProgressPercent,input.ScoreText??"",input.Notes??"",input.PriorityOverride)
            {ConfirmDeadline=input.ConfirmDeadline};
    }
    private static DateTimeOffset? ParseDate(string? value)
    {
        if(value is null)return null;
        if(!Regex.IsMatch(value,@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1))
            || !DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))
            throw new TaskValidationException("TIME001","日期必须包含明确时区，请核对时间。");
        return date.ToUniversalTime();
    }
    private TaskDetail ToDetail(TaskItem task)=>new(ToView(task,utcNow()),task.Link);
    private LinuxTaskView ToView(TaskItem task,DateTimeOffset now)
    {
        var label=TaskRules.DeadlineName(task);
        if(task.DeadlineKind==DeadlineKind.Known && task.DeadlineUtc is {} date)
            label=(task.DeadlineEvidence==DeadlineEvidence.RemainingTime&&!task.Overrides.HasFlag(TaskOverrides.Deadline)?"约 ":"")
                +TimeZoneInfo.ConvertTime(date,timeZone).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture)
                +(task.DeadlineEvidence==DeadlineEvidence.InferredYear&&!task.Overrides.HasFlag(TaskOverrides.Deadline)?"（年份推定）":"");
        return new(task with {Link=null},TaskRules.StateName(task.State),label,TaskRules.CanComplete(task),TaskRules.Priority(task,now),task.LinkIssue);
    }
}
