using AccessProbe;
using AccessProbe.Linux;
namespace LinuxChecks;
static class CourseSelectionChecks
{
    public static Task RunAsync()
    {
        foreach(var (date,year,term) in new[]{("2026-09-01T00:00:00+08:00",2026,"autumn"),("2027-01-31T15:59:00Z",2026,"autumn"),
            ("2027-01-31T16:00:00Z",2026,"spring"),("2026-08-31T23:59:00+08:00",2025,"spring")})
        {
            var period=AcademicSemester.Current(DateTimeOffset.Parse(date));
            Check.Equal(year,period.YearStart);Check.Equal(term,period.Term);
        }
        using var root=new TestRoot();var now=DateTimeOffset.Parse("2026-11-13T12:00:00+08:00");
        using var service=new LinuxAppService(root.Path,()=>now);
        using var protector=new LinuxDataProtector(root.Path);
        var store=new TaskStore(Path.Combine(root.Path,"tasks.db"),protector);
        var courses=new[]{Course("1","2026-2027第一学期 高等数学","官方课程"),Course("2","2026-2027第二学期 大学物理","官方课程"),
            Course("3","大学英语","正在上课"),Course("4","公共课程","班级 1"),Course("5","历史课程","已结课")};
        store.ObserveRecordingCourses("pta",courses.Select(c=>new DiscoveredRecordingCourse(c,_=>false)).ToArray(),true,now);
        var plan=service.GetCourseSelectionPlan("pta");
        Check.Equal(5,plan.Courses.Count);Check.Equal(new AcademicSemester(2026,"autumn"),plan.Semester);
        bool Selected(string key)=>plan.Courses.Single(c=>c.Course.Key==key).Preselected;
        Check.True(Selected(courses[0].Key));Check.True(!Selected(courses[1].Key));Check.True(Selected(courses[2].Key));
        Check.True(Selected(courses[3].Key));Check.True(!Selected(courses[4].Key));
        Check.Equal("review",plan.Courses.Single(c=>c.Course.Key==courses[3].Key).Group);
        Check.True(!service.GetCourses("pta").SelectionConfirmed,"Reading recommendations must not confirm or persist selection");
        var saved=service.SaveReviewedCourseSelection(new("pta",plan.Revision,plan.Semester,[courses[2].Key],[new("政治经济学")]));
        Check.True(saved.SelectionConfirmed);Check.Equal(2,saved.SelectedCourseKeys.Count);
        Check.Equal(0,service.QueryTasks(new()).Total);
        Check.Equal(6,service.GetCourses("pta").Courses.Count);
        Check.Throws<TaskValidationException>(()=>service.SaveReviewedCourseSelection(new("pta",plan.Revision,plan.Semester,[],[new("不应写入的课程")])));
        Check.Equal(6,service.GetCourses("pta").Courses.Count);
        plan=service.GetCourseSelectionPlan("pta");
        Check.True(!Selected(courses[0].Key),"Explicit exclusions must survive same-semester reload");
        Check.True(Selected(courses[2].Key));
        var custom=plan.Courses.Single(c=>c.Course.Name=="政治经济学");
        Check.True(custom.Preselected);Check.True(!custom.Course.Available,"Manual course must not claim an official collection route");
        var newCourse=Course("6","2026年秋季 思想政治","官网课程");
        store.ObserveRecordingCourses("pta",courses.Append(newCourse).Select(c=>new DiscoveredRecordingCourse(c,_=>false)).ToArray(),true,now);
        plan=service.GetCourseSelectionPlan("pta");Check.True(Selected(newCourse.Key));Check.True(!Selected(courses[0].Key));
        var spring=service.GetCourseSelectionPlan("pta",new(2026,"spring"));
        Check.True(spring.Courses.Single(c=>c.Course.Key==courses[1].Key).Preselected);
        Check.True(!spring.Courses.Single(c=>c.Course.Key==courses[0].Key).Preselected);
        Check.Throws<TaskValidationException>(()=>service.GetCourseSelectionPlan("pta",new(2026,"summer")));
        Check.Throws<TaskValidationException>(()=>service.SaveReviewedCourseSelection(new("pta",plan.Revision,plan.Semester,[],[new("")])));
        var ambiguous=Course("7","2026-2027第一学期 名称","2026-2027第二学期 说明");
        store.ObserveRecordingCourses("pta",courses.Append(newCourse).Append(ambiguous).Select(c=>new DiscoveredRecordingCourse(c,_=>false)).ToArray(),true,now);
        plan=service.GetCourseSelectionPlan("pta");
        Check.Equal("review",plan.Courses.Single(c=>c.Course.Key==ambiguous.Key).Group);
        var savedCustom=service.SaveReviewedCourseSelection(new("pta",plan.Revision,plan.Semester,[courses[2].Key],[new("2020年春季复习",false)]));
        Check.Equal("manual",service.GetCourseSelectionPlan("pta").Courses.Single(c=>c.Course.Name=="2020年春季复习").Group);
        var visible=service.GetCourseSelectionPlan("pta");
        var unseen=Course("8","2026秋季 新发现课程","官方课程");
        store.ObserveRecordingCourses("pta",courses.Append(newCourse).Append(ambiguous).Append(unseen).Select(c=>new DiscoveredRecordingCourse(c,_=>false)).ToArray(),true,now);
        service.SaveReviewedCourseSelection(new("pta",visible.Revision,visible.Semester,[courses[2].Key],[]){ReviewedKeys=visible.Courses.Select(c=>c.Course.Key).ToArray()});
        Check.True(service.GetCourseSelectionPlan("pta").Courses.Single(c=>c.Course.Key==unseen.Key).Preselected,"Unseen discovery was incorrectly remembered as an explicit exclusion");
        Check.True(!service.GetCourses("pta").SelectedCourseKeys.Contains(unseen.Key),"Recommendation was persisted without confirmation");
        return Task.CompletedTask;
    }
    private static RecordingCourse Course(string id,string name,string detail)=>new(CourseRecordingRules.MetadataKey("pta","synthetic-account",id,"class"),name,detail);
}
