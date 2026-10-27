param([switch]$Launch)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $taskRoot '.tools\nuget\http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $taskRoot '.tools\nuget\plugins-cache'
$env:TEMP = Join-Path $taskRoot '.local\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$taskDotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { throw 'Project-local .NET SDK is missing.' }
$taskBuildId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$taskBuildRoot = Join-Path $taskRoot ('.local\builds\' + $taskBuildId)
Push-Location $taskRoot
try {
    foreach ($taskProject in @('src\AccessProbe\AccessProbe.csproj', 'tests\ProbeChecks\ProbeChecks.csproj', 'tests\AutomaticSyncHarness\AutomaticSyncHarness.csproj', 'tests\RainSyncHarness\RainSyncHarness.csproj', 'tests\MoocSyncHarness\MoocSyncHarness.csproj')) {
        & $taskDotnet build $taskProject --configuration Debug --artifacts-path $taskBuildRoot --disable-build-servers --configfile (Join-Path $taskRoot 'NuGet.Config') -p:NuGetAudit=false -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $taskProject" }
    }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\ProbeChecks\debug\ProbeChecks.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Probe checks failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\AutomaticSyncHarness\debug\AutomaticSyncHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'Automatic login and sync verification failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\RainSyncHarness\debug\RainSyncHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'Rain/Changjiang login and sync verification failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\MoocSyncHarness\debug\MoocSyncHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'MOOC login, sync and session restart verification failed; current release retained.' }
    # Build and verify separately from any running app. A release becomes current only after all checks pass.
    $taskReleaseRoot = Join-Path $taskRoot ('artifacts\releases\' + $taskBuildId)
    New-Item -ItemType Directory -Force -Path $taskReleaseRoot | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $taskBuildRoot 'bin\AccessProbe\debug') -Force | Copy-Item -Destination $taskReleaseRoot -Recurse
    $taskManifest = [ordered]@{
        schema = 1
        version = $taskBuildId
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        executable = 'releases/' + $taskBuildId + '/AccessProbe.exe'
    }
    $taskManifestPath = Join-Path $taskRoot 'artifacts\current.json'
    $taskManifestTemp = Join-Path $taskRoot ('artifacts\current-' + $taskBuildId + '.tmp')
    [System.IO.File]::WriteAllText($taskManifestTemp, ($taskManifest | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $taskManifestPath) {
        $taskManifestBackup = Join-Path $taskRoot ('artifacts\current-previous-' + $taskBuildId + '.json')
        [System.IO.File]::Replace($taskManifestTemp, $taskManifestPath, $taskManifestBackup)
    }
    else { [System.IO.File]::Move($taskManifestTemp, $taskManifestPath) }
    Write-Output "Verified login-and-sync release selected: $taskBuildId"
    if ($Launch) { & (Join-Path $PSScriptRoot 'start.ps1') }
} finally {
    Pop-Location
}
