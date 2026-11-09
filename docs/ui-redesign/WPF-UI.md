# Windows 界面主体

Windows 端使用 [WPF UI 4.3.0](https://www.nuget.org/packages/WPF-UI/4.3.0) 作为基础组件库，沿用现有 WPF 窗口、事件和数据绑定。2026-11-06 按用户指定的 macOS 方向参考 Apple HIG，首页采用任务分类与平台侧栏、顶部工具栏、统一任务列表和详情的分栏结构；课程管理、任务编辑和连接窗口共用同一套主题。官方依据及本轮实现见 [macOS 设计说明](macOS-HIG.md)。

## 主题入口

唯一主题入口是 [FluentTheme.xaml](../../src/AccessProbe/FluentTheme.xaml)，由 [App.xaml](../../src/AccessProbe/App.xaml) 全局加载；独立连接窗口同时显式加载，方便隔离验证。主题先合并官方浅色资源和控件资源，再定义应用颜色、字体、间距及命名样式。旧 [MaterialTheme.xaml](../../src/AccessProbe/MaterialTheme.xaml) 仅转发到该入口。

按钮、普通输入框和下拉框以官方模板为基础。复选框保留官方模板的外观与交互，只把勾选动画的 `Tag` 改为类型为 `double` 的附加属性，避免课程容器移除后动画读取空值而崩溃；兼容模板集中在 [Themes/CheckBox.xaml](../../src/AccessProbe/Themes/CheckBox.xaml)。需要主按钮或图标时使用 `Wpf.Ui.Controls.Button`；搜索使用 `Wpf.Ui.Controls.TextBox` 的 `PlaceholderText` 和 `Icon`，其 MIT 模板在 [Themes/MacControls.xaml](../../src/AccessProbe/Themes/MacControls.xaml) 中适配为完整焦点环。同一字典提供轻量展开区和任务行选择模板。鲜艳版主色为 `#006BE6`，背景为 `#F5F8FF`，正文为白色，侧栏为 `#F0F4FC`，常用控件圆角为 6 DIP。

首页统计收进侧栏分类，以彩色图标和数量徽章区分类别，选中分类使用蓝底白字。平台入口及任务行使用一致的彩色图标，连接状态仍有圆点；任务行采用双行文字和交替背景，选中行有浅蓝底及左侧蓝条，状态以蓝、橙、红、绿浅底徽章同时显示符号和文字。新增和官方入口使用亮蓝按钮，完成操作使用浅绿按钮；右侧截止卡片按紧急程度着色，其余详情用细分隔线组织。顶部图标工具按钮都有悬停提示和辅助功能名称；侧栏与详情可显隐，并保留筛选和选中任务。平台状态和任务原始依据保持原有计算。

任务表的 `CellStyle` 和 `ColumnHeaderStyle` 显式引用主题内的 `FluentDataGridCell` 和 `FluentDataGridColumnHeader`，确保应用的留白与表头颜色不会被上游默认样式覆盖；任务行采用 `MacDataGridRow`，保留 DataGrid 文本、选择、滚动和详情宿主。

后续添加组件时先使用 WPF UI 的现有控件。专用组件通过局部资源字典或明确的命名样式接入，颜色引用主题内的中性色、导航选择色和成功／提醒／错误语义色，避免在多个窗口重复维护主题。确有视觉需求时局部适配模板，保留必要宿主与交互；不重复合并其他全局主题。

## 图标接入

[ProductIcon.cs](../../src/AccessProbe/Icons/ProductIcon.cs) 继承 WPF UI 的 `IconElement`，通过 `InitializeChildren()` 提供实际的 `Image` 宿主；使用 `DrawingImage` 和等比缩放，兼容按钮、搜索与菜单的图标槽。`Key` 选择图形，`Tile` 选择彩色底，线条图标响应 `Foreground` 变化。

[ProductIcons.cs](../../src/AccessProbe/Icons/ProductIcons.cs) 从嵌入的 [catalog.json](../../src/AccessProbe/Assets/Icons/catalog.json) 读取画板、路径、线宽和颜色，缓存冻结的彩色矢量图像，平台名称颜色与浅底颜色也来自同一目录。窗口使用 `ProductIcons.AppImage`，两个 EXE 共用 `app.ico`。导出与资源说明见 [原创图标系统](Icons.md)。26 项预览检查包含可见图标在实际尺寸的非空渲染、任务行与详情图形一致性、主按钮图标前景色。高对比模式的系统色分支已实现，其效果尚未验收。

[ProductVisuals.cs](../../src/AccessProbe/Icons/ProductVisuals.cs) 提供跨窗口的图标按钮、标题区和表单卡片，操作行为由原窗口维护。图标 `Foreground` 绑定到所属按钮，辅助功能名称随按钮文字更新。课程管理与任务编辑使用同一资源，窗口显式设置品牌图标；任务编辑保存栏与错误信息置于滚动表单外。详见 [细节优化说明](macOS-Refinement.md)。

## 依赖与许可

项目在 [AccessProbe.csproj](../../src/AccessProbe/AccessProbe.csproj) 固定引用 `WPF-UI` 4.3.0。MIT 许可及上游第三方声明保存在 [ThirdParty/WPF-UI](../../src/AccessProbe/ThirdParty/WPF-UI/LICENSE.md)，随编译输出和安装包一起复制。官方接入说明见 [Getting started](https://wpfui.lepo.co/documentation/getting-started.html)。

## 验证范围

本轮 macOS 细节优化版首页的 [77 项检查](../../artifacts/previews/macos-refined-20261106/verification/dashboard-ui-checks.json)覆盖正常、小尺寸、筛选、任务操作、面板显隐、键盘快捷键和原平台依据；[26 项预览检查](../../artifacts/previews/macos-refined-20261106/verification/preview-smoke-checks.json)验证真实产品窗口、主题、独立演示库与本地保存；[11 项目编译](../../artifacts/previews/macos-refined-20261106/verification/compile-checks.json)通过。完整业务回归受隔离用户配置文件的 DPAPI 限制，未形成新的正式发布验收。此前课程选择、平台连接、会话恢复与安装验证属于此前正式版本，范围以 [开发进度](../开发进度.md) 各版本报告为准。

任务库与响应使用合成数据，首页的官方浏览器跳转仅捕获参数。目视效果、正式账号的官网登录和最终任务页面仍需实际验收。

课程列表使用容器回收模式，避免课程刷新重置筛选视图时出现无效子项。课程管理窗口在小尺寸下减少间距并为列表保留空间；首页长标题和依据在独立详情区域滚动，底部操作保持可见。复选框的刷新、搜索、批量勾选和收起／展开由课程管理回归覆盖，最新通过范围以当前报告为准。

[Stageshow 预览](REHEARSAL.md) 保留为此前的设计参考；该预览的验证记录独立于 Windows 程序的构建与回归。
