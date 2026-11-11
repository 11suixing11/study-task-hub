param([string]$Serial = 'emulator-5560', [string]$ExpectedAvd = 'aggregator-install-test', [string]$Version = '1.1.2')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
$env:ANDROID_USER_HOME = Join-Path $taskRoot '.local\android-build-home'
$taskAdb = Join-Path $taskSdk 'platform-tools\adb.exe'
$taskPackage = 'cn.yuki.learningaggregator'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
$taskApk = Join-Path $taskRoot ('artifacts\android\聚合平台-' + $Version + '-android.apk')
$taskEvidence = Join-Path $taskRoot 'artifacts\android\verification'
$taskRun = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$taskProgressRoot = Join-Path $taskRoot '.local\android-installation-runs'
New-Item -ItemType Directory -Force -Path $taskProgressRoot | Out-Null
$taskProgress = Join-Path $taskProgressRoot 'android-installation-progress.json'
$taskRunRoot = Join-Path $taskEvidence $taskRun

function Save-TaskProgress($value) {
    [IO.File]::WriteAllText($taskProgress, ($value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
function Invoke-TaskAdb([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $taskOutput = @(& $taskAdb -s $Serial @Arguments 2>&1)
    $taskExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($taskExit -ne 0) { throw ('ADB command failed: ' + ($Arguments | Select-Object -First 3) -join ' ') }
    return ($taskOutput | ForEach-Object { $_.ToString() }) -join "`n"
}
function Capture-TaskPage([string]$Name) {
    $taskRemote = '/sdcard/aggregator-installation-' + $taskRun + '-' + $Name
    $null = Invoke-TaskAdb @('shell','uiautomator','dump',($taskRemote + '.xml'))
    $taskLocal = Join-Path $taskRunRoot ($Name + '.xml')
    $null = Invoke-TaskAdb @('pull',($taskRemote + '.xml'),$taskLocal)
    $null = Invoke-TaskAdb @('shell','screencap','-p',($taskRemote + '.png'))
    $null = Invoke-TaskAdb @('pull',($taskRemote + '.png'),(Join-Path $taskRunRoot ($Name + '.png')))
    return [xml]([IO.File]::ReadAllText($taskLocal))
}
function Read-TaskReport([string]$Name) {
    $taskReport = Join-Path $taskRunRoot ($Name + '.json')
    $taskRemote = '/data/user/0/' + $taskPackage + '/files/synthetic-installation-test/self-test.json'
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $ErrorActionPreference = 'Continue'
        $taskRaw = @(& $taskAdb -s $Serial shell cat $taskRemote 2>$null) -join ''
        $ErrorActionPreference = 'Stop'
        if ($taskRaw.StartsWith('{')) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    if (-not $taskRaw.StartsWith('{')) { throw 'Synthetic installation report timed out.' }
    [IO.File]::WriteAllText($taskReport, $taskRaw, [Text.UTF8Encoding]::new($false))
    $taskValue = $taskRaw | ConvertFrom-Json
    if ($taskValue.Outcome -ne 'passed' -or $taskValue.OfficialNetworkAccessed -or $taskValue.ProductionDataAccessed) { throw 'Synthetic native checks did not pass.' }
    return $taskValue
}
try {
    if ($Serial -notmatch '^emulator-[0-9]+$') { throw 'This verification accepts an emulator only.' }
    New-Item -ItemType Directory -Force -Path $taskRunRoot | Out-Null
    if ((Invoke-TaskAdb @('shell','getprop','ro.kernel.qemu')).Trim() -ne '1') { throw 'The target is not an emulator.' }
    $taskAvd = (Invoke-TaskAdb @('emu','avd','name')) -split "`n" | Select-Object -First 1
    if ($taskAvd.Trim() -ne $ExpectedAvd -or $ExpectedAvd -ne 'aggregator-install-test') { throw 'The target is not the dedicated installation-test AVD.' }
    $null = Invoke-TaskAdb @('shell','svc','wifi','disable')
    $null = Invoke-TaskAdb @('shell','svc','data','disable')
    $null = Invoke-TaskAdb @('root')
    $null = Invoke-TaskAdb @('wait-for-device')
    $taskComponent = $taskPackage + '/crc64dd8222226edccaad.MainActivity'
    Save-TaskProgress @{outcome='running';stage='install';serial=$Serial;run=$taskRun}
    $null = Invoke-TaskAdb @('shell','am','force-stop',$taskPackage)
    $taskInstall = Invoke-TaskAdb @('install','--no-streaming','-r',$taskApk)
    if ($taskInstall -notmatch 'Success') { throw 'Release APK installation failed.' }
    $null = Invoke-TaskAdb @('logcat','-b','crash','-c')

    # The previous isolated fixture proves upgrade preserves both task records and Keystore cookies.
    $taskPrevious = '/data/user/0/' + $taskPackage + '/files/synthetic-installation-test'
    $null = Invoke-TaskAdb @('shell','rm','-f',($taskPrevious + '/self-test.json'))
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent,'--ez','synthetic_self_test','true','--ez','synthetic_restart_check','true')
    $taskUpgrade = Read-TaskReport 'upgrade-restart-checks'
    if ($taskUpgrade.Checks -ne 4 -or -not $taskUpgrade.RestartChecked) { throw 'Upgrade persistence checks are incomplete.' }
    $null = Invoke-TaskAdb @('shell','am','force-stop',$taskPackage)
    $null = Invoke-TaskAdb @('shell','mv',$taskPrevious,($taskPrevious + '-previous-' + $taskRun))

    Save-TaskProgress @{outcome='running';stage='native-synthetic-checks';serial=$Serial;run=$taskRun}
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent,'--ez','synthetic_self_test','true')
    $taskSynthetic = Read-TaskReport 'synthetic-checks'
    if ($taskSynthetic.Checks -ne 47 -or $taskSynthetic.Tasks -ne 2) { throw 'Native synthetic checks are incomplete.' }
    $null = Invoke-TaskAdb @('shell','am','force-stop',$taskPackage)
    $null = Invoke-TaskAdb @('shell','rm','-f',($taskPrevious + '/self-test.json'))
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent,'--ez','synthetic_self_test','true','--ez','synthetic_restart_check','true')
    Start-Sleep -Seconds 1
    $taskRestart = Read-TaskReport 'restart-checks'
    if ($taskRestart.Checks -ne 4 -or -not $taskRestart.RestartChecked) { throw 'Process restart checks are incomplete.' }

    Save-TaskProgress @{outcome='running';stage='dashboard-and-cold-receivers';serial=$Serial;run=$taskRun}
    $null = Invoke-TaskAdb @('shell','am','force-stop',$taskPackage)
    $null = Invoke-TaskAdb @('shell','pm','grant',$taskPackage,'android.permission.POST_NOTIFICATIONS')
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent)
    Start-Sleep -Seconds 2
    $taskPage = Capture-TaskPage 'dashboard'
    $taskTitle = $taskPage.SelectSingleNode('//*[@text="学习任务" or @text="聚合平台"]')
    if ($null -eq $taskTitle -or $taskPage.OuterXml -match 'keeps stopping') { throw 'Dashboard did not start normally.' }
    $taskNumbers = [regex]::Matches($taskTitle.bounds,'\d+') | ForEach-Object { [int]$_.Value }
    if ($taskNumbers[1] -le 16) { throw 'Dashboard title overlaps the system status bar.' }
    if ($taskPage.OuterXml.Contains([char]0xFFFD)) { throw 'Dashboard contains replacement characters.' }
    $taskDashboardPid = (Invoke-TaskAdb @('shell','pidof',$taskPackage)).Trim()
    if (-not $taskDashboardPid) { throw 'Dashboard process exited.' }

    # Kill only this isolated app process, leaving Android free to start the alarm receiver cold.
    $null = Invoke-TaskAdb @('shell','input','keyevent','KEYCODE_HOME')
    $null = Invoke-TaskAdb @('shell','am','kill',$taskPackage)
    $taskReminderComponent = $taskPackage + '/crc64dd8222226edccaad.MobileReminderReceiver'
    $null = Invoke-TaskAdb @('shell','am','broadcast','-n',$taskReminderComponent,'-a','cn.yuki.learningaggregator.INSTALLATION_CHECK')
    Start-Sleep -Seconds 2
    $taskBootComponent = $taskPackage + '/crc64dd8222226edccaad.MobileBootReceiver'
    $null = Invoke-TaskAdb @('shell','am','broadcast','-n',$taskBootComponent,'-a','android.intent.action.BOOT_COMPLETED')
    Start-Sleep -Seconds 2
    $taskCrashes = Invoke-TaskAdb @('logcat','-b','crash','-d')
    [IO.File]::WriteAllText((Join-Path $taskRunRoot 'crash.log'),$taskCrashes,[Text.UTF8Encoding]::new($false))
    if ($taskCrashes -match 'cn\.yuki\.learningaggregator|UnsatisfiedLinkError') { throw 'Cold receiver startup crashed.' }
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent)
    $taskFinalPage = Capture-TaskPage 'dashboard-after-receivers'
    if ($null -eq $taskFinalPage.SelectSingleNode('//*[@text="学习任务" or @text="聚合平台"]')) { throw 'Dashboard did not recover after cold receiver checks.' }

    $taskStream = [IO.File]::OpenRead($taskApk)
    $taskHasher = [Security.Cryptography.SHA256]::Create()
    try { $taskHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskStream)).Replace('-','').ToLowerInvariant() }
    finally { $taskStream.Dispose(); $taskHasher.Dispose() }
    $taskResult = @{schema=1;outcome='passed';serial=$Serial;avd=$ExpectedAvd;api=35;apkSha256=$taskHash;nativeSyntheticChecks=$taskSynthetic.Checks;restartChecks=$taskRestart.Checks;upgradeChecks=$taskUpgrade.Checks;tasks=2;installation=$true;upgradePreservesData=$true;keystore=$true;dashboardStartup=$true;systemInsets=$true;coldReminderReceiver=$true;bootReceiver=$true;chineseAccessibilityText=$true;officialNetworkAccessed=$false;productionDataAccessed=$false;realAccountAcceptance='pending';visualAcceptance='pending';evidence=$taskRun;finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    [IO.File]::WriteAllText((Join-Path $taskRunRoot 'installation-checks.json'),($taskResult | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $taskRunRoot 'installation-checks.json') -Destination (Join-Path $taskEvidence 'installation-checks.json') -Force
    $taskManifestPath = Join-Path $taskRoot 'artifacts\android\package.json'
    if (Test-Path -LiteralPath $taskManifestPath) {
        $taskManifest = Get-Content -LiteralPath $taskManifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
        $taskManifestApk = [IO.Path]::GetFullPath($taskManifest.package)
        $taskManifestStream = [IO.File]::OpenRead($taskManifestApk)
        $taskManifestHasher = [Security.Cryptography.SHA256]::Create()
        try { $taskManifestHash = [BitConverter]::ToString($taskManifestHasher.ComputeHash($taskManifestStream)).Replace('-','').ToLowerInvariant() }
        finally { $taskManifestStream.Dispose(); $taskManifestHasher.Dispose() }
        if ($taskManifestHash -ne $taskHash) { throw 'The build manifest APK differs from the verified APK.' }
        $taskManifest | Add-Member -NotePropertyName sha256 -NotePropertyValue $taskHash -Force
        $taskManifest | Add-Member -NotePropertyName runtimeTests -NotePropertyValue 'passed' -Force
        $taskManifest | Add-Member -NotePropertyName installationChecks -NotePropertyValue ('verification/' + $taskRun + '/installation-checks.json') -Force
        [IO.File]::WriteAllText($taskManifestPath,($taskManifest | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    }
    Save-TaskProgress $taskResult
} catch {
    Save-TaskProgress @{outcome='failed';stage='installation';message=$_.Exception.Message;run=$taskRun;finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    exit 1
}
