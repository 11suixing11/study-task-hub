$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget\packages'
$taskDotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$taskBuildRoot = Join-Path $taskRoot ('.local\mooc-check-builds\' + [Guid]::NewGuid().ToString('N'))
& $taskDotnet build (Join-Path $taskRoot 'tests\MoocSyncHarness\MoocSyncHarness.csproj') --configuration Debug --artifacts-path $taskBuildRoot --disable-build-servers --configfile (Join-Path $taskRoot 'NuGet.Config') -p:NuGetAudit=false -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'MOOC sync harness build failed.' }
& $taskDotnet (Join-Path $taskBuildRoot 'bin\MoocSyncHarness\debug\MoocSyncHarness.dll') --project-root $taskRoot --verify
if ($LASTEXITCODE -ne 0) { throw 'MOOC sync harness verification failed.' }
$taskRun = Get-Content -LiteralPath (Join-Path $taskRoot '.local\mooc-sync-harness\latest-run.txt') -Raw -Encoding UTF8
Write-Output ('Synthetic MOOC desktop report: ' + (Join-Path $taskRun 'mooc-desktop-checks.json'))
