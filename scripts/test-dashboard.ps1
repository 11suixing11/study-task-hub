param([switch]$ReuseLastRun)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget\packages'
$taskDotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$taskProject = Join-Path $taskRoot 'tests\TaskDashboardHarness\TaskDashboardHarness.csproj'
& $taskDotnet build $taskProject --configuration Debug --disable-build-servers -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Dashboard harness build failed.' }
$taskArgv = @((Join-Path $taskRoot 'tests\TaskDashboardHarness\bin\Debug\net10.0-windows\TaskDashboardHarness.dll'), '--project-root', $taskRoot)
if ($ReuseLastRun) {
    $taskLast = Join-Path $taskRoot '.local\task-dashboard-harness\latest-run.txt'
    if (-not (Test-Path -LiteralPath $taskLast)) { throw 'No previous synthetic run.' }
    $taskRun = (Get-Content -LiteralPath $taskLast -Encoding utf8 -Raw).Trim()
    $taskArgv += @('--run-root', $taskRun)
}
& $taskDotnet @taskArgv
