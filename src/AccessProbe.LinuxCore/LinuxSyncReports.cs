using AccessProbe.Mobile;
using System.Text.Json;
namespace AccessProbe.Linux;

public sealed record SyncReportProblem(string Code,string Course,string Message);
public sealed record SyncCourseReport(string Key,string Name,bool Selected,bool Available,int LocalTasks,int ObservedTasks,string Coverage,DateTimeOffset? LastSuccessfulAtUtc);
public sealed record LinuxSyncReport(string Platform,string Stage,string Outcome,DateTimeOffset StartedAtUtc,DateTimeOffset FinishedAtUtc,
    DateTimeOffset? LastSuccessfulAtUtc,bool Complete,int TasksObserved,string Scope,IReadOnlyDictionary<string,string> Metrics,
    IReadOnlyList<SyncReportProblem> Problems,IReadOnlyList<SyncCourseReport> Courses);
public sealed record SyncReportPage(LinuxSyncReport? Catalog,LinuxSyncReport? Tasks);
public sealed partial class LinuxAppService
{
    public SyncReportPage GetSyncReports(string platform){EnsureOpen();return store.LoadLinuxSyncReports(platform);}
    private void RecordSyncReport(string platform,bool catalogOnly,DateTimeOffset started,MobileSyncResult? result,string? outcome=null,SyncReportProblem? failure=null)
    {
        var metrics=new Dictionary<string,string>();var problems=new List<SyncReportProblem>();
        var summary=result?.Summary is {} value?JsonSerializer.SerializeToElement(value):default;
        if(summary.ValueKind==JsonValueKind.Object)
        {
            foreach(var property in summary.EnumerateObject())
                if(property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)metrics[property.Name]=property.Value.ToString();
            if(summary.TryGetProperty("Problems",out var rows))
                foreach(var problem in rows.EnumerateArray())problems.Add(new(problem.GetProperty("Code").GetString()!,problem.GetProperty("Course").GetString()!,problem.GetProperty("Message").GetString()!));
        }
        foreach(var code in result?.Codes??[])if(!problems.Any(p=>p.Code==code))problems.Add(new(code,"",result!.Message));
        if(failure is not null)problems.Add(failure);
        bool Flag(string name)=>summary.ValueKind==JsonValueKind.Object&&summary.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
        int Number(string name,int fallback)=>summary.ValueKind==JsonValueKind.Object&&summary.TryGetProperty(name,out var v)&&v.TryGetInt32(out var n)?n:fallback;
        var complete=outcome is null&&result is not null&&!result.LoginRequired&&result.CatalogCovered&&problems.Count==0;
        if(!catalogOnly)complete&=result?.Summary is not null&&Number("CoursesRead",-1)==Number("SelectedCourses",Number("Courses",-2))
            &&(platform is not("ucampus" or "pta" or "zhihuishu")||Flag("FullTaskListsCovered"))&&(platform!="ucampus"||Flag("LearningUnitListsCovered"));
        var scope=summary.ValueKind==JsonValueKind.Object&&summary.TryGetProperty("CoverageScope",out var scopeValue)?scopeValue.GetString()??"":"已支持的官方课程与任务；特殊课程需核对官网";
        store.SaveLinuxSyncReport(new(platform,catalogOnly?"catalog":"tasks",outcome??(result?.LoginRequired==true?"login_required":complete?"completed":"partial"),started,utcNow(),null,complete,result?.Tasks??0,scope,metrics,problems,[]));
    }
}
