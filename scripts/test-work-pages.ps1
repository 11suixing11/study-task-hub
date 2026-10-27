$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget\packages'
$env:TEMP = Join-Path $taskRoot '.local\temp'
$env:TMP = $env:TEMP
$taskDotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$taskProject = Join-Path $taskRoot 'tests\WorkPageHarness\WorkPageHarness.csproj'
& $taskDotnet restore $taskProject --configfile (Join-Path $taskRoot 'NuGet.Config') --disable-parallel -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Harness restore failed.' }
& $taskDotnet build $taskProject --no-restore --configuration Debug --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Harness build failed.' }
$taskExecutable = Join-Path $taskRoot 'tests\WorkPageHarness\bin\Debug\net10.0-windows\WorkPageHarness.exe'
$taskArguments = '--project-root "{0}"' -f $taskRoot
$taskProcess = Start-Process -FilePath $taskExecutable -ArgumentList $taskArguments -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
Write-Output "Offline harness started; process ID $($taskProcess.Id). All requests use synthetic local responses."
