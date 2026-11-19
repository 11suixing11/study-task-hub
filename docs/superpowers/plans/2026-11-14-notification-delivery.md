# 通知投递 Implementation Plan

> **For agentic workers:** Use executing-plans inline；不启动子代理。

**Goal:** 完成 F08 有回执的通知发送、状态、测试与失败重试。

**Architecture:** DesktopNotifier 异步 D-Bus + Future；AppScheduler 等待结果；Linux 专用投递记录事务和 GTK 状态窗口。

**Tech Stack:** Gio / GLib / D-Bus / GTK 4；SQLite / .NET 10。

**Spec:** `docs/superpowers/specs/2026-11-14-notification-delivery-design.md`

## Global Constraints

- “桌面已接收”与“用户看见”区分；不读取系统通知之外的私人数据。
- tests 使用独立 dbus-daemon 与本地任务数据库；不访问官网。
- 失败保留，已接收不重复发送；退出释放未确认领取。

## Task 1 — 异步发送与确认

**Files:** 新增 `notifications.py`；修改 `application.py`、`scheduler.py`；测试 `test_linux_notifications.py`。

**Interfaces:** `DesktopNotifier.send(item): Future[NotificationOutcome]`；Outcome 包含 accepted、error_code、message、desktop_id。

- [x] 未完成发送 Future 时检查真实 reminders 仍为 processing，失败后 scheduled 且有错误；先观察旧调度器提前确认失败。
- [x] 实现 Gio.Notify 回执，调度器等待 Future 后 ack，停止时不接受迟到确认。
- [x] 私有 D-Bus 服务返回 id 及拒绝错误，核对客户端实际结果。

## Task 2 — 历史与测试入口

**Files:** 新增 `LinuxNotificationStatus.cs`、`TaskStore.LinuxNotifications.cs`、`notification_window.py`；修改 `LinuxAppService.Reminders.cs`、`BackendHost.cs`、`settings_window.py`、`ReminderChecks.cs`。

**Interfaces:** `get-notification-status`；`retry-notifications`；`record-notification-test {accepted,errorCode}`；ack-notifications 增加可选 errorCode，旧调用兼容。

- [x] 失败错误持久化、手动重试解除退避、成功历史不重发；测试提醒独立结果。
- [x] GTK 显示历史和测试/重试入口；后台恢复后重读。
- [x] 运行 reminders、scheduler、D-Bus/GUI 和完整包回归；审查并提交。
