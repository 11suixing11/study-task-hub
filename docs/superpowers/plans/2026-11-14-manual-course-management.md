# 自定义课程管理 Implementation Plan

> **For agentic workers:** Use executing-plans inline; 用户约定不启动子代理。

**Goal:** 完成 F04 稳定身份、名称/学期编辑、归档/恢复/空课程移除与任务选择课程。

**Architecture:** Linux 专用 settings 登记；复用 TaskStore 私有事务及任务写入；IPC 与 GTK 窗口接入。保留旧名称派生 key，新增使用随机身份。

**Tech Stack:** .NET 10 / SQLite；Python / GTK 4 / libadwaita。

**Spec:** `docs/superpowers/specs/2026-11-14-manual-course-management-design.md`

## Global Constraints

- 当前分支与目录开发；正式数据库与登录保留，使用独立测试目录。
- 课程与任务绑定、Revision、提醒及选课档案一并事务提交。
- 不修改采集任务官方课程身份，不采集作答内容或提交任务。

## Task 1 — 数据与服务

**Files:** 新增 `LinuxManualCourses.cs`、`TaskStore.LinuxManualCourses.cs`、`tests/LinuxChecks/ManualCourseChecks.cs`；修改 `LinuxContracts.cs`、`LinuxAppService.Tasks.cs`、`TaskStore.LinuxCourseSelection.cs`、`LinuxCourseSelection.cs`、`tests/LinuxChecks/Program.cs`。

**Interfaces:** `GetManualCourses(string): ManualCoursePage`；`UpdateManualCourse(ManualCourseEdit): ManualCoursePage`；`RemoveManualCourse(string,string,long): ManualCoursePage`；TaskInput 增加可选 RecordingCourseKey。

- [ ] 写并观察测试失败：登记“数学”，创建同名旧手动任务，改名后断言相同 key、新名称、Revision 增加；归档任务不在默认清单，包含未选课程时仍可见；有任务移除返回 COURSE003。
- [ ] 登记读旧说明作为迁移，新增写登记；管理与任务保存核对 key，在一个事务中绑定并更新相关数据。
- [ ] 运行 `bash scripts/test-linux-core.sh --suite courses` 和 `--suite all`。

## Task 2 — IPC 与 GTK

**Files:** `BackendHost.cs`、新增 `manual_courses.py`；修改 `platform_window.py`、`task_editor.py`；测试 `test_linux_ui.py`。

**Interfaces:** `get-manual-courses {platform}`；`edit-manual-course {platform,key,revision,name,semester,archived}`；`remove-manual-course {platform,key,revision}`。页面返回 Revision 与课程及任务计数。TaskInput `recordingCourseKey` 为 null 保留自由输入行为。

- [ ] 添加真实 GTK 回归：新增自定义课程，任务编辑器选择课程并保存；管理页改名、归档并重新读取任务验证关联。
- [ ] 接入 IPC，管理课程窗口提供编辑、归档/恢复与确认移除；任务下拉选择登记课程，后台恢复后重新读取。
- [ ] 运行 GTK UI 全套，检查改名/归档后刷新和旧连接回调保护；审查 diff 并提交。
