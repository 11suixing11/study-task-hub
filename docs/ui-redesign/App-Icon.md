# 聚合平台主图标

Windows 1.1.0 使用原创“叠层任务卡”图标：鲜艳的蓝紫渐变圆角底，三张半透明任务卡汇聚到前景清单，紫色完成徽章强调任务整理。边缘高光和轻量阴影区分层次，透明外缘适应浅色与深色桌面。该图形由项目自行绘制，与 Microsoft Fluent System Icons 的操作图标分别维护。

参考 [Apple HIG · App icons](https://developer.apple.com/design/human-interface-guidelines/app-icons) 的清晰轮廓、单一主体与缩小识别原则，并按 Windows ICO 和 WPF 的实际用途导出。未使用 Apple 商标、SF Symbols 或系统字体。

| 资源 | 用途 |
| --- | --- |
| [原始 SVG](../../src/AccessProbe/Assets/Icons/Brand/app.svg) | 32×32 画板，15 个路径层；编辑源文件 |
| [BrandIcon.xaml](../../src/AccessProbe/Assets/Icons/Brand/BrandIcon.xaml) | 原生 WPF DrawingImage，资源 Key 为 `ProductBrand.App` |
| [1024 px PNG](../../design/icons/app-1024.png) | 展示与后续素材使用，保留透明通道 |
| [256 px PNG](../../design/icons/png/app.png) | 小尺寸展示 |
| [Windows ICO](../../design/icons/app.ico) | 16、20、24、32、40、48、64、96、128、256 px 共十档 |

应用首页、Window.Icon、程序 EXE、安装程序、卸载显示图标、桌面与开始菜单快捷方式使用同一品牌。`catalog.json` 的 `app` 映射优先加载品牌字典，原有 Fluent 和平台图形保持各自来源。

从项目根目录再生成：

```powershell
python scripts/create-app-icon.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-icons.ps1
python scripts/create-icon-overview.py
```

SVG 转 WPF 只依赖 Python 标准库，导出依赖 Windows WPF，总览依赖 Pillow 与微软雅黑。修改图标后需重新构建程序与安装包，避免只替换外部 ICO 而留下旧的 EXE 嵌入图标。

本轮源 SVG 的 SHA256 为 `42853daaa8b8b640649f3b19c32f018d4a64cb699b272b4743c4937804248a90`，ICO 为 `d9c0844262a408017f0d50ed832381f547517c8e8a07fab072840eba81491359`。构建 `20261106-163639-bf6c5ee0` 的程序和安装 EXE 均通过十档嵌入图标逐帧一致性检查，另检查 SVG 副本、PNG 尺寸与透明通道，见 [图标检查](../../artifacts/installers/20261106-163639-bf6c5ee0/verification/icon-package-checks.json)。这些检查验证资源与打包一致性；最终桌面目视确认仍待用户完成。
