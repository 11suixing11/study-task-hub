# 同步与任务详情 Implementation Plan

> **For agentic workers:** Use executing-plans inline，项目不启动子代理。

**Goal:** 完成 F05/F06 用户可见同步诊断与任务来源依据。

**Architecture:** MobileSync 内存汇总转为 LinuxSyncReport，按平台/阶段 settings 保存；UI 展示标准报告及现有任务证据。

**Tech Stack:** .NET 10 / SQLite；GTK 4 / Python。

**Spec:** `docs/superpowers/specs/2026-11-14-sync-task-details-design.md`

## Global Constraints

- 正式数据、登录环境保留；本地 fixture 测试。
- 没有任务不是覆盖成功；完整覆盖依赖对应采集器汇总。
- 本地数量与本轮观察数量明确区分，完整问题不截断。

## Task 1 — 同步报告

**Files:** `MobileSync.cs`、`LinuxAppService.Sync.cs`；新增 `LinuxSyncReports.cs`、`TaskStore.LinuxSyncReports.cs`；修改 `BackendHost.cs`、`SyncChecks.cs`；新增 `sync_details.py`，修改 `platform_window.py`。

**Interfaces:** `GetSyncReports(string): SyncReportPage`；`get-sync-reports {platform}`。报告分别为 Catalog/Tasks，包含 Outcome、StartedAtUtc、FinishedAtUtc、LastSuccessfulAtUtc、Complete、Metrics、Problems 和课程计数/覆盖。

- [x] 在 PTA fixture 目录读取后检查 Catalog 存在、Tasks 为 null；选课后采集检查完整汇总、5 条任务、课程覆盖及问题，取消后记录 cancelled。
- [x] 在八平台 fixtures 中检查 metrics 和完整问题返回；保存报告使用 settings 单事务，上一成功时间只在完整成功时更新。
- [x] GTK 详情展示两阶段与全部问题，重试复用平台 start-sync；运行 sync/core 和 GUI 检查。

## Task 2 — 任务依据

**Files:** 新增 `task_details.py`，修改 `dashboard.py`；新增 `test_linux_details.py`。

**Interface:** `format_task_detail(detail): str`，消费完整 TaskDetail。

- [x] 创建本地任务包含进度/成绩，实际选中后检查详情含对应值；构造完整采集元数据验证平台状态、日期依据与 overrides 保留。
- [x] 详情文字按当前状态、补充信息、平台依据和人工修改编排，使用现有滚动详情区。
- [x] 运行 GUI 全套与 `git diff --check`，审查并提交。
