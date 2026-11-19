param([string]$Serial = 'emulator-5560', [Parameter(Mandatory=$true)][string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskAdb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
$taskPackage = 'cn.yuki.learningaggregator'
$taskComponent = $taskPackage + '/crc64dd8222226edccaad.MainActivity'
$taskRun = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$taskArtifacts = [IO.Path]::GetFullPath($ArtifactDirectory)
$taskEvidence = Join-Path $taskArtifacts ('verification\login-recovery-' + $taskRun)
$taskRemote = '/data/user/0/' + $taskPackage + '/files/synthetic-login-recovery-test'
function Invoke-TaskAdb([string[]]$Arguments) {
    $taskLines = @(& $taskAdb -s $Serial @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ('ADB command failed: ' + (($Arguments | Select-Object -First 3) -join ' ')) }
    return ($taskLines | ForEach-Object { $_.ToString() }) -join "`n"
}
function Read-TaskReport([string]$Mode) {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(55)
    do {
        $taskRaw = @(& $taskAdb -s $Serial shell cat ($taskRemote + '/webview-' + $Mode + '.json') 2>$null) -join ''
        if ($LASTEXITCODE -eq 0 -and $taskRaw.StartsWith('{')) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    if (-not $taskRaw.StartsWith('{')) { throw ('WebView report timed out: ' + $Mode) }
    [IO.File]::WriteAllText((Join-Path $taskEvidence ('webview-' + $Mode + '.json')),$taskRaw,[Text.UTF8Encoding]::new($false))
    $taskReport = $taskRaw | ConvertFrom-Json
    if ($taskReport.outcome -ne 'passed' -or $taskReport.officialNetworkAccessed -or $taskReport.productionDataAccessed) { throw ('WebView recovery failed: ' + $taskReport.error) }
    return $taskReport
}
try {
    if ($Serial -notmatch '^emulator-[0-9]+$' -or (Invoke-TaskAdb @('shell','getprop','ro.kernel.qemu')).Trim() -ne '1') { throw 'Only an emulator is permitted.' }
    $taskAvd = (Invoke-TaskAdb @('emu','avd','name')) -split "`n" | Select-Object -First 1
    if ($taskAvd.Trim() -ne 'aggregator-install-test') { throw 'Only the dedicated verification AVD is permitted.' }
    New-Item -ItemType Directory -Force -Path $taskEvidence | Out-Null
    $taskManifest = Get-Content -LiteralPath (Join-Path $taskArtifacts 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskApk = Join-Path $taskArtifacts ('聚合平台-' + $taskManifest.version + '-android.apk')
    $taskApkHash = (Get-FileHash -LiteralPath $taskApk -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($taskApkHash -ne $taskManifest.sha256) { throw 'The APK differs from its build manifest.' }
    $null = Invoke-TaskAdb @('root')
    $null = Invoke-TaskAdb @('wait-for-device')
    $taskInstalledApk = (Invoke-TaskAdb @('shell','pm','path',$taskPackage)).Trim() -replace '^package:',''
    if ($taskInstalledApk -notmatch '^/data/app/[a-zA-Z0-9_/+~.=-]+/base\.apk$') { throw 'Installed APK path is unrecognized.' }
    $taskInstalledHash = (Invoke-TaskAdb @('shell','sha256sum',$taskInstalledApk)).Split(' ')[0].Trim()
    if ($taskInstalledHash -ne $taskApkHash) { throw 'The installed APK differs from the artifact under test.' }
    $null = Invoke-TaskAdb @('shell','svc','wifi','disable')
    $null = Invoke-TaskAdb @('shell','svc','data','disable')
    $null = Invoke-TaskAdb @('shell','am','force-stop',$taskPackage)
    $null = & $taskAdb -s $Serial shell test -d $taskRemote 2>$null
    if ($LASTEXITCODE -eq 0) { $null = Invoke-TaskAdb @('shell','mv',$taskRemote,($taskRemote + '-previous-' + $taskRun)) }
    $null = Invoke-TaskAdb @('logcat','-b','crash','-c')
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent,'--es','synthetic_login_recovery_test','webview')
    $taskWeb = Read-TaskReport 'webview'
    if ($taskWeb.checks -lt 30) { throw 'WebView recovery checks are incomplete.' }
    $null = Invoke-TaskAdb @('shell','screencap','-p','/sdcard/synthetic-login-recovery.png')
    $null = Invoke-TaskAdb @('pull','/sdcard/synthetic-login-recovery.png',(Join-Path $taskEvidence 'authorization-result.png'))
    $null = Invoke-TaskAdb @('shell','am','force-stop',$taskPackage)
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent,'--es','synthetic_login_recovery_test','restart')
    $taskRestart = Read-TaskReport 'restart'
    if ($taskRestart.checks -ne 3) { throw 'WebView process restart checks are incomplete.' }
    $taskCrash = Invoke-TaskAdb @('logcat','-b','crash','-d')
    [IO.File]::WriteAllText((Join-Path $taskEvidence 'crash.log'),$taskCrash,[Text.UTF8Encoding]::new($false))
    if ($taskCrash -match 'cn\.yuki\.learningaggregator|UnsatisfiedLinkError') { throw 'WebView recovery produced a native crash.' }
    $taskResult = [ordered]@{schema=1;outcome='passed';apkSha256=$taskApkHash;checks=($taskWeb.checks + $taskRestart.checks);webviewChecks=$taskWeb.checks;restartChecks=$taskRestart.checks;
        avd='aggregator-install-test';officialNetworkAccessed=$false;productionDataAccessed=$false;realAccountAcceptance='pending';finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    [IO.File]::WriteAllText((Join-Path $taskEvidence 'login-recovery-summary.json'),($taskResult | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Write-Output ('Android login recovery verification completed: ' + $taskEvidence)
} catch {
    if (Test-Path -LiteralPath $taskEvidence) { [IO.File]::WriteAllText((Join-Path $taskEvidence 'failure.json'),(@{outcome='failed';message=$_.Exception.Message}|ConvertTo-Json),[Text.UTF8Encoding]::new($false)) }
    throw
}
