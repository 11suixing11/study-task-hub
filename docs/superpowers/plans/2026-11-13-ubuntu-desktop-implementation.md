# Ubuntu 完整桌面版 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans to implement this plan task-by-task. 默认按用户偏好顺序执行；只有用户明确要求时才使用子代理。Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付在用户 Ubuntu 26.04.1 / GNOME 50 / Wayland 上验证的完整任务聚合应用与自包含 amd64 `.deb`。

**Architecture:** 新增 LinuxCore 复用现有领域、SQLite 存储及八个平台同步器；LinuxBackend 通过受限 JSON Lines 子进程协议提供任务、会话、采集和提醒服务。GTK 4 / libadwaita 前端负责原生交互，WebKitGTK 6 负责本人官方登录，Linux 专用适配在新增文件中封装。

**Tech Stack:** Python 3.12+、PyGObject、GTK 4.14+、libadwaita 1.5+、WebKitGTK 6 API 2.44+、.NET 10、Microsoft.Data.Sqlite.Core 10.0.0、SQLitePCLRaw.provider.sqlite3 2.1.11、系统 SQLite 3.37+、dpkg-deb。

**Spec:** `docs/superpowers/specs/2026-11-13-ubuntu-desktop-requirements.md`；当前绝对路径为 `/home/core/桌面/聚合平台/docs/superpowers/specs/2026-11-13-ubuntu-desktop-requirements.md`。

**Workspace:** `/home/core/桌面/聚合平台`。从当前提交保留文档进入 `codex/ubuntu-desktop` 功能分支；已有同名分支时先检查其改动并复用，不重置文件。

## Global Constraints

- **G01：首要验收平台为 Ubuntu 26.04.1 amd64、GNOME 50、Wayland；Ubuntu 24.04 amd64 的兼容性必须独立验证。**
- **G02：前端使用 Python 3.12+、GTK 4.14+、libadwaita 1.5+、WebKitGTK 6.0 API 2.44+；后端使用 .NET 10、net10.0、linux-x64。**
- **G03：复用现有任务领域、存储与平台解析/采集代码；不将 WPF、WebView2 或 Android UI 依赖引入 Linux 核心。**
- **G04：前后端使用当前用户的子进程标准输入输出协议，不开启 HTTP 监听服务，不引入云账号或云服务。**
- **G05：数据默认位于 XDG_DATA_HOME/study-task-hub；未设置 XDG_DATA_HOME 时使用 ~/.local/share/study-task-hub。测试必须使用独立 --data-root。**
- **G06：应用数据根目录与浏览器 profile 目录权限为 0700，保护密钥文件权限为 0600；密钥和密文不可放入安装包或 Git。**
- **G07：保留正式任务库 .local/data/tasks.db 和既有登录环境；测试仅使用隔离数据库、隔离 profile 与本地响应。**
- **G08：用户本人完成官方登录；后台仅采集课程、任务及个人状态元数据，不代答题、不提交任务、不读取题目或答案正文。**
- **G09：人工修改优先；未知结构、分页上限、登录失效不能当作没有任务；平台原始状态和时间依据必须保留。**
- **G10：诊断仅记录错误码、计数、操作名和结果；不记录 Cookie、密码、票据、token、带签名参数的完整 URL 或 IPC 消息正文。**
- **G11：应用运行时自动采集与提醒可用；关闭应用后停止。首版不增加开机自启、常驻系统服务或跨设备同步。**
- **G12：交付自包含 .NET 运行时的 amd64 .deb；系统 GUI、WebKit、SQLite 与运行库依赖必须在包元数据中声明。**
- **G13：沿用当前项目目录作为开发工作区，使用功能分支；默认不启动子代理，除非用户明确要求。**

## 执行规则与基线

每个 Task 是一个可独立检查的交付单元；其步骤分别执行写测试、观察失败、实现、验证、提交。代码片段是必须锁定的行为与接口，不要求复制成重复实现。先建立可编译的最小接口骨架，再观察行为失败；导入/编译错误先解决，不能把环境错误当作红色行为测试。

本文命令默认在项目根目录执行。Task 1 创建开发环境脚本后，每个新终端先执行 `source scripts/linux-dev-env.sh`；测试/构建脚本也自行读取它。该脚本仅为源码开发选择项目本地 SDK，不能被正式 launcher 或最终包验收依赖。每个 C# 检查文件提供 `RunAsync(): Task`，runner 注册 protection/tasks/ipc/sessions/sync/navigation/reminders，`--suite all` 顺序运行已实现检查。Python 测试使用标准库 unittest，不增加仅为断言服务的依赖。

先用 CodeGraph 阅读准备修改的现有符号。新增模块未进入图谱时直接读文件；修改后同步索引。不要改写共享平台解析器来掩盖 Linux 环境限制。

当前 `.tools/dotnet` 已有 SDK 10.0.404，既有 MobileChecks 47 项通过。ProbeChecks 遇到 Windows DPAPI 的平台限制；本计划仅执行适用的共享检查，并独立记录 Windows 回归条件。当前 WebKit 6 运行库已有，缺其 GI 描述包。

进度保存到 `.local/ubuntu-desktop-work/progress.md`，第一行写本计划路径；记录每项 Task 的测试命令、结果、提交和未验收范围。中断后从该文件与 git log 恢复，不重新实施已完成任务。

## 任务依赖与文件责任

| Task | 依赖 | 独立交付 | 主要责任 |
| --- | --- | --- | --- |
| 1 | 无 | Linux 核心、系统 SQLite、本地保护可用 | runtime、paths、protector |
| 2 | 1 | 任务/设置服务及稳定 DTO | contracts、task query/CRUD |
| 3 | 2 | 非阻塞 IPC 与后端进程 | protocol、host、Python bridge |
| 4 | 3 | 原生首页、统计、筛选分页 | application、window、dashboard |
| 5 | 4 | 任务编辑与状态/删除流程 | task editor、UI/core integration |
| 6 | 3、4 | 官方 WebKit 登录与会话保存 | policy、login window、session adapter |
| 7 | 6 | 课程确认与八平台采集、job 取消 | platform view、sync jobs |
| 8 | 7 | 校验过的官方任务入口 | navigation adapter |
| 9 | 5、7 | 自动检查、系统提醒、可靠退出 | scheduler、notification、shutdown |
| 10 | 1–9 | 自包含 deb、验证报告与说明 | packaging、release checks |

### Task 1: Linux 核心、数据路径与保护适配

**Files:**
- Create: `src/AccessProbe.LinuxCore/AccessProbe.LinuxCore.csproj`
- Create: `src/AccessProbe.LinuxCore/LinuxRuntime.cs`, `LinuxDataPaths.cs`, `LinuxDataProtector.cs`
- Create: `tests/LinuxChecks/LinuxChecks.csproj`, `Program.cs`, `Check.cs`, `ProtectionChecks.cs`
- Create: `scripts/linux-dev-env.sh`, `scripts/test-linux-core.sh`
- Read: `tests/MobileChecks/MobileChecks.csproj`, `src/AccessProbe/TaskStore.cs`, `LocalLinkProtector.cs`

**Interfaces:**
- `LinuxRuntime.InitializeSqlite(): void`，幂等初始化系统 SQLite provider。
- `LinuxDataPaths.Resolve(string? explicitRoot, string? xdgDataHome, string userHome): string`，明确参数优先，其次 XDG，其次用户默认目录。
- `LinuxDataProtector(string dataRoot): ILocalLinkProtector, IDisposable`，密钥为 `dataRoot/protection.key`，采用版本字节 + 12 字节 nonce + 16 字节 tag + ciphertext。
- `Check.Equal<T>(T expected, T actual)`, `Check.True(bool condition, string message)`, `Check.Throws<TException>(Action action)`：测试辅助断言，失败抛异常。
- `ProtectionChecks.RunAsync(): Task`；测试 runner 支持 `--suite protection` 和以后新增的 suite。

- [ ] **Step 1: 建立隔离测试骨架与失败行为。** `Check.cs` 实现上述断言；临时目录位于 `.local/linux-checks/<guid>`，清理只针对本测试创建的目录。写入至少以下真实行为测试：

```csharp
var root = Path.Combine(Environment.CurrentDirectory, ".local", "linux-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
byte[] encrypted;
using (var first = new LinuxDataProtector(root)) encrypted = first.Protect("https://example.invalid/课程");
using (var restarted = new LinuxDataProtector(root))
    Check.Equal("https://example.invalid/课程", restarted.Unprotect(encrypted));
Check.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "protection.key")));
encrypted[^1] ^= 1;
using (var restarted = new LinuxDataProtector(root))
    Check.Throws<System.Security.Cryptography.CryptographicException>(() => restarted.Unprotect(encrypted));
```

另写字面量断言：默认路径、XDG 路径、显式路径、已有数据库而缺密钥、错误长度密钥、符号链接密钥、另一个目录的密钥无法解密。

- [ ] **Step 2: 运行红色测试。** `bash scripts/test-linux-core.sh --suite protection`。先使接口可编译，确认失败在未实现的保护/持久化行为，而非运行库缺失。
- [ ] **Step 3: 实现核心引用和保护。** LinuxCore 的 Compile 列表沿用 MobileChecks 的共享领域、TaskStore、各平台 reader/parser/synchronizer 和 MobileSessionRules/MobileVault/MobileSync；不引用 MobileSyntheticChecks、MobilePasswordlessLogin、任何 Activity 或 MobilePlatforms。包引用改为：

```xml
<PackageReference Include="Microsoft.Data.Sqlite.Core" Version="10.0.0" />
<PackageReference Include="SQLitePCLRaw.provider.sqlite3" Version="2.1.11" />
<PackageReference Include="AngleSharp" Version="1.8.5" />
<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.0" />
```

```csharp
// LinuxRuntime 的一次性初始化由 Lazy 控制；调用者在首次创建 TaskStore 前触发。
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
SQLitePCL.raw.FreezeProvider();
// keyBytes 必须是从原子创建/校验权限的 32 字节密钥中读取。
var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
var plain = System.Text.Encoding.UTF8.GetBytes(value);
var cipher = new byte[plain.Length];
var tag = new byte[16];
using var aes = new System.Security.Cryptography.AesGcm(keyBytes, 16);
aes.Encrypt(nonce, plain, cipher, tag);
System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
return new byte[] { 1 }.Concat(nonce).Concat(tag).Concat(cipher).ToArray();
```

目录 0700，FileMode.CreateNew / UnixCreateMode 0600 创建密钥，并写入后 flush；并发创建输家读取赢家的完整密钥。已有 `tasks.db` 且缺密钥时报 KEY002；长度/权限/链接异常时报 KEY001，不能生成新密钥覆盖。Dispose 清除密钥内存。开发环境脚本使用以下路径；不设置或覆盖 HOME：

```bash
taskRoot="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
export DOTNET_ROOT="$taskRoot/.tools/dotnet"
export DOTNET_CLI_HOME="$taskRoot/.tools/dotnet-home"
export NUGET_PACKAGES="$taskRoot/.tools/nuget/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$DOTNET_ROOT:$PATH"
```

test-linux-core.sh 使用相同 root 调用 `dotnet run --project tests/LinuxChecks/LinuxChecks.csproj -c Release -- "$@"`；传递 suite 参数，不自动访问正式目录。

- [ ] **Step 4: 验证。** 重跑 protection suite；使用 LinuxRuntime 初始化后真实 TaskStore 创建/读取数据库。执行 `dotnet list src/AccessProbe.LinuxCore/AccessProbe.LinuxCore.csproj package --vulnerable --include-transitive` 并记录审查，不携带旧 e_sqlite3 native bundle。
- [ ] **Step 5: 提交。** `git add src/AccessProbe.LinuxCore tests/LinuxChecks scripts/linux-dev-env.sh scripts/test-linux-core.sh`；`git commit -m "feat(linux): add shared core and private data protection"`。

### Task 2: 任务查询、编辑、设置和 DTO

**Files:**
- Create: `src/AccessProbe.LinuxCore/LinuxContracts.cs`, `LinuxAppService.cs`, `LinuxAppService.Tasks.cs`
- Create: `tests/LinuxChecks/TaskServiceChecks.cs`
- Modify: `tests/LinuxChecks/Program.cs`，注册 `tasks` suite

**Interfaces:**

```csharp
public sealed record TaskInput(string Title, string PlatformId, string CourseName,
    string DeadlineKind, string? BeginIso, string? DeadlineIso, string? Link,
    decimal? ProgressPercent, string ScoreText, string Notes, int? PriorityOverride,
    bool ConfirmDeadline = false);
public sealed record TaskQuery(string? Platform = null, string State = "all",
    string Search = "", string Sort = "priority", int Offset = 0, int Limit = 100,
    bool IncludeUnselected = false);
public sealed record LinuxTaskView(AccessProbe.TaskItem Task, string StateLabel,
    string DeadlineLabel, bool CanComplete, int Priority, string? LinkIssue);
public sealed record DashboardCounts(int Pending, int Completed, int Ignored,
    int Returned, int NeedsReview, int DueSoon, int Overdue);
public sealed record TaskPage(long SnapshotVersion, int Offset, int Total,
    int FilteredTotal, int HiddenTotal, DashboardCounts Summary, IReadOnlyList<LinuxTaskView> Items,
    IReadOnlyDictionary<string, int> PlatformCounts);
public sealed record TaskDetail(LinuxTaskView View, string? Link);
```

LinuxAppService 实现 IDisposable，构造函数为 `LinuxAppService(string dataRoot, Func<DateTimeOffset>? utcNow = null, TimeZoneInfo? timeZone = null, Func<string,HttpMessageHandler>? readerFactory = null, ReadOnlyMemory<byte>? syntheticMetadataKey = null)`；方法 `QueryTasks(TaskQuery): TaskPage`, `GetTask(string): TaskDetail`, `CreateTask(TaskInput): TaskDetail`, `EditTask(string,int,TaskInput): TaskDetail`, `SetTaskState(string,int,string): TaskDetail`, `DeleteTask(string,int): void`, `LoadSettings(): AppSettings`, `SaveSettings(AppSettings): void`。测试入口 `TaskServiceChecks.RunAsync()`。syntheticMetadataKey 仅在隔离智慧树 fixture 中注入，正式后端不提供该参数入口。

- [ ] **Step 1: 写失败测试。** 使用真实 LinuxDataProtector、TaskStore 与相同目录重建服务：

```csharp
var input = new TaskInput("线代作业", "chaoxing", "数学", "known", null,
    "2026-11-14T20:00:00+08:00", "https://example.invalid/work", null, "", "原备注", null);
var service = new LinuxAppService(root);
var created = service.CreateTask(input);
service.EditTask(created.View.Task.Id, created.View.Task.Revision, input with { Notes = "修订备注" });
Check.Throws<AccessProbe.TaskValidationException>(() =>
    service.EditTask(created.View.Task.Id, created.View.Task.Revision, input));
var restarted = new LinuxAppService(root);
Check.Equal("修订备注", restarted.GetTask(created.View.Task.Id).View.Task.Notes);
Check.Equal(1, restarted.QueryTasks(new TaskQuery(Search: "线代")).FilteredTotal);
```

表驱动补充空标题、300/301 字标题、非法进度、无时区日期、开始晚于截止、Known 无日期、旧 Revision 删除、完成进度限制与设置重启。创建 5,000 条隔离任务测试分页不重复、过滤总数和响应版本。

- [ ] **Step 2: 验证红色。** `bash scripts/test-linux-core.sh --suite tasks`。
- [ ] **Step 3: 实现领域转换与查询。** 复用 TaskRules / TaskStore，不在 Python 重写保存规则。日期只接受含 Z 或偏移的 ISO 字符串。核心编辑调用为：

```csharp
var draft = new AccessProbe.TaskDraft(input.Title, input.PlatformId, input.CourseName,
    deadlineKind, beginUtc, deadlineUtc, input.Link, input.ProgressPercent,
    input.ScoreText, input.Notes, input.PriorityOverride) { ConfirmDeadline = input.ConfirmDeadline };
return ToDetail(store.Edit(id, revision, draft, utcNow()));
```

`deadlineKind/beginUtc/deadlineUtc` 来自本任务的严格转换；`ToDetail(TaskItem)` 产生上面的 TaskDetail。QueryTasks 默认从 LoadRecordedTasks 查询全部状态；IncludeUnselected=true 时从 LoadTasks 恢复查看历史，不改变选课与提醒资格。Total 为当前课程范围内、搜索筛选前的总量，FilteredTotal 为筛选后总量，HiddenTotal 为默认课程范围隐藏的历史数量；Summary 和 PlatformCounts 使用搜索筛选前的当前课程范围。DueSoon/Overdue 仅统计 TaskRules.IsActive 且 HasUsableDeadline 的任务，分别为未来 24 小时内和已过期；推定日期在行内保留证据提示。

每页最多 100，非法 Offset/Limit 返回 TASK001；Store.Changed 增加 SnapshotVersion；查询期间版本变化则重取一致快照。仅 GetTask 返回编辑用链接，首页摘要不输出敏感 URL；LinkIssue 从 JsonIgnore 的领域字段单独映射。JSON 使用 camelCase 属性和 JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues:false)；状态固定 pending/completed/ignored/returned/needsReview，日期种类 unknown/known/none，筛选另允许 all，排序固定 priority/deadline/updated。非法值不能默认为 all。

- [ ] **Step 4: 运行 tasks 与 protection suite，核对 UTC 保存、按指定 timeZone 显示、采集任务 Overrides 及删除持久化。**
- [ ] **Step 5: 提交。** `git add src/AccessProbe.LinuxCore tests/LinuxChecks`；`git commit -m "feat(linux): expose validated task and settings services"`。

### Task 3: JSON Lines 后端与非阻塞 Python bridge

**Files:**
- Create: `src/AccessProbe.LinuxBackend/AccessProbe.LinuxBackend.csproj`, `Program.cs`, `BackendHost.cs`, `Protocol.cs`
- Create: `src/AccessProbe.Linux/study_task_hub/__init__.py`, `bridge.py`
- Create: `tests/LinuxChecks/ProtocolChecks.cs`, `tests/linux_test_paths.py`, `tests/test_linux_bridge.py`
- Modify: `tests/LinuxChecks/LinuxChecks.csproj`, `Program.cs`；引用 backend 项目并注册 `ipc`

**Interfaces:** `BackendHost.ServeAsync(Stream input, Stream output, LinuxAppService service, CancellationToken): Task`；请求为 `{version:1,id:integer,command:string,args:object}`。Python `BackendBridge(backend_path,data_root)` 提供 `request_async(command,args=None): concurrent.futures.Future`、`subscribe_events(callback)`、`close()`。成功 Future 返回 result，失败抛 `BackendError(code,message)`。

本任务创建项目时锁定 AssemblyName=StudyTaskHub.LinuxBackend、OutputType=Exe、TargetFramework=net10.0，并 ProjectReference LinuxCore。所有 Task 的协议扩展按以下表注册；未来命令在对应 Task 完成前返回 IPC002，不设假成功实现。

| 命令 | args | 核心调用与 result | 实现 Task |
| --- | --- | --- | --- |
| snapshot | TaskQuery 的 camelCase 字段 | QueryTasks → TaskPage | 3 |
| get-task | id:string | GetTask → TaskDetail | 3 |
| create-task | TaskInput 的 camelCase 字段 | CreateTask → TaskDetail | 3 |
| edit-task | id:string, revision:int, input:TaskInput | EditTask → TaskDetail | 3 |
| set-state | id:string, revision:int, state:string | SetTaskState → TaskDetail | 3 |
| delete-task | id:string, revision:int | DeleteTask → null | 3 |
| get-settings / save-settings | {} / settings:AppSettings | LoadSettings → AppSettings / SaveSettings → null | 3 |
| shutdown | {} | 停止 job、释放领取并退出 → null | 3、7、9 |
| platforms | {} | LoadPlatformViews → PlatformView[] | 6 |
| capture-session / get-session | BrowserSessionInput / platform:string | CaptureBrowserSession → PlatformSessionInfo / GetBrowserSession → MobileSession? | 6 |
| clear-session | platform:string | ClearBrowserSession → null；前端另清对应 profile | 6 |
| get-courses | platform:string | GetCourses → CourseRecordingSettings | 7 |
| select-courses | platform:string, revision:long, keys:string[], hideUnselected:bool | SelectCourses → CourseRecordingSettings | 7 |
| start-sync / cancel-sync | platform:string, catalogOnly:bool / jobId:string | SyncJobManager.Start → {jobId:string} / Cancel → null | 7 |
| resolve-task | id:string | ResolveTaskAsync → NavigationView | 8 |
| take-notifications | {} | TakeDueNotifications → NotificationView[] | 9 |
| ack-notifications / release-notifications | ids:string[], success:bool / ids:string[] | AcknowledgeNotifications / ReleaseNotifications → null | 9 |

错误码固定 IPC001=不支持的协议版本、IPC002=未知命令、IPC003=非法 JSON/信封/请求超限、IPC004=后端断开、IPC005=无法安全输出的超限响应、IPC006=未预期的内部错误。可识别请求回显 id；无法解析信封时 id=null。领域错误返回现有 Code 与经过筛选的提示。事件信封为 `{version:1,event:"progress"|"job-finished",payload:{...}}`，不占用请求 id。

- [ ] **Step 1: 写失败测试。** ProtocolChecks 将下面字面量写进 MemoryStream，调用真实 BackendHost，核对没有任务写入：

```json
{"version":1,"id":7,"command":"unknown","args":{}}
{"version":9,"id":8,"command":"snapshot","args":{}}
```

期望 id 7 返回 IPC002，id 8 返回 IPC001；坏 JSON 返回 IPC003 且下一条有效请求仍能处理。超 2 MiB 请求拒绝；异常不得把参数或堆栈写入响应。

```python
future = bridge.request_async("snapshot", {"state": "all"})
result = future.result(timeout=5)
assert result["filteredTotal"] == 0
```

本任务的无 GUI bridge 测试直接 `future.result(timeout=5)`，不依赖 Task 4 的 helper；测试实际后端子进程的往返、退出后 Future 失败和重新创建 bridge 后数据恢复。

linux_test_paths.py 定义 `ROOT=Path(__file__).resolve().parents[1]`、把 `ROOT/'src/AccessProbe.Linux'` 插入测试 sys.path，并提供 `backend_path(): Path` 返回 `ROOT/'src/AccessProbe.LinuxBackend/bin/Release/net10.0/StudyTaskHub.LinuxBackend'`。Python 测试先用 tempfile.TemporaryDirectory 创建 data_root，再创建真实 BackendBridge；测试结束 close 后清理。Task 3 的 core runner 项目引用构建 backend apphost；后续 test-linux-ui.sh 在运行 unittest 前同样构建该输出。

- [ ] **Step 2: 运行红色。** `bash scripts/test-linux-core.sh --suite ipc` 和 `python3 -m unittest discover -s tests -p test_linux_bridge.py`。
- [ ] **Step 3: 实现协议边界。** 请求读入前限制字节数；version、id 和 command 校验后调 Task 2 API，设置 JsonSerializerOptions camelCase。标准输出只有协议；诊断仅写筛选后的 stderr。Python 启动方式锁定为：

```python
self.process = subprocess.Popen(
    [str(backend_path), "--data-root", str(data_root)],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, encoding="utf-8", bufsize=1)
```

后台线程按字节上限持续读取 stdout，按 id 解决 pending Future；写入加锁，每条 JSON 一行，并持续消费经过筛选的 stderr，防止管道塞满。进程 EOF/非法超大响应使未完成请求失败并保留 UI 输入；不自动重放 Create/Edit/Delete。事件 callback 在读取线程执行，Gtk 更新由上层 GLib.idle_add 调度。输入 EOF 触发退出，后端等待未完成输出刷新。close 先请求 shutdown，最多等待 5 秒，仍不退出则终止该子进程，不杀其他实例。

- [ ] **Step 4: 验证后台进程启动、任务 CRUD 往返、错误映射、超限与 stdout 无杂项；重跑 core suites。**
- [ ] **Step 5: 提交。** `git add src/AccessProbe.LinuxBackend src/AccessProbe.Linux/study_task_hub tests`；`git commit -m "feat(linux): add bounded asynchronous backend protocol"`。

### Task 4: GTK 原生首页、统计与筛选分页

**Files:**
- Create: `src/AccessProbe.Linux/main.py`, `study_task_hub/application.py`, `window.py`, `dashboard.py`, `models.py`, `style.css`
- Create: `tests/linux_ui_helpers.py`, `tests/test_linux_ui.py`, `scripts/test-linux-ui.sh`

**Interfaces:** `StudyTaskHubApplication(backend_path,data_root)`；`MainWindow(application,bridge)` 拥有 `dashboard`；`Dashboard(bridge)` 拥有 `task_model: Gio.ListStore`、`selection: Gtk.SingleSelection`、`search_entry` 和 `refresh()`。`TaskRow(GObject.Object)` 持有一条 LinuxTaskView。`run_until(predicate,timeout=5)` 为测试 helper，通过 GLib.MainContext.iteration 与 monotonic deadline 等待真实事件。main.py 支持 `--backend PATH`、`--data-root PATH`、`--help`；默认 backend 位于 `Path(__file__).resolve().parent.parent / 'backend/StudyTaskHub.LinuxBackend'`，开发脚本显式传入构建输出路径。

- [ ] **Step 1: 写实际 GTK 失败测试。** TestAppFixture 在独立目录启动真实 bridge 与 MainWindow，提供 `request(command,args)`（等待 Future）、`wait(predicate)`、`close()`：

```python
with TestAppFixture() as fx:
    fx.request("create-task", {"title": "数学作业", "platformId": "chaoxing",
        "courseName": "数学", "deadlineKind": "unknown", "scoreText": "", "notes": ""})
    fx.window.dashboard.refresh()
    fx.wait(lambda: fx.window.dashboard.task_model.get_n_items() == 1)
    fx.window.dashboard.search_entry.set_text("不存在的课程")
    fx.wait(lambda: fx.window.dashboard.task_model.get_n_items() == 0)
    assert fx.request("snapshot", {"state": "all"})["total"] == 1
```

再测试全部状态默认、同名不同平台、连续切换筛选的旧响应被丢弃、第二页追加与后台失败状态。数据验证通过真实 backend，不仅断言标签文案。

- [ ] **Step 2: 运行红色。** `bash scripts/test-linux-ui.sh --group dashboard`，无显示环境时记录环境不足，不用跳过结果当通过。
- [ ] **Step 3: 实现原生布局。** `Adw.ApplicationWindow` + `Adw.ToolbarView`，HeaderBar 提供同步/新增/平台/设置入口；左侧分类与来源、中间列表、右侧详情，窄窗口可收起详情。列表基本构造：

```python
model = Gio.ListStore.new(TaskRow)
selection = Gtk.SingleSelection.new(model)
factory = Gtk.SignalListItemFactory()
factory.connect("setup", self.setup_row)
factory.connect("bind", self.bind_row)
view = Gtk.ListView.new(selection, factory)
```

setup_row 创建实际标签与状态组件，bind_row 使用 set_text 而非未转义 markup。每次查询携带前端 generation，结果只在当前 generation 与 snapshotVersion 匹配时应用；统计显示记录中任务总量与筛选量，隐藏历史另给恢复入口。后台只刷新数据，不改变正在编辑的输入。

- [ ] **Step 4: 运行 dashboard、bridge 与 core 检查；在本机深色主题和 1000×700 窗口检查布局。截图通过测试自己的 GTK WidgetPaintable/Snapshot 渲染保存，不依赖窗口控制脚本。**
- [ ] **Step 5: 提交。** `git add src/AccessProbe.Linux tests/linux_ui_helpers.py tests/test_linux_ui.py scripts/test-linux-ui.sh`；`git commit -m "feat(linux): add native task dashboard and filters"`。

### Task 5: 任务编辑、状态变更与删除

**Files:**
- Create: `src/AccessProbe.Linux/study_task_hub/task_editor.py`
- Modify: `dashboard.py`, `window.py`, `tests/test_linux_ui.py`, `tests/LinuxChecks/TaskServiceChecks.cs`

**Interfaces:** `TaskEditor(parent,bridge,task_id=None)`；`fields` 保存生产代码读取的实际输入控件；`save_button` 与 `save()` 发送 create-task/edit-task；成功后回调刷新，失败保留表单。`parse_local_datetime(text:str,zone:zoneinfo.ZoneInfo): str` 返回带偏移的 ISO 字符串，非法或歧义本地时间抛 ValueError。状态和删除使用已选 Task 的 id/revision。

- [ ] **Step 1: 写失败 UI 行为。** TestAppFixture 创建编辑窗口，通过真实 Gtk.Entry/TextBuffer 输入并激活保存：

```python
editor = TaskEditor(fx.window, fx.bridge)
editor.fields["title"].set_text("系统作业")
editor.fields["notes"].get_buffer().set_text("保留这条备注")
editor.save_button.emit("clicked")
fx.wait(lambda: fx.request("snapshot", {"state": "all"})["total"] == 1)
task_id = fx.request("snapshot", {"state": "all"})["items"][0]["task"]["id"]
assert fx.request("get-task", {"id": task_id})["view"]["task"]["notes"] == "保留这条备注"
```

补充非法输入保留、编辑期间另一次修改造成 Revision 错误、标记完成和确认删除后重启；对采集任务测试平台不能修改与进度不足不能完成。

- [ ] **Step 2: 运行红色。** `bash scripts/test-linux-ui.sh --group editor`。
- [ ] **Step 3: 实现表单与动作。** 标题/课程/成绩为 Entry，备注为 TextView，平台与日期种类为 DropDown，日期输入转带偏移 ISO：

```python
def parse_local_datetime(text, zone):
    naive = datetime.datetime.strptime(text, "%Y-%m-%d %H:%M")
    candidates = [naive.replace(tzinfo=zone, fold=fold) for fold in (0, 1)]
    valid = [value for value in candidates
             if value.astimezone(datetime.timezone.utc).astimezone(zone).replace(tzinfo=None) == naive]
    if not valid or len({value.utcoffset() for value in valid}) != 1:
        raise ValueError("此本地时间处于夏令时切换区间，请选择明确时间。")
    return valid[0].isoformat(timespec="seconds")

# 系统本地时区来自 /etc/localtime，测试显式传入 Asia/Shanghai 或 Europe/Berlin。
with open("/etc/localtime", "rb") as zone_file:
    local_zone = zoneinfo.ZoneInfo.from_file(zone_file)
deadline_iso = parse_local_datetime(text, local_zone)
```

仅 known 发送截止日期；unknown/none 的 DeadlineIso 发送 null。BeginIso 独立于截止种类，有输入时转换，无输入发送 null。前端以往返转换拒绝夏令时切换造成的不存在或歧义时间，后端仍校验明确偏移、日期种类与先后关系；输入保留。采集任务禁用来源选择；错误显示后保存按钮恢复可用，输入不清空。删除使用确认窗口，确认才发 delete-task，取消不调用后端；状态变更使用 Task 2 校验，不直接改列表对象冒充成功。

- [ ] **Step 4: 验证完整 editor/group、重启读取和 core tasks；人工检查大字体与字段焦点顺序。**
- [ ] **Step 5: 提交。** `git add src/AccessProbe.Linux tests`；`git commit -m "feat(linux): add validated task editing and state actions"`。

### Task 6: WebKit 官方登录与会话保存

**Files:**
- Create: `src/AccessProbe.LinuxCore/LinuxPlatformPolicy.cs`, `LinuxAppService.Sessions.cs`, `TaskStore.LinuxSessions.cs`
- Create: `src/AccessProbe.Linux/study_task_hub/login_window.py`, `cookie_adapter.py`
- Create: `tests/LinuxChecks/SessionChecks.cs`, `tests/test_linux_webkit.py`, `tests/linux_webkit_fixture.py`
- Modify: `src/AccessProbe.LinuxCore/LinuxContracts.cs`, `src/AccessProbe.LinuxBackend/BackendHost.cs`, `tests/LinuxChecks/Program.cs`，注册 sessions suite

**Interfaces:** `LinuxPlatformPolicy.Login(string): Uri?`, `Allowed(string,string,bool frame): bool`, `TryUpgrade(string,string): string?`。`BrowserSessionInput(string PlatformId,string PageUri,MetadataSessionCookie[] Cookies,string? AccountId,string? Token,string? TokenBinding)`；`CaptureBrowserSession(BrowserSessionInput): PlatformSessionInfo`；`GetBrowserSession(string): MobileSession?`；`ClearBrowserSession(string): void`。`LoginWindow(parent,bridge,platform_view)`；`capture_session()` 与 `close_with_save()` 使用异步 CookieManager。

- [ ] **Step 1: 写失败测试。** SessionChecks 使用合成学习通 Cookie `_uid=900001`、域 `i.chaoxing.com`、Path `/`、Secure/HttpOnly true，核对保存→重启→取回、外域排除、空快照不清原数据、已有 ready 遇新会话改为 saved。WebKit fixture 只监听 loopback，测试 CookieManager 实际 HttpOnly 属性、导航/弹窗和关闭保存，不访问官网。

```csharp
var cookies = new[] { new AccessProbe.MetadataSessionCookie("_uid", "900001", "i.chaoxing.com", "/", true, true, null) };
service.CaptureBrowserSession(new("chaoxing", "https://i.chaoxing.com/", cookies, null, null, null));
Check.Equal("saved", service.LoadPlatformViews().Single(p => p.Id == "chaoxing").Session.State);
Check.Throws<AccessProbe.TaskValidationException>(() => service.CaptureBrowserSession(
    new("chaoxing", "https://i.chaoxing.com/", Array.Empty<AccessProbe.MetadataSessionCookie>(), null, null, null)));
Check.Equal(1, service.GetBrowserSession("chaoxing")!.Cookies.Length);
```

`PlatformView(string Id,string Name,string? LoginUri,PlatformSessionInfo Session,CourseRecordingSettings Recording,string TaskSyncOutcome,string? TaskSyncAtIso)` 与 `LoadPlatformViews(): IReadOnlyList<PlatformView>` 在本任务产出；FiF 的 Session 为 not_saved 且 LoginUri=null。WebKit loopback 检查验证真实 Cookie 属性、弹窗与关闭保存的异步流程，fixture 的本地域名不送入生产平台会话接口；持久化闭环由 SessionChecks 的允许域合成快照验证。两类结果分别记录，不用域名映射冒充真实官网登录验证。

- [ ] **Step 2: 运行红色。** `bash scripts/test-linux-core.sh --suite sessions`；`bash scripts/test-linux-ui.sh --group webkit`。开发环境准备 WebKit 6 GI；若只能在 `.tools` 解包 GI，标明仅开发测试，最终包验收不借此宣称系统依赖已满足。
- [ ] **Step 3: 实现地址与浏览器适配。** Login、Allowed(frame=false)、Allowed(frame=true) 和 TryUpgrade 分派复用 MobilePlatforms 中的纯规则，不引用 Android.Webkit。WebKit 使用每个平台独立的 NetworkSession 与私有目录；不关闭证书校验，不任意放行外域。CookieAdapter 按 Soup.Cookie 实际属性构造输入，后台用 MobileSessionRules.CookieValid 过滤。loopback fixture 通过测试对象注入只允许该 fixture 主机的 policy；该注入不暴露到正式命令行或配置，不把 localhost 加入生产平台白名单。HTTPS fixture 的未受信任证书必须触发拒绝，不调用 allow_tls_certificate_for_host。

MOOC 仅在官方 `/home.htm` 执行固定元数据脚本：

```javascript
(() => { const u = window.webUser; return u ? String(u.id || '') : ''; })()
```

智慧树仅在现有 IsMetadataDocument 允许页面读取 token；保留 `MainActivity.cs:227` 的 origin/path 规则、16,384 字节上限及 CookieBinding。前后两次 Cookie 指纹不同则拒绝快照。新保存的会话使用 Linux 专用事务写入 `saved/checked_utc=NULL`，不继承旧 ready；字段校验仍沿用 MobileSessionRules。

关闭请求先暂停浏览器/采集，获取并加密保存有效快照，5 秒后无论结果都允许关闭并显示保存结果；空快照不清已有归档。恢复优先使用后台有效会话回写 CookieManager，清除动作调用 ClearBrowserSession（对应 store.ClearPlatformSession），同时清本平台 profile 与归档且不删除任务。密文损坏由会话适配层返回 SESSION001 的明确错误；不能仅沿用 MobileVault.Load 返回 null 而把损坏显示成未登录。

- [ ] **Step 4: 验证真实 WebKit loopback fixture、会话重启、旧响应竞争、HttpOnly/作用域；登录窗口在本机 Wayland 可渲染。**
- [ ] **Step 5: 提交。** `git add src/AccessProbe.LinuxCore src/AccessProbe.LinuxBackend src/AccessProbe.Linux tests`；`git commit -m "feat(linux): add isolated official login and session capture"`。

### Task 7: 课程确认、八平台采集与可取消 job

**Files:**
- Create: `src/AccessProbe.LinuxCore/LinuxAppService.Sync.cs`
- Create: `src/AccessProbe.LinuxBackend/SyncJobManager.cs`
- Create: `src/AccessProbe.Linux/study_task_hub/platform_window.py`, `course_picker.py`
- Create: `tests/LinuxChecks/SyncChecks.cs`, `tests/LinuxChecks/LinuxSyncFixtures.cs`
- Modify: `src/AccessProbe.LinuxCore/LinuxContracts.cs`, `src/AccessProbe.LinuxBackend/BackendHost.cs`, `tests/LinuxChecks/LinuxChecks.csproj`, `tests/LinuxChecks/Program.cs`, `tests/test_linux_ui.py`

**Interfaces:** `GetCourses(string): CourseRecordingSettings`；`SelectCourses(string,long,IReadOnlyList<string>,bool hideUnselected): CourseRecordingSettings`；`RunSyncAsync(string,bool catalogOnly,IProgress<string>?,CancellationToken): Task<MobileSyncResult>`。`SyncJobManager(LinuxAppService service, Action<string,object> emitEvent)`；`Start(string,bool): string`、`Cancel(string): void`、`StopPlatformAsync(string): Task`、`StopAllAsync(): Task`；事件 payload 为 `progress {jobId,platform,message}`、`job-finished {jobId,platform,result,error}`。`LinuxSyncFixtures.CreateHandler(string): HttpMessageHandler` 和 `Session(string): BrowserSessionInput` 产出匹配的隔离响应与合成账号会话。

- [ ] **Step 1: 写失败测试。** 使用测试文件中的 HttpMessageHandler 提供固定学习通门户与课程 HTML；地址不在 fixture 表就抛异常，不转发网络。选课行为为：

```csharp
var result = await service.RunSyncAsync("chaoxing", true, null, CancellationToken.None);
Check.True(result.CatalogCovered, "fixture catalog must be covered");
Check.Equal(0, service.QueryTasks(new()).Total);
var courses = service.GetCourses("chaoxing");
service.SelectCourses("chaoxing", courses.Revision, new[] { courses.Courses.Single().Key }, true);
await service.RunSyncAsync("chaoxing", false, null, CancellationToken.None);
Check.Equal(1, service.QueryTasks(new()).Total);
```

学习通 fixture 移植 `tests/ProbeChecks/ChaoxingSyncChecks.cs` 的 CatalogHtml、Signature、ListHtml 与 RunAsync 中的 handler 地址映射，固定只返回课程 A 和一条任务 A；不要直接 Compile 该整套旧检查。课程卡、签名页和工作行须成套移植，不能只用裸 courseid 字段替代 HTML 容器。其余平台的具体来源如下：

| 路由 | 响应/会话样例来源 | 接入方式 |
| --- | --- | --- |
| yuketang、changjiang | `tests/ProbeChecks/RainClassroomChecks.cs` 的 FixtureHandler 与课程/活动/个人状态响应 | 只移植纯 HTTP handler 与字面量，按 RainPlatform 选择对应 host |
| mooc | `tests/MoocSyncHarness/Fixtures.cs` 的 Handler、Term、LearnedTerm、NewExams | 只移植 RPC 与身份响应；不引入 BlockBrowser/WebView2 |
| xuetang | `tests/XuetangFixtures.cs` 的 XuetangTestData.Metadata.Respond、Catalog、Tasks、Schedule | Compile 纯 fixture，封装 CreateHandler 返回固定 HttpResponseMessage |
| zhihuishu | `tests/MetadataFixtures.cs` 的 PlatformMetadataTestData.Fixtures.Profile、Catalog、FlipList、SharedHomework、SmartList | Compile 纯 fixture，使用其 Key 注入 syntheticMetadataKey，并移植 HTTP 操作到响应的映射 |
| ucampus | `tests/UcampusFixtures.cs` 的 Cookie、Profile、Catalog、CatalogPage、HomeworkShell、DefaultList、ExamShell、DefaultExamList | Compile 纯 fixture，HTTP 地址/方法映射到这些响应；未知地址立即拒绝 |
| pta | `tests/PtaFixtures.cs` 的 PlatformMetadataTestData.PtaFixtures.Cookie 与 Handler | Compile 纯 fixture；调用真实 Handler 覆盖教学课程、题目集及个人状态 |

所有新 handler 都记录方法和路径并拒绝未知请求，不转发网络。各平台在首次 catalogOnly 后断言任务零，确认一个课程后断言课程范围、固定任务数量、状态与日期；错误结构和登录失效分别断言错误码与保留历史，不用各平台同一个模糊“数量大于零”断言。

修改 LinuxChecks.csproj，Compile 链接 `../../src/AccessProbe.Android/MobileSyntheticChecks.cs`、`MobilePasswordlessLogin.cs`、`../XuetangFixtures.cs`、`../MetadataFixtures.cs`、`../UcampusFixtures.cs`、`../PtaFixtures.cs`，其余从 LinuxCore ProjectReference 获得，避免共享类型重复定义；新 LinuxSyncFixtures.cs 位于项目目录自动参与 Compile。MobilePasswordlessLogin 只作既有合成检查依赖，不进入正式 LinuxCore。调用 `MobileSyntheticChecks.RunAsync(Path.Combine(root,"mobile"), protector)` 并断言 Outcome=passed、Checks=47、OfficialNetworkAccessed=false、ProductionDataAccessed=false。

慢 handler 等待 cancellation，验证 cancel 请求在 job 运行时得到响应；若取消发生在首次导入前，任务零写入；已完成课程的事务可以保留，但取消后不得继续写入或记录 completed。选课/会话变化须取消旧 job。

- [ ] **Step 2: 运行红色。** `bash scripts/test-linux-core.sh --suite sync`、`bash scripts/test-linux-ui.sh --group platforms`。
- [ ] **Step 3: 实现复用与 job。** 核心调已有 MobileSync，课程保存显式确认、recordAll=false；不构造虚假课程或默认全选。

```csharp
// service 持有一个 MobileSync；syntheticMetadataKey 仅由隔离测试注入。
sync ??= new AccessProbe.Mobile.MobileSync(store, vault, readerFactory, syntheticMetadataKey);
return await sync.RunAsync(platform, catalogOnly, progress, cancellation);
```

Backend 的 start-sync 立即返回 jobId；后台独立运行、10 分钟 CTS、每平台互斥；主输入循环继续读 cancel-sync。相同平台已有 job 时返回 SYNC003。capture-session、clear-session、select-courses 分派先 await StopPlatformAsync 再提交变化，不能只取消 UI 回调；主输入循环按请求调度异步任务且输出写入串行，取消命令始终可处理。结束事件只在持久化结果后发出；界面按 jobId/generation 更新，不把旧 job 的 success 覆盖新登录。平台页分别显示课程刷新与任务摘要，退出后重启仍可看最近结果。

- [ ] **Step 4: 核对全部 fixtures、人工覆盖优先、部分分页、未知结构、网络错误与会话过期；UI 选课/停止/重试真实调用 backend。**
- [ ] **Step 5: 提交。** `git add src/AccessProbe.LinuxCore src/AccessProbe.LinuxBackend src/AccessProbe.Linux tests`；`git commit -m "feat(linux): integrate selected-course synchronization"`。

### Task 8: 官方任务入口的最新值与归属校验

**Files:**
- Create: `src/AccessProbe.LinuxCore/LinuxAppService.Navigation.cs`, `LinuxPlatformReaders.cs`
- Create: `tests/LinuxChecks/NavigationChecks.cs`
- Modify: `src/AccessProbe.LinuxCore/LinuxContracts.cs`, `src/AccessProbe.LinuxBackend/BackendHost.cs`, `src/AccessProbe.Linux/study_task_hub/dashboard.py`, `tests/LinuxChecks/Program.cs`，注册 navigation suite

**Interfaces:** `ResolveTaskAsync(string,CancellationToken): Task<NavigationView>`；`NavigationView(string Address,string Destination,string Message)`。LinuxPlatformReaders 从当前 MobileVault 提供带验证/续期回存的 cookies，创建既有 ChaoxingHttpReader 与 MetadataHttpReader。

- [ ] **Step 1: 写失败测试。** 手动任务用合法 HTTPS 链接，取得旧详情后编辑链接，再调用 ResolveTaskAsync，期望新地址；错误平台 CollectedNavigationKey 必须拒绝。学习通移植 `tests/ProbeChecks/TaskNavigationChecks.cs` 的 Entry、Target、CaptureHandler、UnreadableContent，断言 content.Reads=0；不直接 Compile 使用 Windows 默认 protector 的旧 RunAsync。U 校园使用 `tests/UcampusFixtures.cs` 的 BoundCourse、LearningCatalog、LearningCourseProgress、LearningProgress，并移植 `tests/ProbeChecks/MetadataPlatformChecks.UcampusUnits.cs` 的 UcampusUnitHttpAsync 和 `MetadataPlatformChecks.Ucampus.cs` 的 UcampusHttpChecks 中的路由/错账号/重做确认场景；所有 TaskStore 显式使用 LinuxDataProtector。

```csharp
var created = service.CreateTask(input with { Link = "https://example.invalid/old" });
service.EditTask(created.View.Task.Id, created.View.Task.Revision,
    input with { Link = "https://example.invalid/new" });
var opened = await service.ResolveTaskAsync(created.View.Task.Id, CancellationToken.None);
Check.Equal("https://example.invalid/new", opened.Address);
```

- [ ] **Step 2: 运行红色。** `bash scripts/test-linux-core.sh --suite navigation`。
- [ ] **Step 3: 复用 TaskNavigationService。** 每次调用独立捕获最终地址，不用共享可变字段：

```csharp
string? opened = null;
var navigation = new AccessProbe.TaskNavigationService(store, ResolvePlatformAddressAsync, address => opened = address);
var result = await navigation.OpenAsync(taskId, cancellation);
return new NavigationView(opened!, result.Destination, AccessProbe.TaskNavigationRules.OpenMessage(result));
```

`ResolvePlatformAddressAsync(Uri,CancellationToken): Task<TaskLinkResolution>` 在本文件分派到已存在 `ChaoxingHttpReader.ResolveTaskAddressAsync` 与 `MetadataHttpReader.ResolveUcampusAsync`；cookie 读取/Set-Cookie 保存前后调用 vault.Validate。Python 只将后台允许地址传给 `Gio.AppInfo.launch_default_for_uri`，失败显示提示；UI 测试在该外部浏览器边界捕获地址，不真的打开官网。

- [ ] **Step 4: 重跑 navigation、tasks、sync，检查没有读取作答正文或日志泄漏完整签名 URL。**
- [ ] **Step 5: 提交。** `git add src/AccessProbe.LinuxCore src/AccessProbe.LinuxBackend src/AccessProbe.Linux tests`；`git commit -m "feat(linux): preserve official task navigation validation"`。

### Task 9: 自动采集、系统提醒、设置与退出

**Files:**
- Create: `src/AccessProbe.LinuxCore/LinuxAppService.Reminders.cs`
- Create: `src/AccessProbe.Linux/study_task_hub/scheduler.py`, `notifications.py`, `settings_window.py`
- Create: `tests/LinuxChecks/ReminderChecks.cs`, `tests/test_linux_scheduler.py`
- Modify: `src/AccessProbe.Linux/study_task_hub/application.py`, `window.py`, `src/AccessProbe.LinuxCore/LinuxContracts.cs`, `src/AccessProbe.LinuxBackend/BackendHost.cs`, `tests/LinuxChecks/Program.cs`，注册 reminders suite

**Interfaces:** `TakeDueNotifications(): IReadOnlyList<NotificationView>`；`NotificationView(string Id,string TaskId,string Title,string Body)`；`AcknowledgeNotifications(IReadOnlyList<string>,bool): void`；`ReleaseNotifications(IReadOnlyList<string>): void`。`AppScheduler(bridge,send_notification,clock=time.monotonic)` 提供 `tick()`、`stop()`；send_notification 返回发送请求是否成功。

- [ ] **Step 1: 写失败测试。** 固定 core utcNow 为 `2026-11-13T12:00:00Z`，任务截止 `2026-11-14T12:00:00Z`，首次 TakeDue 有一条到期前提醒；完成后对应未发项为空。失败 ack 后下一次满足 retry 时间可再次领取，通知关闭时不领取。测试 scheduler 的每 30 分钟采集与每 60 秒提醒检查，busy/login/edit 中不启动重叠采集。

```csharp
var notifications = service.TakeDueNotifications();
Check.Equal(1, notifications.Count);
service.AcknowledgeNotifications(notifications.Select(n => n.Id).ToArray(), false);
service.SetTaskState(task.View.Task.Id, task.View.Task.Revision, "completed");
Check.Equal(0, service.TakeDueNotifications().Count);
```

- [ ] **Step 2: 运行红色。** `bash scripts/test-linux-core.sh --suite reminders`、`python3 -m unittest discover -s tests -p test_linux_scheduler.py`。
- [ ] **Step 3: 实现现有 reminder 租约/重试与本地生命周期。** 核心分别复用 `store.TakeDue(utcNow())`、`store.RecordNotificationRequest(ids,success,utcNow())`、`store.ReleaseReminderClaims(ids)`，映射 DueReminder 为 NotificationView。Gtk 的 Gio.Notification 以 title/body发送，发送请求成功才 ack。Scheduler 计时独立于 wall clock，停止时移除 GLib sources。用户退出顺序：拒绝新操作→取消 job→保存有效登录快照→释放未发通知领取→关闭 backend；总等待上限 5 秒，超时有提示。

```python
notification = Gio.Notification.new(item["title"])
notification.set_body(item["body"])
application.send_notification(item["id"], notification)
```

通知实际显示由 GNOME 设置决定，测试不能把 send_notification 未抛异常当成用户目视已收到。退出后无定时器和子进程残留；再次启动加载原设置/任务。

- [ ] **Step 4: 验证通知关闭、失败重试、课程取消、应用退出、后台重启；本机 GNOME 通知与任务回到首页进行人工检查。**
- [ ] **Step 5: 提交。** `git add src/AccessProbe.LinuxCore src/AccessProbe.LinuxBackend src/AccessProbe.Linux tests`；`git commit -m "feat(linux): add local scheduling notifications and shutdown"`。

### Task 10: 自包含 deb、最终包验收与说明

**Files:**
- Create: `scripts/build-linux.sh`, `scripts/verify-linux-package.sh`
- Create: `packaging/linux/control.in`, `study-task-hub.desktop`, `study-task-hub` launcher
- Create: `tests/test_linux_package.py`, `docs/Ubuntu安装与使用.md`
- Create at runtime: `artifacts/linux/` 包、校验、许可证和验证摘要（该目录已忽略）
- Create: `docs/releases/ubuntu-1.2.0~ubuntu1/verification.json`, `release-notes.md`, `release-assets.json`，保存可提交的发布清单与结果，不复制数据、会话或包二进制
- Modify: `README.md`，添加准确的 Ubuntu 使用和验证范围

**Interfaces:** `bash scripts/build-linux.sh --version 1.2.0~ubuntu1`；输出 `artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb`、`SHA256SUMS.txt`、`verification.json`。`bash scripts/verify-linux-package.sh <deb-path>` 仅解包和验证，不默认安装系统包或启动正式库。

- [ ] **Step 1: 写行为失败测试。** 测试真正执行构建/解包，而非 grep control 文件。验证 deb 架构、依赖、图标与 desktop 路径；在仓库之外，用包内 backend 与隔离数据跑 create-task/snapshot，环境中去掉 DOTNET_ROOT/DOTNET_CLI_HOME/GI_TYPELIB_PATH：

```python
subprocess.run(["dpkg-deb", "--extract", deb_path, unpacked], check=True)
backend = pathlib.Path(unpacked) / "opt/study-task-hub/backend/StudyTaskHub.LinuxBackend"
request = '{"version":1,"id":1,"command":"snapshot","args":{"state":"all"}}\n'
result = subprocess.run([str(backend), "--data-root", isolated_root],
    input=request, text=True, capture_output=True, cwd=outside_repo, env=clean_env, timeout=15)
assert result.returncode == 0
assert json.loads(result.stdout.splitlines()[0])["result"]["total"] == 0
```

`unpacked/isolated_root/outside_repo` 来自 tempfile.TemporaryDirectory。clean_env 保留系统 GUI 会话变量，删除所有 DOTNET_*、NUGET_PACKAGES、GI_TYPELIB_PATH、PYTHONPATH、PYTHONHOME、LD_LIBRARY_PATH、GTK_PATH、GIO_EXTRA_MODULES，并将 PATH 固定为 `/usr/bin:/bin`。再用解包得到的 `usr/bin/study-task-hub --data-root <isolated-root>` 从 outside_repo 启动最终前端；缺失正式 GUI 依赖返回可理解的安装提示，不记通过。

- [ ] **Step 2: 运行红色。** `python3 -m unittest discover -s tests -p test_linux_package.py`。
- [ ] **Step 3: 实现 publish 与包结构。** 使用 Task 3 已锁定的 AssemblyName `StudyTaskHub.LinuxBackend`，保持关闭 trimming：

```bash
"$taskDotnet" publish "$taskRoot/src/AccessProbe.LinuxBackend/AccessProbe.LinuxBackend.csproj" \
  -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=false \
  -o "$taskStage/opt/study-task-hub/backend"
dpkg-deb --root-owner-group --build "$taskStage" "$taskOutput/study-task-hub_${taskVersion}_amd64.deb"
```

taskRoot/taskStage/taskOutput/taskVersion 是脚本局部变量，不能覆盖 HOME/CODEX_HOME；stage 位于 `.local/linux-packaging/<guid>`。复制前端到 `/opt/study-task-hub/ui`，launcher 到 `/usr/bin/study-task-hub`，desktop 到 `/usr/share/applications`，沿用仓库主图标到 hicolor。launcher 根据自身所在的 usr/bin 找到同一安装树的 opt，因此系统安装和临时解包均可运行，不使用仓库路径：

```sh
#!/bin/sh
set -eu
taskLauncherDir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
taskAppRoot="$(CDPATH= cd -- "$taskLauncherDir/../../opt/study-task-hub" && pwd -P)"
exec /usr/bin/python3 "$taskAppRoot/ui/main.py" \
  --backend "$taskAppRoot/backend/StudyTaskHub.LinuxBackend" "$@"
```

main.py 在包内直接启动时也使用 Task 4 的相邻 backend 规则。desktop 使用 `Exec=study-task-hub`、`Terminal=false`、`Icon=study-task-hub`；launcher 和 backend 保留可执行权限。

control 依赖至少包括 `python3 (>=3.12), python3-gi, gir1.2-gtk-4.0 (>=4.14), gir1.2-adw-1 (>=1.5), gir1.2-webkit-6.0 (>=2.44), libsqlite3-0 (>=3.37), libc6 (>=2.35), libgcc-s1, libstdc++6, zlib1g, libssl3t64 | libssl3, libicu78 | libicu74`；按 ldd 和干净环境实测补充必要库。用户目录不进包；postinst/prerm 不删除用户数据。收集 .NET/NuGet、GTK/WebKit 与系统依赖的许可来源，运行托管和系统依赖安全审查。

- [ ] **Step 4: 跑全部适用检查与包验收。** 顺序执行以下命令；前端检查使用正式系统 GI 依赖，不保留开发 GI_TYPELIB_PATH：

```bash
bash scripts/test-linux-core.sh --suite all
python3 -m unittest discover -s tests -p test_linux_bridge.py
bash scripts/test-linux-ui.sh --group all
python3 -m unittest discover -s tests -p test_linux_package.py
bash scripts/verify-linux-package.sh artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb
```

保存提交、依赖、SHA256、每类结果、Ubuntu26.04/24.04/账号/混合DPI状态。测量最终包冷启动到首页、5,000 条隔离任务首屏查询耗时，记录目标和实测值。截图来自最终包，不能沿用开发截图冒充最终包证据。

在可恢复的 Ubuntu 26.04 测试 VM/环境中通过 `apt install ./<deb>` 验证菜单与命令启动；使用测试账号的 XDG 数据目录创建任务和登录 fixture，安装递增候选版本验证任务、protection.key 哈希与解密保持，再 remove/purge 验证该用户数据仍存在。Ubuntu 24.04 单独重复依赖、启动、GUI 与核心检查。测试环境操作不得触及本机正式数据；仅解包时标为“解包预览”，A14 不能记 passed。本人账号按 A15 独立检查；未做则包标候选，不能报告八个平台真实登录全通过。A01–A14 中本机核心或正式依赖失败时停止候选包交付。

- [ ] **Step 5: 文档与提交。** README/Ubuntu说明明确安装 `sudo apt install ./<deb>`、数据位置、关闭程序的后台行为、手动任务、官方登录→选课→采集、会话到期和平台覆盖。构建通过后更新 `docs/releases/ubuntu-<version>/`：记录 version、sourceCommit、architecture、sha256、dependencies、各验收 pass/fail/not_run、平台账号状态与覆盖限制；release-assets.json 列出交付文件、大小和哈希，与 artifacts 实际输出一致。确认升级保留数据及卸载不删除用户目录。`git add scripts packaging/linux tests/test_linux_package.py docs/Ubuntu安装与使用.md docs/releases/ubuntu-1.2.0~ubuntu1 README.md`；`git commit -m "feat(linux): package verified Ubuntu desktop candidate"`。

## 需求与验收覆盖表

| 需求 | Task | 验收 |
| --- | --- | --- |
| U01 | 10 | A01、A14 |
| U02 | 1、2、10 | A02、A14 |
| U03 | 2、4 | A04 |
| U04 | 2、5 | A03 |
| U05 | 2、5 | A03 |
| U06 | 7、9 | A05、A12 |
| U07 | 6、7 | A09 |
| U08 | 6 | A06、A13 |
| U09 | 6 | A07、A08 |
| U10 | 6、7、9 | A08、A15 |
| U11 | 7、9 | A09、A10 |
| U12 | 3、7、9 | A10 |
| U13 | 8 | A11 |
| U14 | 9 | A12 |
| U15 | 2、3、9 | A03、A10、A12 |
| U16 | 4、5、6、10 | A13 |
| U17 | 10 | A01、A14、A15、A16 |
| U18 | 1–10 的范围约束 | A16 |

## 接口与边界检查

| 生产者→消费者 | 共享内容 | 一致性要求 |
| --- | --- | --- |
| 1→2/6 | 数据路径、保护、SQLite | 同一 dataRoot，密钥不重建；只使用系统 provider |
| 2→3/4/5 | TaskInput/TaskQuery/TaskPage/TaskDetail | JSON camelCase；首屏无编辑链接，GetTask 返回链接；状态字符串映射单一 |
| 3→4/6/7/9 | request_async 与事件 | 后台线程读取，GTK 主线程应用；未确认请求不能自动重放 |
| 6→7/8 | MobileVault 会话与新保存状态 | Cookie 指纹校验；新会话先 saved；无 token/凭据诊断 |
| 7→9 | jobId、cancel、退出 | 输入循环不中断；job 不覆盖新登录/选课；退出停止全部 job |
| 8→GTK | NavigationView | 只打开本次后台校验结果；不用旧详情链接 |
| 9→10 | 生命周期与通知 | 无 systemd/自启；默认保留用户数据 |
| 10→全部 | 包内运行 | 不依赖本地 SDK/仓库；正式依赖缺失不得记为通过 |

## 自查与执行出口

执行前按本表自查 18 项 U 需求、16 项 A 验收和 13 条 G 约束均有落点；类型/字段/命令名以上述 Interfaces 为准。若执行中发现 WebKit API 版本或系统 provider 不成立，先做最小复现并调整该适配层，不能通过关闭安全校验或换成空数据获得通过。

文档本身不需要代码测试；其中的构建和测试命令是未来实施时的命令，本次编写不等于已运行。完成各 Task 后以新验证输出确认结果。最终本机包通过后按 finishing-a-development-branch 处理本地提交与集成；外部发布另行明确授权。

交付优先级：本机 Ubuntu 候选包 → Ubuntu 24.04 独立兼容验收 → 本人实际平台验收补齐 → 返回局域网对等同步计划。
