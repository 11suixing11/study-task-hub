param([switch]$Launch, [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
if ($Launch -and $VerifyOnly) { throw 'Launch and VerifyOnly cannot be used together.' }
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
New-Item -ItemType Directory -Force -Path $taskBuildRoot | Out-Null
$taskBuildLog = Join-Path $taskBuildRoot 'build.log'
$taskProjects = @('src\AccessProbe\AccessProbe.csproj', 'tests\ProbeChecks\ProbeChecks.csproj',
    'tests\AutomaticSyncHarness\AutomaticSyncHarness.csproj', 'tests\RainSyncHarness\RainSyncHarness.csproj',
    'tests\MoocSyncHarness\MoocSyncHarness.csproj', 'tests\WorkPageHarness\WorkPageHarness.csproj',
    'tests\TaskDashboardHarness\TaskDashboardHarness.csproj', 'tests\SyncDiagnosticHarness\SyncDiagnosticHarness.csproj',
    'tests\XuetangSyncHarness\XuetangSyncHarness.csproj', 'tests\MetadataSyncHarness\MetadataSyncHarness.csproj',
    'tests\PtaSyncHarness\PtaSyncHarness.csproj')
Push-Location $taskRoot
try {
    foreach ($taskProject in $taskProjects) {
        & $taskDotnet build $taskProject --configuration Debug --artifacts-path $taskBuildRoot --disable-build-servers --configfile (Join-Path $taskRoot 'NuGet.Config') -p:NuGetAudit=false -v:minimal 2>&1 | Tee-Object -FilePath $taskBuildLog -Append
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $taskProject" }
    }
    $taskBusinessOutput = @(& $taskDotnet (Join-Path $taskBuildRoot 'bin\ProbeChecks\debug\ProbeChecks.dll'))
    $taskBusinessExit = $LASTEXITCODE
    $taskBusinessOutput | Tee-Object -FilePath (Join-Path $taskBuildRoot 'business-checks.log')
    if ($taskBusinessExit -ne 0) { throw 'Probe checks failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\TaskDashboardHarness\debug\TaskDashboardHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard layout and interaction verification failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\AutomaticSyncHarness\debug\AutomaticSyncHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'Automatic login and sync verification failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\RainSyncHarness\debug\RainSyncHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'Rain/Changjiang login and sync verification failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\MoocSyncHarness\debug\MoocSyncHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'MOOC login, sync and session restart verification failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\XuetangSyncHarness\debug\XuetangSyncHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'Xuetang login, sync and session restart verification failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\MetadataSyncHarness\debug\MetadataSyncHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'Zhihuishu and legacy Ucampus metadata, FiF-disabled policy and session restart verification failed; current release retained.' }
    & $taskDotnet (Join-Path $taskBuildRoot 'bin\PtaSyncHarness\debug\PtaSyncHarness.dll') --project-root $taskRoot --verify
    if ($LASTEXITCODE -ne 0) { throw 'PTA teaching-course, problem-set, login and session restart verification failed; current release retained.' }
    $taskReports = [ordered]@{
        'task-dashboard-harness' = @('dashboard-ui-checks.json')
        'automatic-sync-harness' = @('automatic-sync-checks.json', 'session-restart-checks.json')
        'rain-sync-harness' = @('rain-desktop-checks.json')
        'mooc-sync-harness' = @('mooc-desktop-checks.json', 'mooc-session-restart-checks.json')
        'xuetang-sync-harness' = @('xuetang-desktop-checks.json', 'xuetang-session-restart-checks.json')
        'metadata-sync-harness' = @('metadata-desktop-checks.json', 'metadata-session-restart-checks.json')
        'pta-sync-harness' = @('pta-desktop-checks.json', 'pta-session-restart-checks.json')
    }
    $taskEvidenceRoot = Join-Path $taskBuildRoot 'verification'
    New-Item -ItemType Directory -Path $taskEvidenceRoot | Out-Null
    $taskPython = (Get-Command python.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    & $taskPython (Join-Path $taskRoot 'tests\check-sync-status.py') --report (Join-Path $taskEvidenceRoot 'sync-status-checks.json')
    if ($LASTEXITCODE -ne 0) { throw 'Read-only sync status accounting verification failed; current release retained.' }
    $taskStatusReport = Get-Content -LiteralPath (Join-Path $taskEvidenceRoot 'sync-status-checks.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskStatusReport.outcome -ne 'passed') { throw 'Sync status verification report failed; current release retained.' }
    $taskEvidence = @{}
    foreach ($taskFamily in $taskReports.Keys) {
        $taskHarnessRoot = [System.IO.Path]::GetFullPath((Join-Path $taskRoot ('.local\' + $taskFamily)))
        $taskHarnessRun = [System.IO.Path]::GetFullPath((Get-Content -LiteralPath (Join-Path $taskHarnessRoot 'latest-run.txt') -Raw -Encoding UTF8).Trim())
        if (-not $taskHarnessRun.StartsWith($taskHarnessRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Harness report path is outside its test directory.' }
        foreach ($taskReportFile in $taskReports[$taskFamily]) {
            $taskReportPath = Join-Path $taskHarnessRun $taskReportFile
            $taskReport = Get-Content -LiteralPath $taskReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($taskReport.outcome -ne 'passed') { throw "Verification report failed: $taskReportFile" }
            Copy-Item -LiteralPath $taskReportPath -Destination (Join-Path $taskEvidenceRoot $taskReportFile)
            $taskEvidence[$taskReportFile] = $taskReport
        }
    }
    $taskBusinessCategories = @()
    foreach ($taskBusinessLine in $taskBusinessOutput) {
        if ($taskBusinessLine -match '^PASS: (\d+) (.+)$') { $taskBusinessCategories += [ordered]@{ checks = [int]$Matches[1]; scope = $Matches[2] } }
    }
    if ($taskBusinessCategories.Count -eq 0) { throw 'Business check totals are missing.' }
    $taskAutomatic = $taskEvidence['automatic-sync-checks.json']
    $taskVerification = [ordered]@{
        schema = 1
        version = $taskBuildId
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        outcome = 'passed'
        projects = $taskProjects.Count
        businessChecks = [int]($taskBusinessCategories | ForEach-Object { $_.checks } | Measure-Object -Sum).Sum
        businessCategories = $taskBusinessCategories
        syncStatusChecks = $taskStatusReport.checks
        desktopChecks = [ordered]@{
            dashboardUi = $taskEvidence['dashboard-ui-checks.json'].checks
            automaticSync = $taskAutomatic.checks
            taskNavigation = $taskAutomatic.taskNavigationChecks
            sessionRaces = $taskAutomatic.sessionRaceChecks
            chaoxingProcessRestarts = $taskEvidence['session-restart-checks.json'].checks
            rainAndUnifiedDashboard = $taskEvidence['rain-desktop-checks.json'].checks
            moocAndCourseSelection = $taskEvidence['mooc-desktop-checks.json'].checks
            moocProcessRestarts = $taskEvidence['mooc-session-restart-checks.json'].checks
            xuetangAndUnifiedDashboard = $taskEvidence['xuetang-desktop-checks.json'].checks
            xuetangProcessRestarts = $taskEvidence['xuetang-session-restart-checks.json'].checks
            otherPlatformConnections = $taskEvidence['metadata-desktop-checks.json'].checks
            otherPlatformProcessRestarts = $taskEvidence['metadata-session-restart-checks.json'].checks
            ptaAndTeachingCourses = $taskEvidence['pta-desktop-checks.json'].checks
            ptaProcessRestarts = $taskEvidence['pta-session-restart-checks.json'].checks
        }
        data = 'synthetic_only'
        network = 'local_handlers_and_webview_fixtures_only'
        externalBrowser = 'captured_without_launch'
        officialWindowAcceptance = 'pending'
    }
    $taskVerification.desktopChecks.total = [int]($taskVerification.desktopChecks.Values | Measure-Object -Sum).Sum
    [System.IO.File]::WriteAllText((Join-Path $taskEvidenceRoot 'verification.json'), ($taskVerification | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $taskBuildLog -Destination (Join-Path $taskEvidenceRoot 'build.log')
    Copy-Item -LiteralPath (Join-Path $taskBuildRoot 'business-checks.log') -Destination (Join-Path $taskEvidenceRoot 'business-checks.log')
    if ($VerifyOnly) {
        Write-Output "Verification completed: $taskEvidenceRoot"
        return
    }
    # Build and verify separately from any running app. A release becomes current only after all checks pass.
    $taskReleaseRoot = Join-Path $taskRoot ('artifacts\releases\' + $taskBuildId)
    New-Item -ItemType Directory -Force -Path $taskReleaseRoot | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $taskBuildRoot 'bin\AccessProbe\debug') -Force | Copy-Item -Destination $taskReleaseRoot -Recurse
    Copy-Item -LiteralPath $taskEvidenceRoot -Destination (Join-Path $taskReleaseRoot 'verification') -Recurse
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
