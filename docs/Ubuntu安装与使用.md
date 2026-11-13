# Ubuntu 桌面开发预览

本轮版本 `1.2.0~ubuntu1`，面向本机 Ubuntu 26.04.1 amd64。程序采用 GTK 4、libadwaita 与 WebKitGTK，本地后台随 `.deb` 提供 .NET 10 运行时；运行安装包无需 SDK。

这是可运行的开发预览。八个平台的课程、选课、采集与统一任务清单已接入；真实官网账号、GNOME 通知显示、1×/2× 跨屏、Ubuntu 24.04、常规系统安装和不同版本覆盖升级尚未验收，不能将隔离 fixture 结果解释为这些场景已经通过。

## 源码启动

在项目目录运行：

```bash
bash scripts/start-linux.sh
```

默认数据目录是 `$XDG_DATA_HOME/study-task-hub`，未设置时为 `~/.local/share/study-task-hub`。可以用 `--data-root /绝对路径` 指定独立数据目录。Windows 的项目内任务库不作为 Linux 启动数据源。

## 包内预览与安装

包在 `artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb`；同目录有 SHA256、验证摘要和附件清单。当前测试覆盖解包运行，以及 fakeroot 配合独立 dpkg 前缀的安装、同版本重装和卸载；依赖另外按本机已安装版本核对。这不等同于修改本机系统安装状态或验证不同版本升级。

不安装系统包也能预览：

```bash
dpkg-deb -x artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb .local/ubuntu-package-preview
.local/ubuntu-package-preview/usr/bin/study-task-hub --data-root "$PWD/.local/ubuntu-package-preview-data"
```

预览数据独立保存。正常安装可在用户决定安装后执行：

```bash
sudo apt install ./artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb
```

安装后从应用菜单打开“聚合平台”，或执行 `study-task-hub`。依赖包括 Python/PyGObject、GTK 4、libadwaita、WebKitGTK 6、系统 SQLite 与 .NET 所需系统库，具体版本见包元数据。卸载脚本不清除用户数据。

## 日常使用

1. 首页可以直接新增手动任务，搜索、筛选状态和来源，按优先级、截止或更新排序。任务列表按 100 条分页。
2. 打开“平台与课程”，本人在对应官网窗口完成扫码或账号登录，点击“登录完成，保存并读取课程”。仅保存 Cookie 不表示账号和课程已经校验。
3. 读取课程后明确保存勾选。确认前只读课程目录；新增课程不会自动勾选。取消课程保留历史任务，并停止其采集与提醒；首页可勾选恢复查看历史。
4. 点击平台的“采集任务”或首页“同步”；运行中的读取可以停止。网络失败、未知结构、分页不完整和登录过期都会保留已有任务并提示结果。
5. 任务详情提供编辑、状态更新、删除确认和官方入口。采集任务的来源受保护，旧 Revision 更新会拒绝并保留输入；仅编辑备注不会改掉原日期的秒数。
6. 设置中控制自动采集与通知。程序运行时每 30 分钟检查已登录且已选课程的平台，每 60 秒检查本地提醒。退出停止作业、释放提醒领取并关闭后台，没有常驻服务。

程序仅聚合课程任务元数据。作答与提交在官网完成，日期和状态以官方页面为准。链接及会话使用本地 AES-GCM 保护；数据和浏览器目录 0700，密钥 0600。密钥丢失或密文损坏会明确报错，不能删除密钥来“修复”已有数据库。

后台断开后，首页会显示“恢复连接”，恢复后重读本地清单；未确认的保存请求不会自动重放。关闭登录窗口会尝试有时限的保存，空快照不清原会话。明确“清除登录”才移除该平台会话与浏览器目录，任务仍保留。

## 验证与维护

```bash
bash scripts/test-linux-core.sh --suite all
bash scripts/test-linux-ui.sh --group all
bash scripts/build-linux.sh --version 1.2.0~ubuntu1
bash scripts/verify-linux-package.sh artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb
```

所有自动验证使用独立数据库、profile 和本地响应，不访问正式数据或真实账号。源码截图工具 `scripts/capture-linux-ui.py` 使用自身 GTK WidgetPaintable 渲染。`GDK_SCALE=2` 在本机 Wayland 未改变实际窗口 scale，不能作为 2× 或移屏验证证据。

最新逐项结果、包哈希、源码提交与未验收范围见 [发布验证摘要](releases/ubuntu-1.2.0~ubuntu1/verification.json)。第三方 .NET/NuGet 许可随包放在 `/usr/share/doc/study-task-hub`；系统 GUI 库许可由对应发行版依赖包提供。
