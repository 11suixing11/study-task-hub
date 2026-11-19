# Windows 简化接入与任务首页 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Windows WPF 客户端中把首页、平台接入和官方登录串成一条清晰的单窗口流程，让用户能直接看到下一步并在失败时就地恢复。

**Architecture:** 保留现有 TaskDashboardWindow 单窗口路由、平台同步服务、WebView2 页面和 TaskStore 数据模型。新增一个只负责展示的首页接入摘要状态层，统一从现有平台会话、课程记录和同步状态生成卡片/摘要文案；连接页继续由现有平台服务判定真实登录结果，UI 只统一步骤、主按钮和恢复动作。

**Tech Stack:** .NET 10 WPF、WPF-UI 4.3.0、WebView2、现有自定义测试 harness、合成 TaskStore 和 WebView2 fixture。

## Global Constraints

- 用户完成官方登录；客户端不新增账号密码采集，不代替答题或提交。
- 正式任务库、Cookie 和 profile 保留；所有自动化验证使用独立测试目录和合成响应。
- 不改变平台解析器、同步接口和 Android 行为；只调整 Windows 首页、接入状态和连接页展示。
- 继续使用 `FluentTheme.xaml` 作为 Windows 主题入口，并保持现有默认窗口尺寸和最小尺寸可用。
- 每种错误状态必须说明发生了什么、当前影响和可执行的下一步。

---

### Task 1: 锁定接入摘要与平台状态的用户行为

**Files:**
- Create: `src/AccessProbe/PlatformAccessPresentation.cs`
- Test: `tests/ProbeChecks/PlatformAccessPresentationChecks.cs`
- Modify: `tests/ProbeChecks/Program.cs`

**Interfaces:**
- Consumes: `PlatformSessionInfo`, `CourseRecording`, `ConnectionReport?`, `platformStatuses` 的运行/警告状态。
- Produces: `PlatformAccessPresentation.For(...)`，返回平台名称、状态标题、状态说明、主按钮文本、摘要分类和是否需要处理的不可变结果。

- [ ] **Step 1: Write the failing tests**

测试必须覆盖四种会影响首页主操作的真实行为：

```csharp
var notConnected = PlatformAccessPresentation.For("pta", session, recording, null, running: false, warning: false);
Check(notConnected.State == PlatformAccessState.NotConnected, "unconnected platform is actionable");
Check(notConnected.PrimaryAction == "连接平台", "unconnected platform uses a plain primary action");

var waitingCourse = PlatformAccessPresentation.For("pta", readySession, recording with { CatalogCheckedAtUtc = now }, verifiedReport, false, false);
Check(waitingCourse.State == PlatformAccessState.CourseSelectionRequired, "verified catalog with no selection asks for courses");
Check(waitingCourse.PrimaryAction == "确认课程", "course selection has one clear action");

var syncing = PlatformAccessPresentation.For("pta", readySession, recording, verifiedReport, true, false);
Check(syncing.State == PlatformAccessState.Syncing, "running sync is visible as syncing");
Check(syncing.PrimaryAction == "查看进度", "running sync does not offer a misleading connect action");

var failed = PlatformAccessPresentation.For("pta", readySession, recording, verifiedReport with { ProblemOutcomes = ["network_error"] }, false, true);
Check(failed.State == PlatformAccessState.NeedsRetry, "warning platform is actionable as retry");
Check(failed.PrimaryAction == "重新连接", "warning platform gets a recovery action");
```

使用现有 `TaskStore` 记录类型和测试 fixture，期望值由用户行为独立写出，不调用被测 presentation 结果生成器计算断言。

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `& .tools\dotnet\dotnet.exe run --project tests\ProbeChecks\ProbeChecks.csproj`

Expected: FAIL because `PlatformAccessPresentation` does not exist yet.

- [ ] **Step 3: Implement the smallest presentation model**

在 `PlatformAccessPresentation.cs` 中创建 `PlatformAccessState`、`PlatformAccessPresentation` 和 `For(...)`。只读取已有会话/课程/报告状态，不触发 I/O，不修改数据库。状态优先级固定为：运行中、会话过期或报告明确失败、未接入、需要选课、已同步、已登录待刷新。

- [ ] **Step 4: Register and run the focused test**

在 `tests/ProbeChecks/Program.cs` 注册 `PlatformAccessPresentationChecks.Run(checks)`，重新运行相同命令。

Expected: PASS，并继续保留现有 ProbeChecks 的默认全部检查。

- [ ] **Step 5: Commit the isolated behavior**

```bash
git add src/AccessProbe/PlatformAccessPresentation.cs tests/ProbeChecks/PlatformAccessPresentationChecks.cs tests/ProbeChecks/Program.cs
git commit -m "feat: add Windows platform access presentation"
```

### Task 2: 在首页加入首要接入摘要和清晰空状态

**Files:**
- Modify: `src/AccessProbe/TaskDashboardWindow.xaml`
- Modify: `src/AccessProbe/TaskDashboardWindow.xaml.cs`
- Modify: `src/AccessProbe/TaskDashboardWindow.Platforms.cs`
- Modify: `src/AccessProbe/FluentTheme.xaml`
- Test: `tests/TaskDashboardHarness/Program.cs`

**Interfaces:**
- Consumes: Task 1 的 `PlatformAccessPresentation`，以及现有 `ShowPlatformManager` / `PlatformConnectionRequested` 路由。
- Produces: `OnboardingBanner`、`OnboardingTitleText`、`OnboardingDescriptionText`、`OnboardingActionButton` 和 `OnboardingSecondaryButton`，由首页刷新时更新。

- [ ] **Step 1: Add the failing dashboard assertions**

在 `VerifyAsync` 的首页加载场景增加以下断言：

```csharp
Check(Find<Border>("OnboardingBanner") is { Visibility: Visibility.Visible },
    "dashboard exposes a first-use connection summary");
Check(Find<Button>("OnboardingActionButton").Content is "开始接入" or "查看需要处理的平台",
    "dashboard exposes one plain-language primary connection action");
Check(Find<TextBlock>("OnboardingDescriptionText").Text.Contains("作业"),
    "connection summary explains the value in user terms");
```

- [ ] **Step 2: Run the dashboard harness and verify the assertions fail**

Run: `& .tools\dotnet\dotnet.exe run --project tests\TaskDashboardHarness\TaskDashboardHarness.csproj -- --project-root (Get-Location).Path --verify --run-root (Join-Path (Get-Location) '.local\plan-red-dashboard')`

Expected: FAIL because the new named controls are absent.

- [ ] **Step 3: Add the banner layout and theme styles**

在任务工作区搜索框之前加入一个可见性可控的 `Border`，包含标题、说明和一个主按钮/一个次级按钮。主按钮通过首页事件连接到接入中心或当前需要处理的平台；次级按钮可关闭摘要。使用主题资源定义浅蓝背景、细边框和统一圆角，避免添加第二套颜色。

- [ ] **Step 4: Update the summary from real store state**

在 `RefreshTasks` 完成任务计数和 `UpdatePlatformCard` 后调用 `UpdateOnboardingSummary()`。该方法读取支持桌面自动同步的平台状态，优先显示需要重试的平台，其次显示未接入平台，全部已接入时显示最近同步结果；没有任务时保持价值说明和“开始接入”。不把无任务直接解释为登录失败。

- [ ] **Step 5: Wire actions through existing single-window navigation**

“开始接入”打开接入中心首个平台或统一平台管理入口；“查看需要处理的平台”打开第一个需要重试/确认课程的平台；关闭按钮只隐藏摘要，不清除状态。继续使用 `ShowPlatformManager`、`ConnectPlatform` 和现有连接事件，不创建第二个 dashboard window。

- [ ] **Step 6: Run the dashboard harness and verify it passes**

Run the same `TaskDashboardHarness` command. Expected: PASS and updated screenshots/report show the banner remains inside the default and minimum layouts.

- [ ] **Step 7: Commit the dashboard slice**

```bash
git add src/AccessProbe/TaskDashboardWindow.xaml src/AccessProbe/TaskDashboardWindow.xaml.cs src/AccessProbe/TaskDashboardWindow.Platforms.cs src/AccessProbe/FluentTheme.xaml tests/TaskDashboardHarness/Program.cs
git commit -m "feat: add plain-language dashboard onboarding"
```

### Task 3: Simplify connection status controls and recovery copy

**Files:**
- Modify: `src/AccessProbe/ConnectionStatusPanel.cs`
- Modify: `src/AccessProbe/ConnectionFeedback.cs`
- Modify: `src/AccessProbe/ConnectionWindowLayout.cs`
- Modify: `src/AccessProbe/MetadataConnectionWindow.cs`
- Modify: `src/AccessProbe/MoocConnectionWindow.cs`
- Modify: `src/AccessProbe/RainConnectionWindow.cs`
- Modify: `src/AccessProbe/XuetangConnectionWindow.cs`
- Modify: `src/AccessProbe/MainWindow.xaml.cs`
- Modify: `tests/ProbeChecks/ConnectionFeedbackChecks.cs`
- Modify: `tests/TaskDashboardHarness/ConnectionUxChecks.cs`
- Modify: `tests/TaskDashboardHarness/ConnectionLayoutChecks.cs`

**Interfaces:**
- Consumes: Existing `ConnectionReport`, `ConnectionFeedback`, page failure outcomes and platform sync events.
- Produces: A consistent left-side connection guide with one primary recovery action, readable four-step guidance, and the same recovery text on all Windows connection pages.

- [ ] **Step 1: Write failing copy and action tests**

Add behavior checks for login-required, network failure, empty catalog, partial catalog and verified catalog states:

```csharp
var login = ConnectionFeedback.From(report with { LoginRequired = true, AccountVerified = false });
Check(login.NextStep.Contains("刷新登录页") || login.NextStep.Contains("重新打开"),
    "login-required state offers a concrete page recovery action");
var network = ConnectionFeedback.From(report with { ProblemOutcomes = ["network_error"] });
Check(network.Explanation.Contains("网络"), "network failure explains the impact in plain language");
var empty = ConnectionFeedback.From(report with { CatalogCovered = true, Courses = 0, CatalogOnly = true });
Check(empty.Title.Contains("课程"), "empty catalog is distinguished from login failure");
```

In the UI harness assert only one visible primary action is presented for each synthetic state and that the page keeps the official WebView usable at minimum size.

- [ ] **Step 2: Run focused checks and verify the new checks fail**

Run: `& .tools\dotnet\dotnet.exe run --project tests\ProbeChecks\ProbeChecks.csproj`

Run: `& .tools\dotnet\dotnet.exe run --project tests\TaskDashboardHarness\TaskDashboardHarness.csproj -- --project-root (Get-Location).Path --verify --run-root (Join-Path (Get-Location) '.local\plan-red-connection')`

Expected: the new wording/action assertions fail against the current generic retry text or multiple visible actions.

- [ ] **Step 3: Implement the smallest status panel change**

Keep `ConnectionReport` as the source of truth. Update `ConnectionFeedback.From`, `PageFailed`, `Interrupted` and `Checking` copy so each state has one plain-language main action. Make the check button text follow the state (`刷新登录页`, `重试读取课程`, or `检查接入`) and keep course selection as the only second action when it is actually available. Keep the diagnostics expander collapsed by default.

- [ ] **Step 4: Apply the shared guide layout to all Windows connection pages**

Use `ConnectionWindowLayout` to give the guide a stable readable width, preserve the official WebView minimum size, and keep header actions in a consistent order: `返回任务首页`, main login/recovery action, advanced cleanup only when needed. Do not alter platform URL or service calls.

- [ ] **Step 5: Run focused checks and verify they pass**

Run both focused commands again. Expected: PASS with no official network access and no change to Android compilation paths.

- [ ] **Step 6: Commit the connection slice**

```bash
git add src/AccessProbe/ConnectionStatusPanel.cs src/AccessProbe/ConnectionFeedback.cs src/AccessProbe/ConnectionWindowLayout.cs src/AccessProbe/MetadataConnectionWindow.cs src/AccessProbe/MoocConnectionWindow.cs src/AccessProbe/RainConnectionWindow.cs src/AccessProbe/XuetangConnectionWindow.cs src/AccessProbe/MainWindow.xaml.cs tests/ProbeChecks/ConnectionFeedbackChecks.cs tests/TaskDashboardHarness/ConnectionUxChecks.cs tests/TaskDashboardHarness/ConnectionLayoutChecks.cs
git commit -m "feat: simplify Windows login recovery states"
```

### Task 4: Build and verify the Windows preview package

**Files:**
- Create: `packaging/windows/使用说明-1.1.9测试版.txt`
- Create: generated files under the newest directory in `artifacts/releases/`
- Create: `C:/Users/yuki/Desktop/聚合平台安装包/Windows-1.1.9-体验优化-20261117/...` delivery copy

**Interfaces:**
- Consumes: Updated Windows source, existing isolated test harnesses and packaging scripts.
- Produces: Verified Windows self-contained publish, portable ZIP, Setup EXE, checksums, screenshots and a short usage note.

- [ ] **Step 1: Run the full Windows business and dashboard checks**

Run: `pwsh -NoProfile -File scripts\build.ps1 -VerifyOnly`

Expected: existing business, dashboard, connection and packaging checks complete with `outcome = passed`; if baseline failures exist, stop and report them before packaging.

- [ ] **Step 2: Build a unique release after all checks pass**

Run: `pwsh -NoProfile -File scripts\build.ps1`

Expected: a new release directory appears under `artifacts\releases\`; its `verification\verification.json` has `outcome = passed` and the Windows publish output is present.

- [ ] **Step 3: Build the Windows installer and portable package**

Run: `$releaseVersion = Get-ChildItem artifacts\releases -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name; pwsh -NoProfile -File scripts\build-installer.ps1 -ReleaseVersion $releaseVersion -PackageVersion 1.1.9 -UsageFile packaging\windows\使用说明-1.1.9测试版.txt`

Expected: Setup EXE and portable ZIP are created only from the verified release payload.

- [ ] **Step 4: Copy the final delivery folder safely**

Create `C:\Users\yuki\Desktop\聚合平台安装包\Windows-1.1.9-体验优化-20261117`, verify the resolved path is inside `C:\Users\yuki\Desktop\聚合平台安装包`, and copy only the generated Setup, portable ZIP, checksums, usage note, verification summary and third-party license directory.

- [ ] **Step 5: Launch the isolated portable build for visual inspection**

Run the generated portable launcher with an isolated data directory. Confirm the new banner, platform status, default window and minimum window remain usable; do not launch the installed app against the formal database.

- [ ] **Step 6: Report exact output paths and fresh verification evidence**

Include the release build ID, package paths, verification command results, and any remaining manual checks. Do not claim completion if any required check or visual inspection fails.

