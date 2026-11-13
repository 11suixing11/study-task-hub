# 加密备份恢复 Implementation Plan

> **For agentic workers:** Use executing-plans inline，用户约定不启动子代理。

**Goal:** 完成 F07 加密、一致性、可校验、可回退的备份恢复与设置入口。

**Architecture:** SQLite BackupDatabase 快照 + AES-GCM 固定格式；恢复日志保护 DB/key 双文件替换；BackendServer 串行操作并停止 job；GTK 在检查后确认恢复。

**Tech Stack:** .NET 10 / SQLite / System.IO.Compression / System.Security.Cryptography；GTK 4。

**Spec:** `docs/superpowers/specs/2026-11-14-backup-restore-design.md`

## Global Constraints

- 正式数据不参与测试；不包含官网作答内容。
- 文件 0600，工作目录 0700，密码不记录；数据库版本 1，备份版本 1。
- 认证与内容校验先于替换；恢复前保存回退备份与 profiles。

## Task 1 — 加密快照与校验

**Files:** 新增 `LinuxBackup.cs`、`TaskStore.LinuxBackup.cs`、`LinuxAppService.Backup.cs`、`BackupChecks.cs`；修改 `LinuxDataProtector.cs`、`LinuxAppService.cs`、`Program.cs`。

**Interfaces:** `CreateBackup(path,password): BackupPreview`；`InspectBackup(path,password): BackupPreview`；`RestoreBackup(path,password,fingerprint): RestoreResult`。

- [ ] 测试先创建备份、修改原任务后恢复，断言原任务/加密链接/会话可读；错误密码返回 BACKUP003 且当前任务不变。
- [ ] 使用 SQLite 快照和固定 ZIP 白名单，认证密码和版本，预览返回任务数/会话数/指纹。
- [ ] 恢复双文件前写日志，成功重新验证后提交；启动时遇到日志执行原数据回退。
- [ ] 运行 backups/core suites，检查损坏/变更拒绝和模拟日志恢复。

## Task 2 — IPC 与 GTK

**Files:** 修改 `BackendHost.cs`、`settings_window.py`；新增 `backup_window.py`；新增 `test_linux_backup.py`。

**Interfaces:** `create-backup/inspect-backup {path,password}`；`restore-backup {path,password,fingerprint}`；恢复结果返回回退备份/环境目录。

- [ ] GTK 先检查再确认；确认前任务不变，确认后恢复备份并显示回退位置。
- [ ] 后台串行每个请求，恢复先 StopAll；前端等待登录关闭、停止调度并禁用其他窗口，恢复完成再恢复交互。
- [ ] 核心、GUI 与包回归，审查后提交。
