# Ubuntu 优先逻辑修复 Implementation Plan

> **For agentic workers:** Use executing-plans in the current session. 项目约定不默认启动子代理。按以下独立交付任务执行，每项先观察失败、实现、验证并提交。

**Goal:** 修复功能审计中已复现的 F01/F02/F03，为课程管理、同步诊断和数据恢复建立可靠基础。

**Architecture:** 复用当前 LinuxCore、SQLite 与子进程协议。新增 Linux 专用偏好事务，后台/手动采集共用资格判断，学期选择采用独立学期记录并保留旧客户端可读的当前记录。

**Tech Stack:** .NET 10 / 系统 SQLite；Python 3.12+ / GTK 4 / libadwaita。

**Spec:** `docs/功能缺口排查-2026-11-13.md` 中 F01/F02/F03。

## Global Constraints

- 沿用 `/home/core/桌面/聚合平台` 与 `codex/ubuntu-desktop` 分支，保留既有未提交局域网设计。
- 正式任务库和登录环境保留；所有验证使用独立数据目录、合成 Cookie 与本地响应。
- 不改变 Windows/Android 的现有登录或采集契约；不读取题目/答案，不代提交。
- 旧版本当前选课记录继续可读，新学期档案使用新增 settings key，写入与选择处于同一事务。
- 不把网络错误、元数据不完整或未确认选择视为空任务。

## Task 1 — F01 偏好保存事务

**Files:** 新增 `src/AccessProbe.LinuxCore/TaskStore.LinuxPreferences.cs`；修改 `LinuxAppService.cs`、`tests/LinuxChecks/TaskServiceChecks.cs`。

**Interface:** `TaskStore.SaveLinuxPreferences(AppSettings requested): void`。只更新 NotificationsEnabled、AutomaticSyncEnabled、ChaoxingAutomaticSyncEnabled；DisclaimerSeen 保留已确认状态。LastSyncAtUtc、LastSyncOutcome、LastSyncSummary 从同一事务内最新记录保留。

- [ ] 在测试中取得旧设置，直接以独立 TaskStore 保存 `LastSyncOutcome=completed`，再调用 `service.SaveSettings(cached with { NotificationsEnabled=false })`；断言结果仍为 completed 且通知关闭。
- [ ] 运行 `bash scripts/test-linux-core.sh --suite tasks`，观察旧结果覆盖失败。
- [ ] 在 `lock(gate)` 与 SQLite 事务内 `ReadSettings`，构造 `latest with { ...偏好字段... }`，写 `settings/app` 并提交；LinuxAppService 调此新方法。
- [ ] 重跑 tasks/reminders/ipc，确认同步记录与开关持久化。
- [ ] 提交该独立修复。

## Task 2 — F02 统一采集资格

**Files:** 新增 `src/AccessProbe.Linux/study_task_hub/sync_rules.py`；修改 `scheduler.py`、`window.py`、`platform_window.py`、`tests/test_linux_scheduler.py`。

**Interfaces:** `has_recorded_official_courses(recording): bool`；`can_sync_tasks(platform_view): bool`。确认选择且至少一门 Available 官方课程被选中才具备任务采集资格；登录状态可复用和 LoginUri 必须存在。

- [ ] 用真实后台保存手动课程并保存合成登录，拦截 start-sync 网络边界；调用 scheduler.platforms_loaded，断言没有 start-sync 请求。
- [ ] 使用一门已选可用官方课程、未选官方课程、历史不可用课程、RecordAll 四组数据分别检查资格。
- [ ] 运行 scheduler 测试，观察仅手动课程被错误调度失败。
- [ ] 三个 UI/调度入口复用新规则，课程目录刷新继续保留独立 catalogOnly 流程。
- [ ] 重跑 scheduler、platform 和 SmartCourse GUI 检查。
- [ ] 提交该独立修复。

## Task 3 — F03 多学期选择档案

**Files:** 修改 `LinuxCourseSelection.cs`、`TaskStore.LinuxCourseSelection.cs`、`tests/LinuxChecks/CourseSelectionChecks.cs`、`tests/test_linux_ui.py`。

**Interface:** 学期快照包含 Semester、ReviewedKeys、SelectedKeys、RecordAll；key 为 `linux-course-period:<platform>:<yearStart>:<term>`。现有 `linux-course-selection:<platform>` 继续保存当前学期兼容记录。

- [ ] 保存秋季排除、春季不同选择，再查询秋季；断言秋季排除仍在，春季选择也在。重建服务后重复断言。
- [ ] 在仅有旧版单学期记录的数据库中保存新学期，断言旧学期档案保留。验证旧版格式仍有 Semester/ReviewedKeys。
- [ ] 运行 courses suite，观察跨学期排除丢失。
- [ ] 查询指定学期的快照；若为当前兼容记录所属学期，用最新全局选择补充，历史学期用独立快照。首次新格式保存时先快照原当前学期，再保存新学期、全局课程选择和兼容记录。
- [ ] 仍以 Revision 保护事务；未知或损坏档案返回 DB002，不静默丢失选择。
- [ ] 重跑 courses、SmartCourse、全部 Linux 核心与 GUI 回归。
- [ ] 提交并更新审计、开发进度与新包验证清单。

## 后续子项目顺序

F04 稳定手动课程管理与任务关联；F05/F06 同步与任务详情；F07/F08 备份恢复与通知处理。分别建立适合其数据/接口的设计与实施步骤，不将它们混入上述修复。F10/F11 按已定顺序在 Ubuntu 闭环与验收后推进，F12 根据对应客户端环境处理。
