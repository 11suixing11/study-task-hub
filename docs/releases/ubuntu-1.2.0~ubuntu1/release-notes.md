# Ubuntu 原生桌面开发预览 1.2.0~ubuntu1

面向本机 Ubuntu 26.04.1 amd64 的可运行开发预览。原生 GTK 4 / libadwaita 首页、任务编辑、WebKit 官方登录、课程确认、八个平台同步、官网任务入口、提醒、自动检查和退出清理已接入。FiF 保持手动模式。

首页支持统计、搜索、来源/状态筛选、排序与 100 条分页；窄窗口或大字体使用可收起的任务详情。保存失败和 Revision 冲突保留表单；修复备注编辑误改截止秒数、U 校园独立任务域名路由和大体积管道写入阻塞。

最终验证通过 162 项 Linux 核心断言、47 项共享移动业务检查和 18 项 Python/GTK/WebKit/安装包测试。八个平台使用只读本地 fixture；WebKit 检查覆盖真实 HttpOnly Cookie、允许的本地 iframe/弹窗、外域拦截、未受信任 TLS 拒绝及关闭保存失败时保留旧会话。深/浅色及大字体 1000×700 截图已核对。NuGet 当前源未报告脆弱依赖。

`.deb` 包内后台包含 .NET 运行时，移除 SDK 环境变量并离开仓库后启动通过。依赖按本机实际安装版本核对；fakeroot 独立 dpkg 前缀验证安装、同版本重装、卸载和数据/密钥保留。测试未更改本机系统安装状态。

本人官网登录、GNOME 通知目视、1×/2× 跨屏、Ubuntu 24.04、常规系统安装和不同版本升级均未验收。本轮只按开发预览交付，不作为这些场景已经验证的候选发行版。GUI 验证期间曾遇到 GNOME portal 超时，恢复用户级服务后重新完成整套检查。

包：`artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb`，29,020,402 字节。SHA256、源码提交、依赖版本、逐项范围见 [verification.json](verification.json)。

源码启动：`bash scripts/start-linux.sh`。包内预览、安装命令和流程见 [Ubuntu 安装与使用](../../Ubuntu安装与使用.md)。
