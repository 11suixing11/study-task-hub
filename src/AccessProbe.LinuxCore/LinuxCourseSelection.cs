namespace AccessProbe.Linux;

public sealed record AcademicSemester(int YearStart,string Term)
{
    public string Label=>$"{YearStart}-{YearStart+1}学年{(Term=="autumn"?"上学期":"下学期")}";
    public static AcademicSemester Current(DateTimeOffset now)
    {
        var date=now.ToOffset(TimeSpan.FromHours(8));
        return date.Month>=9?new(date.Year,"autumn"):date.Month==1?new(date.Year-1,"autumn"):new(date.Year-1,"spring");
    }
    public void Validate()
    {if(YearStart is <1980 or >2100||Term is not("autumn" or "spring"))throw new TaskValidationException("COURSE001","请选择有效学年与上下学期。");}
}
public sealed record CourseSuggestion(RecordingCourse Course,bool Preselected,string Group,string Reason,int LocalPending);
public sealed record CourseSelectionPlan(AcademicSemester Semester,long Revision,bool CatalogComplete,string CatalogMessage,IReadOnlyList<CourseSuggestion> Courses);
public sealed record CustomCourseInput(string Name,bool Selected=true);
public sealed record ReviewedCourseSelection(string Platform,long Revision,AcademicSemester Semester,string[] Keys,CustomCourseInput[] CustomCourses)
{ public string[]? ReviewedKeys {get;init;} }
internal sealed record LinuxCoursePreference(AcademicSemester Semester,string[] ReviewedKeys,string[]? SelectedKeys=null,bool RecordAll=false);
public sealed partial class LinuxAppService
{
    public CourseSelectionPlan GetCourseSelectionPlan(string platform,AcademicSemester? semester=null)
    {
        EnsureOpen();semester??=AcademicSemester.Current(utcNow());semester.Validate();
        var (recording,preference,tasks)=store.LoadLinuxCourseSelection(platform,semester);
        var same=preference is not null?preference.Semester==semester:semester==AcademicSemester.Current(utcNow());
        var reviewed=(preference?.ReviewedKeys??recording.Courses.Select(c=>c.Key).ToArray()).ToHashSet(StringComparer.Ordinal);
        var counts=tasks.Where(TaskRules.IsActive).GroupBy(CourseRecordingRules.TaskCourseKey).ToDictionary(g=>g.Key,g=>g.Count());
        var suggestions=recording.Courses.Select(course=>
        {
            var pending=counts.GetValueOrDefault(course.Key);
            var (group,selected,reason)=CourseSelectionRules.Suggest(course,semester,pending);
            if(same&&(preference?.SelectedKeys is not null||recording.SelectionConfirmed))
            {
                var selectedKeys=preference?.SelectedKeys??recording.SelectedCourseKeys;
                var recordAll=preference?.RecordAll??recording.RecordAllCourses;
                if(recordAll||selectedKeys.Contains(course.Key,StringComparer.Ordinal))
                {selected=true;group="selected";reason="保留已保存选择 · "+reason;}
                else if(reviewed.Contains(course.Key)){selected=false;reason="本学期已手动排除 · "+reason;}
            }
            return new CourseSuggestion(course,selected,group,reason,pending);
        }).OrderBy(c=>CourseSelectionRules.Order(c.Group)).ThenBy(c=>c.Course.Name,StringComparer.CurrentCulture).ToArray();
        return new(semester,recording.Revision,recording.CatalogComplete,CourseRecordingRules.CatalogRefreshText(recording),suggestions);
    }
    public CourseRecordingSettings SaveReviewedCourseSelection(ReviewedCourseSelection input)
    {EnsureOpen();return store.SaveLinuxCourseSelection(input,utcNow());}
}

internal static class CourseSelectionRules
{
    public static int Order(string group)=>group switch{"current"=>0,"selected"=>1,"review"=>2,"manual"=>3,_=>4};
    public static (string,bool,string) Suggest(RecordingCourse course,AcademicSemester semester,int pending)
    {
        var manual=course.Detail.StartsWith("自定义 · ",StringComparison.Ordinal);
        var nameHint=ParseSemester(course.Name);var detailHint=ParseSemester(course.Detail);
        var hint=manual?detailHint:nameHint??detailHint;
        var closed=course.Detail.Contains("已结课",StringComparison.Ordinal)||course.Detail.Contains("已结束",StringComparison.Ordinal);
        if(!manual&&!closed&&course.Available&&nameHint is not null&&detailHint is not null&&nameHint!=detailHint)
            return("review",true,"名称与说明的学期不一致，预勾供审查");
        if(closed||hint is not null&&hint!=semester||!course.Available&&!manual)
        {
            var reason=closed?"官网显示已结课":hint is not null&&hint!=semester?$"属于{hint.Label}":"历史记录，当前目录不可用";
            return pending>0?("review",true,reason+$" · 本地仍有 {pending} 条待办，请审查"):("history",false,reason);
        }
        if(manual)return("manual",pending>0,"自定义手动课程"+(hint is not null?$" · {hint.Label}":""));
        if(hint==semester)return("current",true,$"匹配{semester.Label}");
        if(course.Detail.Contains("正在上课",StringComparison.Ordinal)||course.Detail.Contains("进行中",StringComparison.Ordinal))
            return("current",true,"官网显示正在上课，学期未标明");
        if(course.Detail.Contains("即将开课",StringComparison.Ordinal))return("review",false,"官网显示即将开课，请审查学期");
        return("review",true,"平台未标明学期，预勾供审查");
    }
    private static AcademicSemester? ParseSemester(string text)
    {
        const System.Text.RegularExpressions.RegexOptions options=System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        var timeout=TimeSpan.FromSeconds(1);
        bool autumn=System.Text.RegularExpressions.Regex.IsMatch(text,@"上学期|第一学期|第1学期|秋",options,timeout);
        bool spring=System.Text.RegularExpressions.Regex.IsMatch(text,@"下学期|第二学期|第2学期|春",options,timeout);
        if(autumn==spring)return null;
        var range=System.Text.RegularExpressions.Regex.Match(text,@"(?<!\d)(?<start>\d{4})\s*[-–—/至]\s*(?<end>\d{4})(?!\d)",options,timeout);
        if(range.Success)
        {
            var start=int.Parse(range.Groups["start"].Value);
            return start is >=1980 and <=2100&&int.Parse(range.Groups["end"].Value)==start+1?new(start,autumn?"autumn":"spring"):null;
        }
        var single=System.Text.RegularExpressions.Regex.Match(text,@"(?<!\d)(?<year>\d{4})\s*(?:年|学年)?\s*(?<term>秋|春|上学期|下学期|第一学期|第二学期|第[12]学期)",options,timeout);
        if(!single.Success)return null;
        if(single.Groups["term"].Value is not("春" or "秋")&&!text.Contains("学年",StringComparison.Ordinal))return null;
        var year=int.Parse(single.Groups["year"].Value);
        if(year is <1980 or >2100)return null;
        return new(single.Groups["term"].Value.StartsWith("春",StringComparison.Ordinal)?year-1:year,autumn?"autumn":"spring");
    }
}
