# macOS 设计语言与 Windows 实现

2026-11-06 按用户要求参考 Apple Human Interface Guidelines 调整真实 WPF 首页，并进一步设计 37 枚原创图标。视觉方向采用 macOS 常见的侧栏、工具栏、列表和检查器结构，WPF UI 4.3.0 继续提供基础控件；圆角渐变图形、鲜艳语义色和统一线条帮助识别任务、来源与操作。

## 官方要求与本轮调整

| Apple 官方参考 | 设计依据 | 产品实现 |
| --- | --- | --- |
| [Designing for macOS](https://developer.apple.com/design/human-interface-guidelines/designing-for-macos) | 内容为主体，熟悉的桌面交互 | 浅蓝灰侧栏、顶部工具栏、白色任务列表和右侧详情 |
| [Sidebars](https://developer.apple.com/design/human-interface-guidelines/sidebars) | 简单层级，符号帮助识别，颜色有明确含义 | 分类采用蓝、橙、红、绿图标和数量徽章；选中分类蓝底白字，八个平台各有彩色图标，状态同时有文字 |
| [Toolbars](https://developer.apple.com/design/human-interface-guidelines/toolbars) | 常用操作集中于顶部，减少视觉重量 | 图标工具按钮提供同步、管理课程、新增、设置和面板显隐；悬停提示与辅助功能名称齐全 |
| [Split views](https://developer.apple.com/design/human-interface-guidelines/split-views) | 合理栏宽、持续选择状态、可隐藏面板 | 220 DIP 侧栏和 256–300 DIP 详情栏，1 DIP 分隔线；分类持续高亮，显隐保留任务选择 |
| [Lists and tables](https://developer.apple.com/design/human-interface-guidelines/lists-and-tables) | 清晰表头、内容优先，可用交替行背景 | 任务／课程、截止、状态三列；56 DIP 双行任务、彩色平台图标、浅色交替行；选中行浅蓝背景和左侧 3 DIP 蓝色条 |
| [Materials](https://developer.apple.com/design/human-interface-guidelines/materials) | Liquid Glass 用于功能层且应克制，避免用于内容层 | 导航与工具栏用不透明浅色背景区分层次，正文保持白色；小品牌图标使用蓝紫渐变，本轮未实现系统 Liquid Glass |
| [Typography](https://developer.apple.com/design/human-interface-guidelines/typography) | 桌面字号有层级，系统字体不随应用嵌入 | 标题 17/22 DIP，正文 13 DIP，辅助文字 11/12 DIP；使用 Segoe UI 与微软雅黑回退，不打包 Apple 系统字体 |
| [Buttons](https://developer.apple.com/design/human-interface-guidelines/buttons) | 少量主操作，同层级按钮尺寸一致 | 新增和官方任务入口使用亮蓝主按钮，标记完成使用浅绿按钮；常用按钮高度 30 DIP，圆角 6 DIP |
| [Accessibility](https://developer.apple.com/design/human-interface-guidelines/accessibility) | 可读对比、键盘操作、状态不能只靠颜色 | 状态有彩色浅底、深色文字及图标，详情截止卡片同步显示紧急程度；搜索完整焦点环、可清除，工具按钮提供名称和提示，长详情独立滚动 |
| [Windows](https://developer.apple.com/design/human-interface-guidelines/windows) | 使用系统窗口，避免仿造系统框架与控件 | 保留 Windows 原生关闭、最小化、最大化与缩放行为 |
| [Layout](https://developer.apple.com/design/human-interface-guidelines/layout) | 适应窗口尺寸并保持内容清晰 | 默认 1320×860、最小 1100×700 DIP；另检查 1440×960 和 1180×800 布局；侧栏和详情可收起以扩大列表 |

这是对 macOS 设计语言的 Windows 适配，不是 Apple 原生 AppKit 实现或完整 HIG 合规认证。Apple 的系统材质、SF Symbols、字体等资产未复制到安装包中。

## 原创图标

主图标用三张任务卡汇聚为完成勾，使用蓝紫渐变和圆角轮廓。任务分类采用蓝、橙、红、绿语义色；八个平台以书本、雨云、水波、学士帽、播放屏幕、树叶、校园与代码区分，辅以平台名称。操作和状态采用圆头线条，跟随所属按钮或徽章的前景色。平台图形在侧栏、任务行和详情中共用同一资源；这些图形是应用自行绘制的来源标识。

品牌与平台图形以 32×32 画板维护，Fluent 彩色图形保留官方 24×24 源画板并在 WPF 中缩放绘制；Windows EXE 和窗口使用同一主图标。Windows 1.1.0 进一步细化为蓝紫渐变圆角底、半透明叠层任务卡和完成徽章，见 [主图标规范](App-Icon.md)。图标没有独立交互焦点，工具按钮继续提供文字提示和辅助功能名称，状态保留文字说明。详见 [图标规范与映射](Icons.md)、[图标总览](../../design/icons/图标设计总览.png)及 [资源下载总览](../../design/icons/图标设计总览.html)。

## 跨窗口细节

课程管理窗口使用与首页相同的平台图形，连接、刷新、同步、保存和批量勾选采用原创操作图标；课程区为白色圆角面板，行悬停使用浅蓝背景。最小尺寸减少标题与说明的间距，保留课程行和底部操作。任务编辑采用三个表单分组、统一字段间距和固定保存栏；必填错误显示在操作栏上方。空任务页根据筛选与课程记录原因给出新增、课程管理或清除筛选操作。实现与真实窗口截图见 [细节优化说明](macOS-Refinement.md)。

## 交互

侧栏分类清除搜索与平台过滤后显示对应的全平台任务。同步、平台管理、课程选择、原平台状态与时间依据、人工修改、提醒和官方入口沿用现有业务。

| 快捷键 | 操作 |
| --- | --- |
| Ctrl+F | 聚焦搜索并选中已有文字 |
| Ctrl+N | 新增任务 |
| F5 | 同步所有平台，同步进行时不可重复启动 |
| Ctrl+Shift+L | 显示或隐藏侧栏 |
| Ctrl+Shift+I | 显示或隐藏详情 |

## 实现入口

[FluentTheme.xaml](../../src/AccessProbe/FluentTheme.xaml) 维护应用配色、系统字体、间距和控件样式：主蓝 `#006BE6`、背景 `#F5F8FF`、侧栏 `#F0F4FC`，正文白色，选中行 `#DFEBFF`；状态使用蓝、橙、红、绿的深色文字和对应浅色底。[TaskDashboardWindow.xaml](../../src/AccessProbe/TaskDashboardWindow.xaml) 定义首页；[Themes/MacControls.xaml](../../src/AccessProbe/Themes/MacControls.xaml) 提供轻量展开控件、任务行和完整搜索焦点环。搜索模板基于 WPF UI 的 MIT 许可模板适配，保留文本宿主、占位文字、清除命令、图标位置及只读行为。许可声明位于 [WPF UI LICENSE](../../src/AccessProbe/ThirdParty/WPF-UI/LICENSE.md)。

验证使用独立合成任务，官方浏览器跳转只捕获参数。首页检查区分 Windows DPAPI 与仅用于无用户配置文件环境的进程内测试替身；后者只验证界面和任务交互，不证明加密或跨进程会话恢复。

此前细节优化预览的 [77 项首页检查](../../artifacts/previews/macos-refined-20261106/verification/dashboard-ui-checks.json)、[11 项目编译](../../artifacts/previews/macos-refined-20261106/verification/compile-checks.json)和 [26 项预览检查](../../artifacts/previews/macos-refined-20261106/verification/preview-smoke-checks.json)保留为历史证据。2026-11-07 的 Windows 1.1.0 正式构建 `20261106-163639-bf6c5ee0` 已通过 2,810 项业务、1,124 项桌面与重启、36 项 Python 检查，Windows DPAPI 加密与跨进程恢复正常；新品牌另通过 26 项真实 WPF 界面检查、隔离安装与图标打包检查，正式指针已更新。最终桌面目视与真实账号核对仍待用户完成，实际范围见 [开发进度](../开发进度.md)。

[细节优化版独立预览](../../artifacts/previews/macos-refined-20261106/启动预览.cmd)直接实例化产品窗口和主题，每次使用新的演示库；同步与提醒服务不启动，连接操作显示预览提示。可查看 [默认窗口截图](../../artifacts/previews/macos-refined-20261106/screenshots/首页-默认窗口.png)及 [小窗口截图](../../artifacts/previews/macos-refined-20261106/screenshots/首页-小窗口.png)。
