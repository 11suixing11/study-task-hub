using AccessProbe;
using AccessProbe.Linux;
using AccessProbe.Mobile;
using System.Net;
using System.Text;
using System.Text.Json;
using D=PlatformMetadataTestData.Fixtures;
using U=PlatformMetadataTestData.UcampusFixtures;
namespace LinuxChecks;
static class LinuxSyncFixtures
{
    private const string Entry="https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid=101&clazzid=201&cpi=301";
    private const string Work="https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=201&cpi=301&workId=401&answerId=501&enc=SYNTHETIC";
    public static BrowserSessionInput Session(string p)
    {
        MetadataSessionCookie[] cookies=p switch
        {
            "chaoxing"=>[new("_uid","900001",".chaoxing.com","/",true,true,null)],
            "mooc"=>[new("NTESSTUDYSI","synthetic-seed",".icourse163.org","/",true,true,null)],
            "xuetang"=>[new("xtsessionid","synthetic-seed",".xuetangx.com","/",true,true,null)],
            "ucampus"=>[U.Cookie()],
            "zhihuishu"=>[D.Cookie(),new("jt-cas","synthetic-flip-token",".zhihuishu.com","/",true,false,null)],
            _=>[new("sessionid","synthetic-seed",RainPlatform.Find(p).DefaultHost,"/",true,true,null)]
        };
        var page=p=="zhihuishu"?MetadataPlatform.Zhihuishu.Home.AbsoluteUri:LinuxPlatformPolicy.Login(p)!.AbsoluteUri;
        return new(p,page,cookies,p=="mooc"?"9001":null,p=="zhihuishu"?"synthetic-flip-token":null,
            p=="zhihuishu"?MobileSessionRules.CookieFingerprint(p,cookies):null);
    }
    public sealed class Handler(string platform):HttpMessageHandler
    {
        public List<string> Requests=[];
        public List<TrackedContent> Contents=[];
        public bool UnknownCatalog { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var uri=request.RequestUri!;
            Requests.Add(request.Method+" "+uri.AbsolutePath);
            if(uri.Scheme!="https")throw new InvalidDataException("Fixture refuses non-HTTPS request");
            var query=RainAddressPolicy.Query(uri)!;
            var json="";
            if(platform=="chaoxing")
            {
                if(!ChaoxingSyncParser.IsReadAddress(uri)||request.Method!=HttpMethod.Get)throw new InvalidDataException("Unexpected Chaoxing fixture request");
                json=uri.AbsolutePath switch
                {
                    "/base"=>"<input id='fid' value='901'>",
                    "/mooc2-ans/visit/courselistdata"=>UnknownCatalog?"<html>unknown</html>":"<div class='course-list'><div class='course'><a href='"+WebUtility.HtmlEncode(Entry)+"'><span class='course-name' title='合成课程 A'>A</span></a></div></div>",
                    "/visit/stucoursemiddle"=>"<input id='workEnc' value='WORK'><input id='openc' value='OPEN'><input id='enc' value='GENERAL'>",
                    "/mooc-ans/mooc2/work/task"=>"<div class='detailsHead'><h2 class='mark_title'>合成任务</h2><div class='infoHead'><p class='rightBord'>作答时间:<em>2026-11-13 19:04</em>至<em>2026-11-25 23:59</em></p></div></div>",
                    _ when uri.AbsolutePath.EndsWith("/work/list",StringComparison.Ordinal)=>"<ul id='workList'><li data='"+WebUtility.HtmlEncode(Work)+"' onclick='goTask(this);'><div class='right-content'><p class='overHidden2'>合成任务</p><p class='status'>已完成</p></div><div class='time'>剩余48小时</div></li></ul>",
                    _=>throw new InvalidDataException("Unknown Chaoxing fixture route")
                };
            }
            else if(platform is "yuketang" or "changjiang")
            {
                if(!RainAddressPolicy.IsReadAddress(RainPlatform.Find(platform),RainPlatform.Find(platform).DefaultHost,uri)||request.Method!=HttpMethod.Get)throw new InvalidDataException("Unexpected rain request");
                json=uri.AbsolutePath switch
                {
                    "/v2/api/web/userinfo"=>"{\"errcode\":0,\"data\":[{\"user_id\":77}]}",
                    "/v2/api/web/courses/list"=>"{\"errcode\":0,\"data\":{\"list\":[{\"id\":101,\"name\":\"一班\",\"course_name\":\"课程\"}]}}",
                    "/v2/api/web/classrooms/101"=>"{\"errcode\":0,\"data\":{\"id\":101,\"name\":\"一班\",\"course_name\":\"课程\"}}",
                    "/mooc-api/v1/lms/learn/course/schedule"=>"{\"success\":true,\"data\":{\"leaf_schedules\":{}}}",
                    "/v2/api/web/logs/learn/101"=>query["page"]=="0"?"{\"errcode\":0,\"data\":{\"has_more\":false,\"activities\":[{\"type\":4,\"courseware_id\":601,\"title\":\"旧版测验\",\"deadline\":1793010000000,\"status\":2,\"progress\":3,\"problem_count\":4}]}}":"{\"errcode\":0,\"data\":{\"activities\":[],\"has_more\":false}}",
                    _=>throw new InvalidDataException("Unknown rain route")
                };
            }
            else if(platform=="xuetang")
            {
                if(uri.IdnHost!="www.xuetangx.com"||request.Method!=HttpMethod.Get)throw new InvalidDataException("Unexpected Xuetang request");
                json=XuetangTestData.Metadata.Respond(uri).Json;
            }
            else if(platform=="ucampus")
            {
                if(uri.IdnHost is not ("u.unipus.cn" or "ucontent.unipus.cn")||request.Method!=HttpMethod.Get)throw new InvalidDataException("Unexpected Ucampus request");
                json=uri.AbsolutePath switch
                {
                    "/user/data/getToken"=>U.Profile(),"/user/student/index"=>U.Catalog(),"/user/student/mycourse/courseCatalog"=>U.CatalogPage(),
                    "/user/student/homework/index"=>query.GetValueOrDefault("type")=="2"?U.ExamShell():U.HomeworkShell(),
                    "/user/student/homework/list"=>query.GetValueOrDefault("type")=="2"?U.DefaultExamList():U.DefaultList(),
                    "/user/student/homework/chk"=>"{\"code\":0,\"rs\":{\"code\":0,\"sign\":\"synthetic-desktop-sign\"}}",
                    "/user/student/homework/examDetail"=>JsonSerializer.Serialize(new{code=0,rs=new{sign="synthetic-desktop-sign",type=query.GetValueOrDefault("homeworkId")=="4005"?2:1}}),
                    "/course/api/course/synthetic%2Bcontent%3Apc/default/" or "/course/api/course/synthetic+content:pc/default/"=>U.EmptyContentInfo(),
                    "/course/api/course_catalog/synthetic%2Bcontent%3Apc/default/" or "/course/api/course_catalog/synthetic+content:pc/default/"=>U.EmptyContentCatalog(),
                    _ when uri.AbsolutePath.Contains("/course_progress/",StringComparison.Ordinal)=>U.EmptyContentProgress(),
                    _=>throw new InvalidDataException("Unknown Ucampus route "+uri.AbsolutePath)
                };
            }
            else if(platform=="zhihuishu")
            {
                if(uri.IdnHost is not ("cloudapi.polymas.com")&&!uri.IdnHost.EndsWith(".zhihuishu.com",StringComparison.Ordinal))throw new InvalidDataException("Unexpected ZHS host");
                json=uri.AbsolutePath switch
                {
                    "/gateway/f/v1/login/getLoginUserInfo"=>D.Profile(),
                    "/gateway/t/v1/student/online/course/getCourseList"=>D.Catalog(),
                    "/gateway/t/v1/student/queryStudentAICourseList"=>D.SmartCatalog(),
                    "/gateway/t/v1/student/course/share/queryShareCourseInfo"=>D.SharedCatalog(),
                    "/student-course/student/index/teacherQueryStudentCourse"=>D.AgentCatalog(),
                    "/gateway/t/v1/student/getStudentNoCommitExam"=>D.Shared(),
                    "/zhsathome/homework/findImportantReminderList"=>D.Reminders(D.Reminder(),D.Reminder("202",type:2,status:2,title:"合成考试")),
                    "/zhsathome/homeworkUser/List"=>D.FlipList([D.FlipTask("201","合成作业",1)]),
                    "/zhsathome/examStu/List"=>D.FlipList([D.FlipTask("202","合成考试",1)]),
                    _=>throw new InvalidDataException("Unknown ZHS route "+uri.AbsolutePath)
                };
            }
            else if(platform=="mooc")
            {
                var fields=RainAddressPolicy.Query(new Uri("https://www.icourse163.org/?"+await request.Content!.ReadAsStringAsync(token)))!;
                if(uri.IdnHost!=MoocPlatform.Host||request.Method!=HttpMethod.Post||!MoocPlatform.IsReadRequest(uri.AbsolutePath,fields))throw new InvalidDataException("Unexpected MOOC request");
                var due=DateTimeOffset.Parse("2026-11-25T12:00:00Z").ToUnixTimeMilliseconds();
                json=uri.AbsolutePath switch
                {
                    MoocPlatform.CourseRpc=>"{\"code\":0,\"result\":{\"result\":[{\"id\":1001,\"name\":\"合成 MOOC 课程\",\"mode\":0,\"schoolPanel\":{\"shortName\":\"PKU\"},\"termPanel\":{\"id\":2001,\"courseId\":1001}}],\"pagination\":{\"pageIndex\":1,\"totlePageCount\":1,\"totleCount\":1}}}",
                    MoocPlatform.LearnedTermRpc=>"{\"code\":0,\"result\":{\"mocTermDto\":{\"id\":2001,\"chapters\":[{\"homeworks\":[{\"name\":\"同名合成作业\",\"test\":{\"id\":3001,\"usedTryCount\":1,\"deadline\":"+due+"}}],\"quizs\":[],\"exam\":null}],\"exams\":[]},\"lastLearnUnitId\":null}}",
                    _=>"{\"code\":0,\"result\":[]}"
                };
            }
            else throw new InvalidDataException("Unknown platform");
            var content=new TrackedContent(json);Contents.Add(content);
            return new(HttpStatusCode.OK){Content=content,RequestMessage=request};
        }
    }
    public sealed class TrackedContent(string value):HttpContent
    {
        private readonly byte[] bytes=Encoding.UTF8.GetBytes(value);
        public int Reads { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream,System.Net.TransportContext? context){Reads++;return stream.WriteAsync(bytes).AsTask();}
        protected override bool TryComputeLength(out long length){length=bytes.Length;return true;}
    }
}
