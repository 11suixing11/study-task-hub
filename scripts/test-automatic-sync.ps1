param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget\packages'
$taskDotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $taskDotnet build (Join-Path $taskRoot 'tests\AutomaticSyncHarness\AutomaticSyncHarness.csproj') --configuration Debug --disable-build-servers -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Automatic sync harness build failed.' }
$taskArguments = @((Join-Path $taskRoot 'tests\AutomaticSyncHarness\bin\Debug\net10.0-windows\AutomaticSyncHarness.dll'), '--project-root', $taskRoot)
if ($VerifyOnly) { $taskArguments += '--verify' }
& $taskDotnet @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Automatic sync harness verification failed.' }
