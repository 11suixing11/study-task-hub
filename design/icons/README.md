# 聚合平台图标资源

37 个产品图标：28 个 Microsoft Fluent System Icons 图形，9 个原创品牌及平台图形；其中 7 个 Fluent Key 同时提供官方彩色版本。适用于 macOS 风格 Windows 首页、课程管理和任务编辑。查看 [图标设计总览.png](图标设计总览.png) 或 [图标设计总览.html](图标设计总览.html)。

- `svg/`：37 个默认矢量图形；五种分类与手动任务共 6 个彩色图形保留 24×24 画板，其余为 32×32。课程管理导出为线条版，彩色书本保存在原始素材目录。操作和状态轮廓使用 `currentColor`。
- `png/`：37 个 256 px 透明 PNG。
- [app-1024.png](app-1024.png)：1024 px 主图标。
- [app.ico](app.ico)：十档 Windows 图标，16–256 px。
- [overview-layout.json](overview-layout.json)：展示图文字边界与画布尺寸记录。

- [Fluent来源清单.json](Fluent来源清单.json)、[Fluent-LICENSE.txt](Fluent-LICENSE.txt)、[Fluent-NOTICE.txt](Fluent-NOTICE.txt)：固定版本、35 个原始素材的 URL 与 SHA256、MIT 许可和来源说明。

产品映射在 [catalog.json](../../src/AccessProbe/Assets/Icons/catalog.json)，Fluent 原始 SVG 与生成的 WPF 彩色字典在 [Fluent](../../src/AccessProbe/Assets/Icons/Fluent)。Windows 1.1.0 的精细主图标源文件在 [Brand/app.svg](../../src/AccessProbe/Assets/Icons/Brand/app.svg)，生成 `ProductBrand.App` 原生 WPF 资源，见 [主图标规范](../../docs/ui-redesign/App-Icon.md)。避免直接改导出文件。从项目根目录依次运行 [import-fluent-icons.py](../../scripts/import-fluent-icons.py)、[create-app-icon.py](../../scripts/create-app-icon.py)、[export-icons.ps1](../../scripts/export-icons.ps1) 和 [create-icon-overview.py](../../scripts/create-icon-overview.py)。Fluent 导入需要 fontTools，品牌转换使用 Python 标准库，导出需要 Windows WPF，总览需要 Pillow 和微软雅黑字体。图标映射及验证见 [图标系统说明](../../docs/ui-redesign/Icons.md)，来源与上游 color 弃用状态见 [Fluent 接入说明](../../docs/ui-redesign/Fluent-Icons.md)。

正式构建 `20261106-163639-bf6c5ee0` 的程序、安装向导与外部 ICO 十档逐帧相同，PNG 尺寸与透明通道也已验证，见 [图标打包检查](../../artifacts/installers/20261106-163639-bf6c5ee0/verification/icon-package-checks.json)。
