using AccessProbe;
using System.Security.Cryptography;
using System.Text;

public static class ChaoxingIndependentSyncChecks
{
    private static int checks;
    public static async Task RunAsync(string group="all")
    {
        if(group is not("all" or "diagnostics" or "independent"))throw new ArgumentException("Unknown regression group.");
        checks=0;var failures=new List<string>();var cases=0;
        foreach(var (name,category,test) in new(string,string,Func<Task>)[]
        {
            ("rejected_exam_reports_partial","diagnostics",RejectedExamReportsPartial),
            ("duplicate_exams_count_once","diagnostics",DuplicateExamsCountOnce),
            ("missing_work_signature_keeps_exams","independent",MissingWorkSignatureKeepsExams),
            ("unknown_work_list_keeps_exams","independent",UnknownWorkListKeepsExams),
            ("exam_failure_keeps_works","independent",ExamFailureKeepsWorks),
            ("normal_and_repeat_keep_metadata","independent",NormalAndRepeatKeepMetadata),
            ("login_failure_stops_exam_request","independent",LoginFailureStopsExamRequest),
            ("rate_limit_stops_exam_request","independent",RateLimitStopsExamRequest),
            ("cancellation_stops_exam_request","independent",CancellationStopsExamRequest)
        })
        {
            if(group!="all"&&group!=category)continue;cases++;
            try{await test();Console.WriteLine("PASS: "+name);}
            catch(Exception error){failures.Add(name+": "+error.Message);Console.Error.WriteLine("FAIL: "+failures[^1]);}
        }
        if(failures.Count>0)throw new InvalidDataException(string.Join(Environment.NewLine,failures));
        Console.WriteLine($"PASS: {cases} independent work/exam cases, {checks} assertions; synthetic data only, no official network or production data.");
    }
    private static void Check(bool value,string message){checks++;if(!value)throw new InvalidDataException(message);}

    private static async Task RejectedExamReportsPartial()
    {
        using var fixture=new Fixture("foreign-exam-class",emptyWorks:true);var summary=await fixture.Run();
        Check(summary.ExamsAdded==0&&summary.ExamsUnresolved==1,"The rejected exam must remain unsaved and counted as unresolved.");
        Check(summary.Tasks==0,"A rejected exam was counted as a saved task.");
        Check(summary.Problems.Any(p=>p.Code=="SYNC006"),"The rejected exam did not report incomplete synchronization.");
        Check(fixture.Store.LoadTasks().Count==0,"Foreign-course exam was saved.");
    }
    private static async Task DuplicateExamsCountOnce()
    {
        using var fixture=new Fixture("duplicate-exams",emptyWorks:true);var summary=await fixture.Run();
        Check(summary.ExamsAdded==1&&summary.ExamsUpdated==0,"Duplicate rows should import one exam.");
        Check(summary.Tasks==1&&fixture.Store.LoadTasks().Count==1,"Duplicate rows inflated the task count.");
        Check(summary.Problems.Count==0,"Duplicate copies of a confirmed exam are not an incomplete list.");
    }
    private static async Task MissingWorkSignatureKeepsExams()
    {
        using var fixture=new Fixture("missing-work-signature");var summary=await fixture.Run();
        Check(summary.ExamsAdded==1,"A missing work signature prevented the valid exam from being collected.");
        Check(summary.Tasks==1&&summary.Added==0,"Only the valid exam should be saved.");
        Check(summary.Problems.Any(p=>p.Code=="SYNC005"&&p.Message.Contains("作业")),"The work failure was not reported.");
        Check(fixture.Store.LoadTasks().Single().Title=="合成考试","The exam was not persisted.");
        Check(fixture.Reader.Addresses.Count(a=>a.AbsolutePath=="/visit/stucoursemiddle")==1,"The shared course page was requested twice.");
    }
    private static async Task UnknownWorkListKeepsExams()
    {
        using var fixture=new Fixture("unknown-work-list");var summary=await fixture.Run();
        Check(summary.ExamsAdded==1&&summary.Tasks==1,"An unrecognized work list prevented exam collection.");
        Check(summary.Problems.Any(p=>p.Code=="SYNC006"&&p.Message.Contains("作业")),"The work schema failure was not reported.");
        Check(fixture.Store.LoadTasks().Single().PlatformStatusText=="未开始","Exam platform state was not preserved.");
    }
    private static async Task ExamFailureKeepsWorks()
    {
        foreach(var mode in new[]{"missing-exam-signature","unknown-exam-list"})
        {
            using var fixture=new Fixture(mode);var summary=await fixture.Run();
            Check(summary.Added==1&&summary.ExamsAdded==0&&summary.Tasks==1,"An exam failure destroyed a successfully collected work.");
            Check(summary.Problems.Any(p=>p.Message.Contains("考试")),"The exam failure was not reported.");
            Check(fixture.Store.LoadTasks().Single().Title=="合成作业","The work was not persisted.");
        }
    }
    private static async Task NormalAndRepeatKeepMetadata()
    {
        using var fixture=new Fixture("normal");var summary=await fixture.Run();
        Check(summary.Added==1&&summary.ExamsAdded==1&&summary.Tasks==2&&summary.Problems.Count==0,"Normal synchronization did not collect both task types.");
        var tasks=fixture.Store.LoadTasks();var exam=tasks.Single(t=>t.Title=="合成考试");
        Check(exam.DeadlineKind==DeadlineKind.Unknown&&exam.DeadlineUtc is null&&exam.State==TaskState.Pending,"Exam metadata was guessed or changed.");
        var work=tasks.Single(t=>t.Title=="合成作业");
        Check(work.DeadlineEvidence==DeadlineEvidence.RemainingTime&&work.DeadlineUtc is not null,"Work date evidence was lost.");
        fixture.Store.SetState(exam.Id,exam.Revision,TaskState.Completed,DateTimeOffset.UtcNow);
        var repeated=await fixture.Run();
        Check(repeated.Added==0&&repeated.ExamsAdded==0&&repeated.Updated==1&&repeated.ExamsUpdated==1&&repeated.Tasks==2,"Repeat synchronization did not update the same tasks.");
        Check(fixture.Store.Get(exam.Id)!.State==TaskState.Completed,"Manual exam status was overwritten.");
    }
    private static async Task LoginFailureStopsExamRequest()
    {
        using var fixture=new Fixture("work-login-required");var summary=await fixture.Run();
        Check(summary.LoginRequired&&summary.Problems.Any(p=>p.Code=="SYNC001"),"Login failure was not reported.");
        Check(!fixture.Reader.Addresses.Any(a=>a.AbsolutePath==ChaoxingExamParser.ListPath),"Reading continued after login became invalid.");
    }
    private static async Task RateLimitStopsExamRequest()
    {
        using var fixture=new Fixture("work-rate-limited");var summary=await fixture.Run();
        Check(summary.Problems.Any(p=>p.Code=="SYNC008"),"Rate limiting was not reported.");
        Check(!fixture.Reader.Addresses.Any(a=>a.AbsolutePath==ChaoxingExamParser.ListPath),"Reading continued after the platform requested a pause.");
    }
    private static async Task CancellationStopsExamRequest()
    {
        using var fixture=new Fixture("cancel-work");using var cancellation=new CancellationTokenSource();
        var run=fixture.Run(cancellation.Token);await fixture.Reader.WorkEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));cancellation.Cancel();
        try{await run;throw new InvalidDataException("Cancellation was swallowed.");}
        catch(OperationCanceledException){Check(true,"Cancellation propagated.");}
        Check(!fixture.Reader.Addresses.Any(a=>a.AbsolutePath==ChaoxingExamParser.ListPath),"Reading continued after cancellation.");
    }

    private sealed class Fixture:IDisposable
    {
        private readonly string root=Directory.CreateTempSubdirectory("chaoxing-independent-sync-").FullName;
        private readonly FixtureProtector protector=new();
        public TaskStore Store{get;}
        public FixtureReader Reader{get;}
        public Fixture(string mode,bool emptyWorks=false)
        {
            Store=new(Path.Combine(root,"tasks.db"),protector);
            var settings=Store.LoadCourseRecording("chaoxing");Store.SaveCourseRecording("chaoxing",true,[],true,settings.Revision,DateTimeOffset.UtcNow);
            Reader=new(mode,emptyWorks);
        }
        public Task<ChaoxingSyncSummary> Run(CancellationToken token=default)=>new ChaoxingSynchronizer(Store,Reader,TimeSpan.Zero).RunAsync(null,token);
        public void Dispose(){protector.Dispose();Directory.Delete(root,true);}
    }
    private sealed class FixtureReader(string mode,bool emptyWorks):IChaoxingPageReader
    {
        public List<Uri> Addresses{get;}=[];
        public TaskCompletionSource WorkEntered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChaoxingPage> ReadAsync(Uri address,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Addresses.Add(address);string html;var resultAddress=address;
            if(address.AbsolutePath=="/base")html="<input id='fid' value='901'>";
            else if(address.AbsolutePath.Contains("courselistdata"))html="<div class='course-list'><div class='course'><a href='https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid=101&amp;clazzid=201&amp;cpi=301'><span class='course-name' title='合成课程'>合成课程</span></a></div></div>";
            else if(address.AbsolutePath=="/visit/stucoursemiddle")html=(mode=="missing-work-signature"?"":"<input id='workEnc' value='SYNTHETIC_WORK'>")+(mode=="missing-exam-signature"?"":"<input id='examEnc' value='SYNTHETIC_EXAM'>");
            else if(address.AbsolutePath=="/mooc2/work/list")
            {
                WorkEntered.TrySetResult();
                if(mode=="cancel-work")await Task.Delay(Timeout.Infinite,token);
                if(mode=="work-login-required")return new("login_required",401,address,"");
                if(mode=="work-rate-limited")return new("rate_limited",429,address,"");
                html=mode=="unknown-work-list"?"<html>unrecognized work list</html>":emptyWorks?"<div class='null'>暂无作业</div>":
                    "<ul id='workList'><li data='https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&amp;classId=201&amp;cpi=301&amp;workId=401&amp;enc=SYNTHETIC' onclick='goTask(this)'><div class='right-content'><p class='overHidden2'>合成作业</p><p class='status'>未交</p></div><div class='time'>剩余48小时</div></li></ul>";
            }
            else if(address.AbsolutePath==ChaoxingExamParser.ListPath)
            {
                var row="<li><div onclick=\"viewExamAnswer('701','901')\"><p class='overHidden2'>合成考试</p><p class='status'>未开始</p></div></li>";
                html=mode=="unknown-exam-list"?"<html>unrecognized exam list</html>":"<div class='bottomList'><ul>"+row+(mode=="duplicate-exams"?row:"")+"</ul></div>";
                if(mode=="foreign-exam-class")resultAddress=new(address.OriginalString.Replace("clazzid=201","clazzid=999"));
            }
            else if(address.AbsolutePath=="/mooc-ans/mooc2/work/task")return new("detail_requires_user_action",302,address,"");
            else throw new InvalidDataException("Unexpected synthetic route.");
            return new("ok",200,resultAddress,html);
        }
    }
    private sealed class FixtureProtector:ILocalLinkProtector,IDisposable
    {
        private readonly byte[] key=RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(string value)
        {
            var plain=Encoding.UTF8.GetBytes(value);var encrypted=new byte[28+plain.Length];RandomNumberGenerator.Fill(encrypted.AsSpan(0,12));
            try{using var aes=new AesGcm(key,16);aes.Encrypt(encrypted.AsSpan(0,12),plain,encrypted.AsSpan(28),encrypted.AsSpan(12,16));return encrypted;}
            finally{CryptographicOperations.ZeroMemory(plain);}
        }
        public string Unprotect(byte[] value)
        {
            var plain=new byte[value.Length-28];
            try{using var aes=new AesGcm(key,16);aes.Decrypt(value.AsSpan(0,12),value.AsSpan(28),value.AsSpan(12,16),plain);return Encoding.UTF8.GetString(plain);}
            finally{CryptographicOperations.ZeroMemory(plain);}
        }
        public void Dispose()=>CryptographicOperations.ZeroMemory(key);
    }
}
