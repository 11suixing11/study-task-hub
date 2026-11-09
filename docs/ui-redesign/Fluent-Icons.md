# Fluent 开源图标接入

2026-11-07 在现有 WPF UI 与 macOS 风格布局上，使用 Microsoft Fluent System Icons 替换通用操作和状态图形。分类入口更醒目，按钮轮廓更统一；品牌及八个平台继续使用原有图形。查看 [独立预览](../../artifacts/previews/macos-fluent-20261106/启动预览.cmd)、[实际窗口截图](../../artifacts/previews/macos-fluent-20261106/screenshots/界面预览.html) 和 [图标总览](../../design/icons/图标设计总览.html)。

## 来源与许可

- 上游：[microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons)。
- 固定 revision：`e15fc0de25fab983e3e42bac8798be69a85c23bf`。
- 许可：[MIT 原文](../../src/AccessProbe/ThirdParty/Fluent-System-Icons/LICENSE.txt)，附 [NOTICE](../../src/AccessProbe/ThirdParty/Fluent-System-Icons/NOTICE.txt)。
- [sources.json](../../src/AccessProbe/Assets/Icons/Fluent/sources.json) 记录 28 个 regular 与 7 个 color 原始 SVG 的 URL、SHA256 和版本，素材保存在 [Fluent/svg](../../src/AccessProbe/Assets/Icons/Fluent/svg)。

上游当前将 color 变体标记为 deprecated，原文保留在 [官方 SVG README](../../src/AccessProbe/ThirdParty/Fluent-System-Icons/Upstream-SVG-README.md)。本版使用固定保存的 MIT 素材，不依赖远程资源；后续升级需要重新核查来源和绘制能力。图标集合未提供教育平台官方标志，本应用的来源图形仍为原创。

## 映射与绘制

| 用途 | 数量 | 接入方式 |
| --- | --- | --- |
| 通用分类、操作与状态 | 28 个 Key | 官方 24×24 regular 轮廓转为 32×32，跟随按钮 Foreground |
| 分类、手动任务、课程管理的彩色入口 | 7 个上述 Key 的彩色版本 | 原始渐变与透明度转为 WPF DrawingImage |
| 应用主图标与八个平台 | 9 个 Key | 保留原创图形和鲜艳来源配色 |

彩色 Key 为 `tasks`、`todo`、`soon`、`overdue`、`completed`、`document`、`courses`，按钮使用同一 Key 的 regular。37 个 Key 的完整用途见 [图标系统](Icons.md)。状态始终伴随文字，任务行和详情共用来源图形，按钮保留辅助功能名称。高对比模式使用系统颜色的单色分支，尚未单独目视验收。

[catalog.json](../../src/AccessProbe/Assets/Icons/catalog.json) 管理产品映射。[FluentIcons.xaml](../../src/AccessProbe/Assets/Icons/Fluent/FluentIcons.xaml) 是可复现生成的原生 WPF 字典，由 [ProductIcons.cs](../../src/AccessProbe/Icons/ProductIcons.cs) 加载。不新增运行时 SVG 库；原始 SVG、转换结果、导出 PNG 和十档 ICO 随预览包提供。

## 维护

在项目根目录执行：

```powershell
python scripts/import-fluent-icons.py
python scripts/create-app-icon.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-icons.ps1
python scripts/create-icon-overview.py
.\scripts\codegraph.ps1 sync
```

导入需要 fontTools，离线验证源文件 SHA256，并拒绝未支持的 SVG 属性和绘制结构。导出需要 Windows WPF；总览需要 Pillow 与微软雅黑。原始 SVG 使用 24×24 画板；产品 regular 导出为 32×32，彩色导出保留 24×24。37 个默认导出含 6 个彩色图形和 31 个 32×32 图形；课程管理默认导出为线条版，彩色版本仍在原始素材和 WPF 字典中。WPF 内统一按 32×32 画板缩放。

新增素材时更新固定来源清单，保留官方原文件和许可，重新转换、导出、编译，并运行首页检查和预览检查。源码同步后发布独立预览，组装副本再执行 `MacPreview.exe --smoke-check`；测试生成的合成库归档到工作目录，交付时不包含测试库。

## 验证范围

[77 项首页检查](../../artifacts/previews/macos-fluent-20261106/verification/dashboard-ui-checks.json)、[26 项预览检查](../../artifacts/previews/macos-fluent-20261106/verification/preview-smoke-checks.json) 和 [11 项目编译](../../artifacts/previews/macos-fluent-20261106/verification/compile-checks.json)通过，编译零警告、零错误。完整便携组装副本另通过 [同一 26 项检查](../../artifacts/previews/macos-fluent-20261106/verification/assembled-preview-smoke-checks.json)。检查覆盖实际尺寸图标非空、按钮配色、八个平台最小布局、课程搜索和勾选、编辑校验与保存以及空清单恢复。

上述开源图标预览使用合成数据，保留其当时的验证范围：[预览交付清单](../../artifacts/previews/macos-fluent-20261106-delivery-checks.json)。2026-11-07 的 Windows 1.1.0 正式构建 `20261106-163639-bf6c5ee0` 已通过完整 DPAPI 加密与跨进程回归：2,810 项业务、1,124 项桌面与重启、36 项 Python 检查；正式指针已更新。新品牌另通过 26 项真实 WPF 界面检查和十档嵌入图标检查，参见 [主图标规范](App-Icon.md)与 [正式交付核对](../../artifacts/installers/20261106-163639-bf6c5ee0/verification/delivery-checks.json)。最终桌面目视与真实账号核对仍待用户完成。
