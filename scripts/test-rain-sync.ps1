$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget\packages'
$taskDotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$taskBuildRoot = Join-Path $taskRoot ('.local\rain-check-builds\' + [Guid]::NewGuid().ToString('N'))
& $taskDotnet build (Join-Path $taskRoot 'tests\RainSyncHarness\RainSyncHarness.csproj') --configuration Debug --artifacts-path $taskBuildRoot --disable-build-servers --configfile (Join-Path $taskRoot 'NuGet.Config') -p:NuGetAudit=false -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Rain/Changjiang sync harness build failed.' }
& $taskDotnet (Join-Path $taskBuildRoot 'bin\RainSyncHarness\debug\RainSyncHarness.dll') --project-root $taskRoot --verify
if ($LASTEXITCODE -ne 0) { throw 'Rain/Changjiang sync harness verification failed.' }
$taskRun = Get-Content -LiteralPath (Join-Path $taskRoot '.local\rain-sync-harness\latest-run.txt') -Raw -Encoding UTF8
Write-Output ('Synthetic Rain/Changjiang desktop report: ' + (Join-Path $taskRun 'rain-desktop-checks.json'))
