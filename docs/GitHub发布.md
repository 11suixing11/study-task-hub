# GitHub 发布与源码构建

仓库：[11suixing11/study-task-hub](https://github.com/11suixing11/study-task-hub)。最新发布为 [1.1.2](https://github.com/11suixing11/study-task-hub/releases/tag/v1.1.2)，提供 Android 登录跳转修复版（VersionCode 4）、Windows 安装 EXE 与便携 ZIP、中文说明、验证摘要和 SHA256 校验。Android 构建为 `20261111-061250-2043227f`，Windows 为 `20261111-060027-e533c14f`。

Release 安装附件名称为 `StudyTaskHub-1.1.2-Android.apk`、`StudyTaskHub-1.1.2-Setup-win-x64.exe` 和 `StudyTaskHub-1.1.2-Portable-win-x64.zip`，与本地中文命名的交付包字节和 SHA256 一致。说明附件为 `Android-1.1.2-README.txt`、`Windows-1.1.2-README.txt`，校验附件为 `SHA256SUMS.txt`，综合验证附件为 `StudyTaskHub-1.1.2-verification.json`。Android 第三方许可另附 `StudyTaskHub-1.1.2-Android-Licenses.zip`，转发 APK 时请一并保留；Windows 许可已包含在包内。

公开验证见 [Android](releases/v1.1.2/android-verification.json)、[Windows](releases/v1.1.2/windows-verification.json)、[完整业务与桌面报告](releases/v1.1.2/packaged-build-verification.json)和 [隔离安装](releases/v1.1.2/installation-checks.json)。Android 登录跳转与课程会话的 16 项检查通过，真实手机课程读取尚未复验，目视验收待完成。Windows 安装和升级实测使用同脚本、同 payload 的隔离 TestInstallation 变体，交付 Setup 本身未直接安装实测。当前官方登录限制见 [已知问题](登录状态已知问题.md)。[1.1.1 报告](releases/v1.1.1/packaged-build-verification.json)保留历史范围。

## Windows 源码构建

需要 Windows 10/11 64 位、Microsoft WebView2、Python 3.9 或更高版本，以及 .NET 10 SDK。将 .NET SDK 放在仓库 `.tools/dotnet`，或者用 Microsoft 官方脚本安装：

```powershell
New-Item -ItemType Directory -Force .tools | Out-Null
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.tools/dotnet-install.ps1'
& '.tools/dotnet-install.ps1' -Channel 10.0 -InstallDir '.tools/dotnet'
& '.\scripts\build.ps1' -VerifyOnly
```

`-VerifyOnly` 编译 11 个项目并运行隔离业务、桌面和重启检查，不选择新的正式构建。需要生成项目启动版本时运行 `scripts/build.ps1`，通过后再使用“启动聚合平台.cmd”。所有构建目录与测试数据都在被 Git 忽略的 `.tools`、`.local` 和 `artifacts` 下。

Windows 安装打包另需 Inno Setup 6.7.4 和官方 WebView2 bootstrapper，按 `scripts/build-installer.ps1` 指定的 `.tools/packaging` 路径准备。Android 源码需 .NET Android workload、Android SDK、JDK 21 及自己的签名密钥；本机签名材料不上传到仓库。构建可执行 `scripts/build-android.ps1 -Version 1.1.2 -VersionCode 4`，环境路径可通过参数指定。1.1.2 发布 APK 沿用旧版签名，修复学习通登录返回并提供官网二维码辅助登录；自己的签名不能覆盖本机交付签名。验收范围见 [Android 摘要](releases/android-1.1.2/verification.json)与 [扫码说明](Android应用授权.md)。

## 文件范围

Git 仓库保存源码、测试、平台说明、图标、预览设计和必要第三方许可。安装包放在 Release 附件，不进入 Git 历史。任务数据库、登录归档、WebView profile、签名密钥、依赖缓存和本地日志均不属于发布源码。

历史文档中的 `artifacts/...` 链接对应本地开发证据，未全部上传；当前 1.1.2 的公开下载和验证以本页及 Release 为准。
