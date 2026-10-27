# CodeGraph

项目已安装并默认启用 [CodeGraph 1.6.2](https://github.com/colbymchenry/codegraph/releases/tag/v1.6.2)。官方 Windows x64 发行包保存在 `.tools/codegraph/v1.6.2/codegraph-win32-x64`；不依赖全局 Node 或全局 npm 安装。下载校验和与 MCP 初始化证据见 [安装检查](../artifacts/synthetic-checks/codegraph-install-20261024.json)。

`.codex/config.toml` 默认启用项目 stdio MCP，遥测关闭；根目录 [AGENTS.md](../AGENTS.md) 要求理解和定位源码时先使用 CodeGraph。新项目会话会加载 MCP；当前开发会话已实际使用 CLI 查询和更新索引。

```powershell
.\scripts\codegraph.ps1 sync .
.\scripts\codegraph.ps1 explore 'MoocSyncService ReadAccountAsync'
.\scripts\codegraph.ps1 node src/AccessProbe/MoocSyncService.cs
```

编辑源码后先执行 `sync .` 再查询，避免 CLI 使用旧关系图。MCP 连接期间会自动更新。`codegraph.json` 排除 `.local/`、`.tools/`、`artifacts/`、bin 和 obj，因此正式任务库、会话归档、浏览器 profile 和账号诊断不进入索引；测试与脚本降低检索优先级。

如目录移动，更新 `.codex/config.toml` 的 `command`、`args` 和 `cwd` 为新项目路径，再启动新的项目会话。原用户 Codex 配置的信任设置在安装时已备份。该组件帮助定位与理解源码，不改变聚合平台的采集流程。
