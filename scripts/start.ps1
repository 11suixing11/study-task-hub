param([switch]$FromShortcut)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:TEMP = Join-Path $taskRoot '.local\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$taskManifestPath = Join-Path $taskRoot 'artifacts\current.json'
if (-not (Test-Path -LiteralPath $taskManifestPath)) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    if (-not $?) { throw 'Build failed. See the output above.' }
}
$taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskManifest.schema -ne 1 -or $taskManifest.executable -notmatch '^releases/[0-9]{8}-[0-9]{6}-[a-f0-9]{8}/AccessProbe\.exe$') {
    throw 'Current release manifest is invalid. Rebuild with scripts\build.ps1.'
}
$taskArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))
$taskExecutable = [System.IO.Path]::GetFullPath((Join-Path $taskArtifactsRoot $taskManifest.executable))
if (-not $taskExecutable.StartsWith($taskArtifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Release path is outside the project artifacts directory.' }
if (-not (Test-Path -LiteralPath $taskExecutable)) { throw 'AccessProbe executable is missing.' }
$taskArguments = '--project-root "{0}"' -f $taskRoot
# Serialize entry-point launches and reuse a running project instance so old versions cannot overwrite sync results.
$taskLaunchHash = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash(
    [System.Text.Encoding]::UTF8.GetBytes($taskRoot.ToUpperInvariant()))).Replace('-', '')
$taskLaunchMutex = [System.Threading.Mutex]::new($false, ('Local\LearningTaskAggregator.Launch.' + $taskLaunchHash))
$taskLaunchLockHeld = $false
$taskShowExitHint = $false
try {
    try { $taskLaunchLockHeld = $taskLaunchMutex.WaitOne([TimeSpan]::FromSeconds(10)) }
    catch [System.Threading.AbandonedMutexException] { $taskLaunchLockHeld = $true }
    if (-not $taskLaunchLockHeld) { throw 'Another project launch is still in progress. Retry shortly.' }
    $taskRootPattern = '(?:^|\s)--project-root\s+(?:"' + [regex]::Escape($taskRoot) + '"|' + [regex]::Escape($taskRoot) + ')(?:\s|$)'
    $taskExisting = @(Get-CimInstance Win32_Process -Filter "Name = 'AccessProbe.exe'" | Where-Object {
        $_.ExecutablePath -and $_.CommandLine -and $_.CommandLine -match $taskRootPattern -and
        [System.IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($taskArtifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($taskExisting.Count -gt 0) {
        $taskSelected = $taskExisting | Where-Object {
            [System.IO.Path]::GetFullPath($_.ExecutablePath).Equals($taskExecutable, [System.StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($taskSelected) { Write-Output "Login-and-sync dashboard is already running (release $($taskManifest.version), process ID $($taskSelected.ProcessId)); no duplicate started." }
        else {
            $taskRunningRelease = Split-Path (Split-Path $taskExisting[0].ExecutablePath -Parent) -Leaf
            Write-Output "聚合平台仍在运行旧版：$taskRunningRelease"
            Write-Output "当前修复版：$($taskManifest.version)"
            Write-Output '请先在聚合平台主清单点击“退出程序”，再重新双击启动入口。'
            Write-Output '右上角 × 只会隐藏到托盘，无法完成版本切换。'
            $taskShowExitHint = $FromShortcut.IsPresent
        }
        return
    }
    $taskProcess = Start-Process -FilePath $taskExecutable -ArgumentList $taskArguments -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
    Write-Output "Login-and-sync dashboard started (release $($taskManifest.version), process ID $($taskProcess.Id))."
} finally {
    if ($taskLaunchLockHeld) { $taskLaunchMutex.ReleaseMutex() }
    $taskLaunchMutex.Dispose()
    if ($taskShowExitHint) { Read-Host '按回车关闭此提示' | Out-Null }
}
