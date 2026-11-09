# macOS 风格界面预览主机

此主机直接加载 `AccessProbe` 的 `TaskDashboardWindow` 与 `FluentTheme.xaml`。独立创建 16 条无链接演示任务，不调用正式 `App` 入口、同步服务或提醒服务；平台连接显示预览提示。普通启动每次使用新的 `demo-data/<run>/tasks.db`，关闭窗口直接退出。

在项目根目录使用项目内 .NET SDK 发布：

```powershell
$env:DOTNET_ROOT = "$PWD\.tools\dotnet"
$env:DOTNET_CLI_HOME = "$PWD\.tools\dotnet-home"
$env:NUGET_PACKAGES = "$PWD\.tools\nuget\packages"
& "$env:DOTNET_ROOT\dotnet.exe" publish packaging\windows\MacPreview\MacPreview.csproj --configuration Release --runtime win-x64 --self-contained true --output .local\mac-preview-publish -p:RuntimeFrameworkVersion=10.0.13 -p:PublishTrimmed=false
```

`MacPreview.exe --smoke-check` 执行 26 项演示库检查：真实窗口和主题、三列表、侧栏与详情、1100×700 最小窗口、本地完成状态保存，以及可见图标实际尺寸的非空渲染、任务行与详情平台图形一致性、主按钮图标前景色。另检查八个平台课程窗口的品牌与操作图形、760×620 最小布局、收起后操作可见、搜索与批量勾选、关闭保留选课、编辑必填验证与保存、空清单恢复以及未选课程历史保留。退出前保存首页、课程窗口、编辑窗口、空清单等真实截图、`icon-render-metrics.json`、`dialog-layout-metrics.json` 和报告。所有数据来自预览目录下的独立合成库。该检查不能证明 Windows DPAPI、跨进程登录恢复或真实账号同步，目视验收单独记录。

便携包仅提供 `MacPreview.exe` 作为启动应用；发布目录中引用项目产生的 `AccessProbe.exe` 应在组装时排除，保留所需 `AccessProbe.dll` 和运行时依赖。交付时排除测试数据与 PDB，保留第三方许可。当前设计与验证状态见 [macOS 设计说明](../../../docs/ui-redesign/macOS-HIG.md)。

主图标与产品共用 [app.ico](../../../src/AccessProbe/Assets/Icons/app.ico)。28 个 Key 使用 Microsoft Fluent System Icons，7 个另有官方彩色绘图；品牌和八个平台共 9 个原创图形保留。图形源、导出资源与维护方法见 [图标系统](../../../docs/ui-redesign/Icons.md) 和 [Fluent 接入](../../../docs/ui-redesign/Fluent-Icons.md)。组装时保留 Fluent MIT、NOTICE、固定来源清单、35 个原始 SVG 和生成的 XAML。组装后再运行同一 26 项检查，归档生成的演示库及报告，交付包仅保留 verification 与 screenshots 下的证据。
