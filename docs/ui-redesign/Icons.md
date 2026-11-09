# 聚合平台图标系统

2026-11-07 按用户确认的“WPF UI 主体 + Fluent 彩色入口 + Fluent 线条按钮”接入 Microsoft Fluent System Icons。37 个产品 Key 中，28 个采用 MIT 许可的 Fluent 图形，应用主图标和八个平台来源图形共 9 个继续使用原创设计。五种任务分类、手动任务和课程管理另有 7 个官方彩色版本。来源与维护见 [Fluent 开源图标接入](Fluent-Icons.md)。

Windows 1.1.0 已换用更精细的蓝紫渐变叠层任务卡品牌图标，源 SVG 和原生 WPF 品牌字典分别维护，见 [主图标规范](App-Icon.md)。Fluent 与八个平台图形保持原映射，产品目录仍为 37 个 Key。

[整套展示图](../../design/icons/图标设计总览.png) · [资源下载总览](../../design/icons/图标设计总览.html) · [正式版界面截图](../../artifacts/installers/20261106-163639-bf6c5ee0/screenshots/界面截图.html) · [历史独立预览](../../artifacts/previews/macos-fluent-20261106/启动预览.cmd)

## 图形规范

WPF 使用统一 32×32 画板；Fluent 官方 24×24 图形按比例转换。彩色入口保留官方渐变、透明度与留白，原创品牌和平台继续采用鲜艳圆角底。主图标为蓝紫色；文字、数量徽章和任务状态仍使用蓝色待办、橙色临期、红色逾期、绿色完成。操作轮廓在 WPF 中跟随 `Foreground`，导出的 regular SVG 使用 `currentColor`。37 个默认导出中，五种分类与手动任务共 6 个彩色 SVG 保留官方 24×24 画板，其他 31 个为 32×32；课程管理默认导出为线条版，彩色书本保存在原始素材目录。

首页品牌为 34 DIP，分类为 24 DIP，平台入口设置为 26 DIP，详情来源为 22 DIP，工具按钮通常为 18 DIP，状态符号为 14 DIP。任务行的实际图标尺寸以布局测量为准，本轮烟雾检查测得 18 DIP；实际渲染指标随预览包交付。资源画板尺寸不代表控件必须固定为 32 DIP。

平台图形是应用绘制的来源标识，平台名称始终相邻显示。状态同时显示文字；图标不独立占用键盘焦点，所属按钮保留提示与辅助功能名称。高对比模式有系统色绘制分支，尚未验收。Apple HIG 的布局与语义原则见 [macOS 设计说明](macOS-HIG.md)。

课程窗口标题使用 38 DIP 平台图形，紧凑模式为 32 DIP；连接、刷新、同步、保存和批量选择使用 18 DIP 操作图标。编辑窗口和课程窗口显式设置品牌 Window.Icon。跨窗口接入见 [细节优化说明](macOS-Refinement.md)。

## 图标映射

| Key | 名称 | 分组 | 图形与配色 |
| --- | --- | --- | --- |
| `app` | 聚合平台 | 主图标 | 三张任务卡与完成勾，蓝紫渐变 |
| `tasks` | 全部任务 | 任务分类 | Fluent Document Text，官方彩色与 regular |
| `todo` | 待办 | 任务分类 | Fluent Clipboard Task，官方彩色与 regular |
| `soon` | 即将到期 | 任务分类 | Fluent Clock，官方彩色与 regular |
| `overdue` | 已逾期 | 任务分类 | Fluent Error Circle，官方彩色与 regular |
| `completed` | 已完成 | 任务分类 | Fluent Checkmark Circle，官方彩色与 regular |
| `chaoxing` | 学习通 | 平台来源 | 书本与星，珊瑚色 |
| `yuketang` | 雨课堂 | 平台来源 | 雨云，蓝色 |
| `changjiang` | 长江雨课堂 | 平台来源 | 云与水波，靛紫 |
| `mooc` | 中国大学 MOOC | 平台来源 | 学士帽，橙色 |
| `xuetang` | 学堂在线 | 平台来源 | 播放屏幕，紫色 |
| `zhihuishu` | 智慧树 | 平台来源 | 树叶，绿色 |
| `ucampus` | U 校园 | 平台来源 | 校园与 U，粉色 |
| `pta` | PTA | 平台来源 | 代码符号，青色 |
| `document` | 手动任务 | 平台来源 | Fluent Document，官方彩色与 regular |
| `sync` | 同步所有平台 | 操作 | 循环箭头 |
| `courses` | 管理记录课程 | 操作 | Fluent Book Open，官方彩色与 regular |
| `add` | 新增任务 | 操作 | 加号 |
| `sidebar` | 显示或隐藏侧栏 | 操作 | 左侧面板 |
| `inspector` | 显示或隐藏详情 | 操作 | 右侧面板 |
| `settings` | 设置 | 操作 | Fluent Settings，齿轮 |
| `search` | 搜索 | 操作 | 放大镜 |
| `open` | 打开官方任务 | 操作 | 向外箭头 |
| `edit` | 编辑任务 | 操作 | 铅笔 |
| `more` | 更多操作 | 操作 | 三点 |
| `info` | 任务详情 | 操作 | 信息圆环 |
| `saved` | 登录信息自动保存 | 操作 | Fluent Shield Checkmark，盾牌与完成勾 |
| `copy` | 复制延期申请 | 操作 | 叠放文档 |
| `delete` | 删除任务 | 操作 | 垃圾桶 |
| `returned` | 被打回 | 状态 | Fluent Arrow Hook Up Left，返回箭头 |
| `review` | 待核对 | 状态 | Fluent Warning，警示三角 |
| `ignored` | 已忽略 | 状态 | Fluent Eye Off，隐藏眼睛 |
| `save` | 保存 | 操作 | 存储盘轮廓 |
| `connect` | 连接平台 | 操作 | 相连链环 |
| `stop` | 停止采集 | 操作 | Fluent Stop，停止方块 |
| `check-all` | 全选当前列表 | 操作 | 勾选清单 |
| `clear-selection` | 全不选当前列表 | 操作 | 空勾选框清单 |

## 资源与维护

产品映射在 [catalog.json](../../src/AccessProbe/Assets/Icons/catalog.json)，官方原始素材与 URL、版本和 SHA256 在 [sources.json](../../src/AccessProbe/Assets/Icons/Fluent/sources.json)。[import-fluent-icons.py](../../scripts/import-fluent-icons.py) 离线转换 regular 轮廓和 [FluentIcons.xaml](../../src/AccessProbe/Assets/Icons/Fluent/FluentIcons.xaml) 彩色绘图。[create-app-icon.py](../../scripts/create-app-icon.py) 将 [原创品牌 SVG](../../src/AccessProbe/Assets/Icons/Brand/app.svg) 转为 `ProductBrand.App`。运行时由 [ProductIcons.cs](../../src/AccessProbe/Icons/ProductIcons.cs) 优先加载品牌资源，再加载 Fluent 与平台矢量，[ProductIcon.cs](../../src/AccessProbe/Icons/ProductIcon.cs) 接入 WPF UI 图标宿主，无新增运行时 SVG 库。主窗口、程序及安装 EXE 使用同一原创品牌图形。

[design/icons](../../design/icons/README.md) 提供 37 个独立 SVG、37 个 256 px PNG、[1024 px 主图标](../../design/icons/app-1024.png)、[Windows ICO](../../design/icons/app.ico)、展示图和本地 HTML。ICO 包含 16、20、24、32、40、48、64、96、128、256 px 共十档。

修改素材映射后从项目根目录执行：

```powershell
python scripts/import-fluent-icons.py
python scripts/create-app-icon.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-icons.ps1
python scripts/create-icon-overview.py
```

导入脚本依赖 fontTools 并先校验原始素材 SHA256，导出脚本依赖 Windows WPF，展示图脚本依赖 Pillow 和微软雅黑。HTML 内嵌 SVG 的渐变 ID 按图标 Key 区分，独立 SVG 可单独使用。新增 Key 后同步首页绑定、平台映射与维护说明，并运行实际预览检查。Fluent 的 [MIT 许可](../../src/AccessProbe/ThirdParty/Fluent-System-Icons/LICENSE.txt) 和 [来源声明](../../src/AccessProbe/ThirdParty/Fluent-System-Icons/NOTICE.txt) 随包交付。上游已将 color 标记为 deprecated，本版固定保存所选素材；维护时评估替换，不自动追随最新版本。

## 验证

[77 项首页检查](../../artifacts/previews/macos-fluent-20261106/verification/dashboard-ui-checks.json)和 [26 项预览检查](../../artifacts/previews/macos-fluent-20261106/verification/preview-smoke-checks.json)通过，后者检查实际尺寸图标非空渲染、任务行与详情图形一致性、主按钮图标前景色。[11 项目编译](../../artifacts/previews/macos-fluent-20261106/verification/compile-checks.json)通过，零警告、零错误。图标渲染记录见 [实际尺寸指标](../../artifacts/previews/macos-fluent-20261106/verification/icon-render-metrics.json)。静态资产检查覆盖原始素材 SHA256、SVG 结构、HTML 资源链接、PNG 尺寸和 ICO 帧；26 项预览检查同时覆盖八个平台的课程窗口、任务编辑和空清单。完整便携组装副本另运行同一 26 项检查，见 [组装检查](../../artifacts/previews/macos-fluent-20261106/verification/assembled-preview-smoke-checks.json)。

上述开源图标预览报告作为历史记录保留。2026-11-07 的 Windows 1.1.0 正式构建 `20261106-163639-bf6c5ee0` 已通过完整回归：2,810 项业务、1,124 项桌面与重启、36 项 Python 检查，含 77 项首页检查；Windows DPAPI 与跨进程恢复正常。新品牌另通过 [26 项真实界面检查](../../artifacts/installers/20261106-163639-bf6c5ee0/verification/preview-smoke-checks.json)和 [安装 EXE / 程序 EXE 图标逐帧检查](../../artifacts/installers/20261106-163639-bf6c5ee0/verification/icon-package-checks.json)。正式指针已更新，旧预览保留；最终桌面目视确认仍待完成。
