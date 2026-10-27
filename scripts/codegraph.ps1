$ErrorActionPreference = 'Stop'
$taskProjectRoot = Split-Path $PSScriptRoot -Parent
$taskCodegraph = Join-Path $taskProjectRoot '.tools\codegraph\v1.6.2\codegraph-win32-x64\bin\codegraph.cmd'
if (-not (Test-Path -LiteralPath $taskCodegraph)) { throw 'Project-local CodeGraph is missing. See docs/CodeGraph.md.' }
$env:CODEGRAPH_TELEMETRY = '0'
Push-Location $taskProjectRoot
try {
    & $taskCodegraph @args
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
