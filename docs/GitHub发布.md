# GitHub 发布与源码构建

仓库：[11suixing11/study-task-hub](https://github.com/11suixing11/study-task-hub)。Windows 最新安装版为 [1.1.1](https://github.com/11suixing11/study-task-hub/releases/tag/v1.1.1)，提供安装 EXE、便携 ZIP、使用说明和 SHA256 校验。安装包与便携包对应已验证构建 `20261106-184644-b9a71520`；源码另包含随后完成的托盘图标统一，其完整验证构建为 `20261106-193620-910cd4a7`。

Release 附件名称为 `StudyTaskHub-1.1.1-Setup-win-x64.exe`、`StudyTaskHub-1.1.1-Portable-win-x64.zip` 和 `Windows-1.1.1-README.txt`。安装 EXE 与便携 ZIP 的内容和 SHA256 与本地中文命名的 1.1.1 交付文件一致；TXT 补充了 GitHub 文件名与当前登录问题。

2026-11-07 的完整验证报告保存在 [安装包业务与桌面报告](releases/v1.1.1/packaged-build-verification.json) 和 [最新源码完整报告](releases/v1.1.1/source-build-verification.json)。这些测试使用合成数据；当前官方登录限制见 [已知问题](登录状态已知问题.md)。

## Windows 源码构建

需要 Windows 10/11 64 位、Microsoft WebView2、Python 3.9 或更高版本，以及 .NET 10 SDK。将 .NET SDK 放在仓库 `.tools/dotnet`，或者用 Microsoft 官方脚本安装：

```powershell
New-Item -ItemType Directory -Force .tools | Out-Null
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.tools/dotnet-install.ps1'
& '.tools/dotnet-install.ps1' -Channel 10.0 -InstallDir '.tools/dotnet'
& '.\scripts\build.ps1' -VerifyOnly
```

`-VerifyOnly` 编译 11 个项目并运行隔离业务、桌面和重启检查，不选择新的正式构建。需要生成项目启动版本时运行 `scripts/build.ps1`，通过后再使用“启动聚合平台.cmd”。所有构建目录与测试数据都在被 Git 忽略的 `.tools`、`.local` 和 `artifacts` 下。

Windows 安装打包另需 Inno Setup 6.7.4 和官方 WebView2 bootstrapper，按 `scripts/build-installer.ps1` 指定的 `.tools/packaging` 路径准备。Android 源码需 .NET Android workload、Android SDK、JDK 21 及自己的签名密钥；本机签名材料不上传到仓库，现有发布 APK 仍为本地交付的 1.0.0。

## 文件范围

Git 仓库保存源码、测试、平台说明、图标、预览设计和必要第三方许可。安装包放在 Release 附件，不进入 Git 历史。任务数据库、登录归档、WebView profile、签名密钥、依赖缓存和本地日志均不属于发布源码。

历史文档中的 `artifacts/...` 链接对应本地开发证据，未全部上传；1.1.1 的公开下载和验证以本页及 Release 为准。
