# macOS 界面细节优化

2026-11-06 将首页图标延伸到课程管理和任务编辑，统一圆角、表单层级与操作位置，继续采用 Apple HIG 的桌面内容结构和鲜艳语义色。

本文保留细节优化版的历史记录。2026-11-07 的开源图标版沿用这些布局，图形来源和最新预览见 [Fluent 接入说明](Fluent-Icons.md)。

## 调整结果

| 界面 | 行为与外观 |
| --- | --- |
| 课程管理 | 38 DIP 平台图形，紧凑模式32 DIP；连接、刷新、同步、保存、停止及批量勾选使用同源图标；白色圆角课程区与浅蓝悬停 |
| 任务编辑 | 任务信息、时间安排、补充信息三组白色表单；保存栏和错误提示固定在滚动区外，首次焦点仍在任务名称 |
| 空清单 | 首次使用显示新增和课程管理；搜索无结果提供查看全部任务；未选课程的历史保留说明引导重新选课 |
| 图标 | 新增保存、连接、停止、全选、全不选五枚操作图形，总计37枚；按钮颜色和辅助功能名称随所属按钮更新 |

课程选择、任务校验、原平台依据、隐藏历史和保存逻辑继续由原有窗口维护。表单帮助文字保留时区和时间置信度说明。未知平台采集结果仍显示需要核对。

## 查看

[启动真实WPF预览](../../artifacts/previews/macos-refined-20261106/启动预览.cmd) · [完整截图对照](../../artifacts/previews/macos-refined-20261106/screenshots/界面预览.html) · [图标总览](../../design/icons/图标设计总览.png)

截图使用合成数据，可直接打开：

- [课程管理](../../artifacts/previews/macos-refined-20261106/screenshots/课程管理-默认窗口.png)
- [课程管理小窗口](../../artifacts/previews/macos-refined-20261106/screenshots/课程管理-小窗口.png)
- [任务编辑](../../artifacts/previews/macos-refined-20261106/screenshots/任务编辑-默认窗口.png)
- [空搜索](../../artifacts/previews/macos-refined-20261106/screenshots/首页-搜索无结果.png)
- [首次使用](../../artifacts/previews/macos-refined-20261106/screenshots/首页-首次使用.png)

## 源码与维护

[ProductVisuals.cs](../../src/AccessProbe/Icons/ProductVisuals.cs) 提供同源图标按钮、标题区与表单卡片；[CourseSelectionWindow.cs](../../src/AccessProbe/CourseSelectionWindow.cs) 管理课程窗口；[TaskEditorWindow.cs](../../src/AccessProbe/TaskEditorWindow.cs) 管理表单分组与固定底栏；[TaskDashboardWindow.xaml](../../src/AccessProbe/TaskDashboardWindow.xaml) 与 [TaskDashboardWindow.xaml.cs](../../src/AccessProbe/TaskDashboardWindow.xaml.cs) 按空清单原因显示操作。图形由 [catalog.json](../../src/AccessProbe/Assets/Icons/catalog.json) 维护，导出方法见 [原创图标系统](Icons.md)，设计依据见 [Apple HIG 与实现](macOS-HIG.md)。

## 验证范围

[77项首页检查](../../artifacts/previews/macos-refined-20261106/verification/dashboard-ui-checks.json)、[26项预览检查](../../artifacts/previews/macos-refined-20261106/verification/preview-smoke-checks.json)和 [11项目编译](../../artifacts/previews/macos-refined-20261106/verification/compile-checks.json)通过。26项预览检查使用独立任务和课程库，实际创建八个平台窗口，覆盖图标渲染、最小布局、收起操作、搜索和批量勾选、关闭保留选课，以及编辑必填校验、滚动中保存、输入结果返回与空清单恢复。课程窗口最小尺寸760×620，课程列表实际高度116 DIP，详见 [布局指标](../../artifacts/previews/macos-refined-20261106/verification/dialog-layout-metrics.json)。

本轮仅交付独立界面预览；目视确认待完成，完整加密业务回归受隔离Windows用户配置文件的DPAPI限制。正式任务库、登录和发布指针保留。
