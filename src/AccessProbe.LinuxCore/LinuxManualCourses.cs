namespace AccessProbe.Linux;

public sealed record ManualCourse(string Key,string Name,AcademicSemester Semester,bool Archived=false,int TaskCount=0);
public sealed record ManualCoursePage(long Revision,IReadOnlyList<ManualCourse> Courses);
public sealed record ManualCourseEdit(string Platform,string Key,long Revision,string Name,AcademicSemester Semester,bool Archived);
public sealed partial class LinuxAppService
{
    public ManualCoursePage GetManualCourses(string platform){EnsureOpen();return store.LoadLinuxManualCourses(platform,utcNow());}
    public ManualCoursePage UpdateManualCourse(ManualCourseEdit input){EnsureOpen();return store.EditLinuxManualCourse(input,utcNow());}
    public ManualCoursePage RemoveManualCourse(string platform,string key,long revision){EnsureOpen();return store.RemoveLinuxManualCourse(platform,key,revision,utcNow());}
}
