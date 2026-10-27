<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.
- This project pins CodeGraph 1.6.2 under `.tools/codegraph/`. In PowerShell, use `./scripts/codegraph.ps1 explore "<symbol names or question>"`. The wrapper sets the project directory and disables telemetry. Use `sync` before CLI analysis after edits; the MCP server updates automatically while connected.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->

## 项目开发约定

- 所有平台任务汇总到同一个首页清单；新增平台必须接入自动同步、来源统计、状态和截止日期，以及具体任务的官方跳转入口。
- 正式任务库 `.local/data/tasks.db` 与登录环境保留；测试使用独立数据目录及本地响应，构建通过后更新发布清单。
- 用户完成官方登录；后台只读采集课程与任务元数据，不代替答题或提交。诊断记录计数、字段名与结果，不记录账号凭据或作答正文。
- 未知结构、分页上限、登录失效不能当作没有任务；平台原始状态和时间依据保留，手动修改优先。
- 默认不启动子代理，除非用户明确要求并行代理工作。
