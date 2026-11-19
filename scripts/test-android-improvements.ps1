param([string]$Serial = 'emulator-5560', [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
$taskAdb = Join-Path $taskSdk 'platform-tools\adb.exe'
$taskPackage = 'cn.yuki.learningaggregator'
$taskComponent = $taskPackage + '/crc64dd8222226edccaad.MainActivity'
$taskArtifacts = if ($ArtifactDirectory) { [IO.Path]::GetFullPath($ArtifactDirectory) } else { Join-Path $taskRoot 'artifacts\android-preview\20261111' }
$taskEvidence = Join-Path $taskArtifacts ('verification\improvements-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$taskFixtures = Join-Path $taskRoot '.local\roadmap-checks\routing-fixtures'
$taskUiRoot = '/data/user/0/' + $taskPackage + '/files/synthetic-improvement-ui-test'
$taskReports = @()

function Invoke-TaskAdb([string[]]$Arguments) {
    $taskLines = @(& $taskAdb -s $Serial @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ('ADB verification command failed: ' + ($Arguments | Select-Object -First 3) -join ' ') }
    return ($taskLines | ForEach-Object { $_.ToString() }) -join "`n"
}
function Read-TaskReport([string]$Remote, [string]$Name) {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $taskRaw = @(& $taskAdb -s $Serial shell cat $Remote 2>$null) -join ''
        if ($LASTEXITCODE -eq 0 -and $taskRaw.StartsWith('{')) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    if (-not $taskRaw.StartsWith('{')) { throw ('Native report timed out: ' + $Name) }
    [IO.File]::WriteAllText((Join-Path $taskEvidence ($Name + '.json')), $taskRaw, [Text.UTF8Encoding]::new($false))
    $taskValue = $taskRaw | ConvertFrom-Json
    if ($taskValue.outcome -ne 'passed' -or $taskValue.officialNetworkAccessed -or $taskValue.productionDataAccessed) { throw ('Native checks failed: ' + $Name + ' ' + $taskValue.error) }
    return $taskValue
}
function Run-TaskUi([string]$Mode) {
    $null = Invoke-TaskAdb @('shell','am','force-stop',$taskPackage)
    $null = Invoke-TaskAdb @('shell','rm','-f',($taskUiRoot + '/ui-checks-' + $Mode + '.json'))
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent,'--es','synthetic_improvement_ui_test',$Mode)
    return Read-TaskReport ($taskUiRoot + '/ui-checks-' + $Mode + '.json') ('native-' + $Mode)
}
function Capture-TaskPage([string]$Name) {
    $taskRemote = '/sdcard/synthetic-improvement-' + $Name
    $null = Invoke-TaskAdb @('shell','uiautomator','dump',($taskRemote + '.xml'))
    $null = Invoke-TaskAdb @('pull',($taskRemote + '.xml'),(Join-Path $taskEvidence ($Name + '.xml')))
    $null = Invoke-TaskAdb @('shell','screencap','-p',($taskRemote + '.png'))
    $null = Invoke-TaskAdb @('pull',($taskRemote + '.png'),(Join-Path $taskEvidence ($Name + '.png')))
}
function Remove-TaskFixture([string]$Package, [string]$Label) {
    $taskListed = Invoke-TaskAdb @('shell','pm','list','packages',$Package)
    if ($taskListed.Trim() -eq ('package:' + $Package)) {
        # These IDs only belong to our generated fixtures on this dedicated AVD.
        $taskManifest = Join-Path $taskFixtures $(if ($Package -eq 'com.chaoxing.mobile') { 'app_task\AndroidManifest.xml' } else { 'browser\AndroidManifest.xml' })
        if (-not (Get-Content -LiteralPath $taskManifest -Raw -Encoding UTF8).Contains($Label)) { throw 'Fixture source identity is unconfirmed.' }
        $null = Invoke-TaskAdb @('uninstall',$Package)
    }
}
try {
    if ($Serial -notmatch '^emulator-[0-9]+$') { throw 'Only the dedicated emulator is permitted.' }
    if ((Invoke-TaskAdb @('shell','getprop','ro.kernel.qemu')).Trim() -ne '1') { throw 'Not an emulator.' }
    $taskAvd = (Invoke-TaskAdb @('emu','avd','name')) -split "`n" | Select-Object -First 1
    if ($taskAvd.Trim() -ne 'aggregator-install-test') { throw 'Not the dedicated installation-test AVD.' }
    New-Item -ItemType Directory -Force -Path $taskEvidence | Out-Null
    $null = Invoke-TaskAdb @('shell','svc','wifi','disable')
    $null = Invoke-TaskAdb @('shell','svc','data','disable')
    $null = Invoke-TaskAdb @('root')
    $null = Invoke-TaskAdb @('wait-for-device')
    $taskManifest = Get-Content -LiteralPath (Join-Path $taskArtifacts 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskApk = Join-Path $taskArtifacts ('聚合平台-' + $taskManifest.version + '-android.apk')
    $taskApkHash = (Get-FileHash -LiteralPath $taskApk -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($taskApkHash -ne $taskManifest.sha256) { throw 'The APK differs from its build manifest.' }
    $taskInstalledApk = (Invoke-TaskAdb @('shell','pm','path',$taskPackage)).Trim() -replace '^package:',''
    if ($taskInstalledApk -notmatch '^/data/app/[a-zA-Z0-9_/+~.=-]+/base\.apk$') { throw 'Installed APK path is unrecognized.' }
    $taskInstalledHash = (Invoke-TaskAdb @('shell','sha256sum',$taskInstalledApk)).Split(' ')[0].Trim()
    if ($taskInstalledHash -ne $taskApkHash) { throw 'The installed APK differs from the artifact under test.' }
    $null = Invoke-TaskAdb @('logcat','-b','crash','-c')
    $taskReports += Run-TaskUi 'ui'
    Capture-TaskPage 'task-detail'

    $null = Invoke-TaskAdb @('shell','am','force-stop',$taskPackage)
    $taskLoginRoot = '/data/user/0/' + $taskPackage + '/files/synthetic-login-test'
    $null = Invoke-TaskAdb @('shell','rm','-f',($taskLoginRoot + '/login-checks.json'))
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent,'--ez','synthetic_login_test','true')
    $taskReports += Read-TaskReport ($taskLoginRoot + '/login-checks.json') 'in-app-login'
    Capture-TaskPage 'in-app-login-result'

    $null = Invoke-TaskAdb @('shell','am','force-stop',$taskPackage)
    $taskQrRoot = '/data/user/0/' + $taskPackage + '/files/synthetic-qr-test'
    $null = Invoke-TaskAdb @('shell','rm','-f',($taskQrRoot + '/qr-checks.json'))
    $null = Invoke-TaskAdb @('shell','am','start','-W','-n',$taskComponent,'--es','synthetic_qr_test','automatic')
    $taskReports += Read-TaskReport ($taskQrRoot + '/qr-checks.json') 'official-qr'

    foreach ($taskMode in @('app_task','app_home')) {
        $taskFixtureApk = Join-Path $taskFixtures ($taskMode + '\routing-fixture.apk')
        if (-not (Test-Path -LiteralPath $taskFixtureApk)) { throw 'Build routing fixtures before verification.' }
        $null = Invoke-TaskAdb @('install','--no-streaming','-r',$taskFixtureApk)
        $taskReports += Run-TaskUi $taskMode
    }
    Remove-TaskFixture 'com.chaoxing.mobile' '隔离任务路由测试'
    $null = Invoke-TaskAdb @('install','--no-streaming','-r',(Join-Path $taskFixtures 'browser\routing-fixture.apk'))
    $taskReports += Run-TaskUi 'web'
    Remove-TaskFixture 'cn.yuki.syntheticbrowser' '隔离网页路由测试'
    $taskReports += Run-TaskUi 'unavailable'
    $taskCrashes = Invoke-TaskAdb @('logcat','-b','crash','-d')
    [IO.File]::WriteAllText((Join-Path $taskEvidence 'crash.log'),$taskCrashes,[Text.UTF8Encoding]::new($false))
    if ($taskCrashes -match 'cn\.yuki\.learningaggregator|UnsatisfiedLinkError') { throw 'Android native checks produced a crash.' }
    $taskSummary = [ordered]@{ schema=1;outcome='passed';apkSha256=$taskApkHash;api=35;avd='aggregator-install-test';checks=[int]($taskReports | ForEach-Object {$_.checks} | Measure-Object -Sum).Sum;reports=$taskReports;officialNetworkAccessed=$false;productionDataAccessed=$false;realAccountAcceptance='pending';visualAcceptance='pending';evidence=$taskEvidence;finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    [IO.File]::WriteAllText((Join-Path $taskEvidence 'android-improvements.json'),($taskSummary | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Write-Output ('Android improvement verification completed: ' + $taskEvidence)
} catch {
    if (Test-Path -LiteralPath $taskEvidence) { [IO.File]::WriteAllText((Join-Path $taskEvidence 'failure.json'),(@{outcome='failed';message=$_.Exception.Message}|ConvertTo-Json),[Text.UTF8Encoding]::new($false)) }
    throw
}
