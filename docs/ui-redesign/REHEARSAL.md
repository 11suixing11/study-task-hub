# Windows 首页重构验证记录

本轮将 Stageshow 学习任务预览的三栏布局落实到 Windows 首页：左侧平台导航，中间统一任务清单与统计，右侧固定任务详情与操作。Windows 默认保留全部状态；详细采集依据按需展开，原平台状态、原文时间和估算日期标记保留。窄屏仅用于独立预览，没有修改 Android APK。

## 预览证据

预览入口为 `http://127.0.0.1:4280/?workbench`，可再次运行项目根目录的 [启动Stageshow预览.cmd](../../启动Stageshow预览.cmd)。预览使用演示数据。

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 点击与键盘操作 | 43 项通过，页面无脚本错误；覆盖搜索、筛选、课程选择、完成与恢复、新增、弹窗焦点和窄屏返回 | [交互报告](../../design/stageshow/shots/windows-redesign/interaction-checks.json) |
| 桌面静态画面 | 1200 px，静态 gate 返回 0，无自动检测问题 | [桌面静态报告](../../design/stageshow/shots/windows-redesign/desktop-still-gate.json) |
| 窄屏静态画面 | 390、358 px，静态 gate 均返回 0，无自动检测问题 | [390 px](../../design/stageshow/shots/windows-redesign/compact-390-still-gate.json)、[358 px](../../design/stageshow/shots/windows-redesign/compact-358-still-gate.json) |
| 操作宿主布局 | 1440、390、358 px，均返回 0；控件和内边距检查无异常 | [桌面](../../design/stageshow/shots/windows-redesign/host-desktop-current.json)、[390 px](../../design/stageshow/shots/windows-redesign/host-390-current.json)、[358 px](../../design/stageshow/shots/windows-redesign/host-358-current.json) |
| TypeScript 与构建 | `npm.cmd run typecheck`、`npm.cmd run build` 通过；Vite 使用 runner 配置加载器 | `design/stageshow/package.json` |

静态截图已保存。当前图像读取未提供可靠的目视结论，因此光学对齐、字体观感和视觉效果仍待目视确认；自动布局报告不能代替该确认。

## 动画范围

默认入口为完整静态界面，由用户直接操作。桌面和 390 px 动画采样均为 `inconclusive`，分别有 24、26 个采样间隙超出限制，不能认定动画验收通过；静态与交互通过不改变这一结论。见 [桌面动画报告](../../design/stageshow/shots/windows-redesign/desktop-gate.json)和 [390 px 动画报告](../../design/stageshow/shots/windows-redesign/compact-390-motion-gate.json)。

## 首次三栏改造的历史 Windows 检查

以下是接入 WPF UI 之前的历史记录。当前界面主体和最新发布证据见 [WPF UI 接入](WPF-UI.md)及 [开发进度](../开发进度.md)。

最终构建为 `20261105-155409-0644f6c1`，北京时间 2026-11-06 00:00:33 完成。11 个项目零警告、零错误；2,810 项业务、1,099 项桌面与跨进程、36 项 Python 检查通过，见 [完整报告](../../artifacts/releases/20261105-155409-0644f6c1/verification/verification.json)。[52 项首页检查](../../artifacts/releases/20261105-155409-0644f6c1/verification/dashboard-ui-checks.json)使用隔离合成数据，覆盖 1440 × 960 和最小 1180 × 800 窗口、全部状态默认值、任务表与详情不重叠、表格列无需横向滚动、更多操作、空搜索清除旧操作、长标题与备注滚动以及官方入口参数捕获。正常与最小窗口的任务表高分别为 402、250 DIP；展开同步依据后为 298、186 DIP。

智慧树／U 校园桌面测试曾因 `PlatformActionPanel` 仍强制转换为旧 `WrapPanel` 失败，已改为通用 `Panel`，保留按钮可见、窗口边界和互不重叠断言；最终发布要求整轮重新运行通过，没有拼接旧报告。

所有原生回归使用独立任务库、会话与本地响应；系统浏览器仅捕获参数。正式窗口没有自动操作，官网真实登录与最终任务页面仍由用户核对。[安装报告](../../artifacts/installers/20261105-155409-0644f6c1/verification/installation-checks.json)确认安装文件与 payload 的 SHA256 一致、安装版启动成功、旧库合成标记保留、归档可解密、浏览器目录未导入、覆盖升级和卸载保留外部记录。新版已复制到桌面安装包目录，Android APK SHA256 保持 `3f57c288b244ba07f0b4102cd53659dc151b7aa837fdaf2b3b45971fb8f8165c`；见 [交付清单](../../artifacts/packaging-verification/delivery-checks.json)。
