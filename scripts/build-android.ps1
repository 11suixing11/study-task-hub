param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.1.2',
    [ValidateRange(1,2147483647)][int]$VersionCode = 4,
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$OutputEncoding=[Console]::OutputEncoding
Add-Type -AssemblyName System.Security
$taskRoot=Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT=Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_NOLOGO='1'
$env:NUGET_PACKAGES=Join-Path $taskRoot '.tools\nuget\packages'
$env:NUGET_HTTP_CACHE_PATH=Join-Path $taskRoot '.tools\nuget\http-cache'
$env:TEMP=Join-Path $taskRoot '.local\temp'; $env:TMP=$env:TEMP
$taskSdk=if ($AndroidSdkDirectory) { [IO.Path]::GetFullPath($AndroidSdkDirectory) } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$taskJdk=if ($JavaSdkDirectory) { [IO.Path]::GetFullPath($JavaSdkDirectory) } elseif ($env:JAVA_HOME -and (Test-Path -LiteralPath (Join-Path $env:JAVA_HOME 'bin\keytool.exe'))) { $env:JAVA_HOME } else { Join-Path $env:USERPROFILE 'Tools\Java\jdk-21\jdk-21.0.12.1+1' }
$env:JAVA_HOME=$taskJdk
$env:PATH=(Join-Path $taskJdk 'bin')+';'+$env:PATH
$env:ANDROID_USER_HOME=Join-Path $taskRoot '.local\android-build-home'
New-Item -ItemType Directory -Force -Path $env:TEMP,$env:ANDROID_USER_HOME | Out-Null
if (-not (Test-Path -LiteralPath $taskSdk)) { throw 'Android SDK is required.' }
if (-not (Test-Path -LiteralPath (Join-Path $taskJdk 'bin\keytool.exe'))) { throw 'JDK 21 is required.' }
$taskKeyDirectory=Join-Path $taskRoot '.local\android-signing'
New-Item -ItemType Directory -Force -Path $taskKeyDirectory | Out-Null
$taskKey=Join-Path $taskKeyDirectory 'aggregator-release.keystore'
$taskSecretFile=Join-Path $taskKeyDirectory 'signing-password.bin'
$taskKeyAlias='aggregator-release'
if (Test-Path -LiteralPath $taskSecretFile) {
    $taskPlain=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($taskSecretFile), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $taskPassword=[Text.Encoding]::UTF8.GetString($taskPlain); [Array]::Clear($taskPlain,0,$taskPlain.Length)
} else {
    if (Test-Path -LiteralPath $taskKey) { throw 'The release key exists but its protected password is missing.' }
    $taskRandom=New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($taskRandom)
    $taskPassword=[Convert]::ToBase64String($taskRandom); [Array]::Clear($taskRandom,0,$taskRandom.Length)
    [IO.File]::WriteAllBytes($taskSecretFile,[Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($taskPassword),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
}
$env:AGGREGATOR_SIGNING_PASSWORD=$taskPassword
try {
    if (-not (Test-Path -LiteralPath $taskKey)) {
        $ErrorActionPreference='Continue'
        & (Join-Path $taskJdk 'bin\keytool.exe') -genkeypair -keystore $taskKey -alias $taskKeyAlias -keyalg RSA -keysize 3072 -validity 10000 -storepass:env AGGREGATOR_SIGNING_PASSWORD -keypass:env AGGREGATOR_SIGNING_PASSWORD -dname 'CN=Learning Task Aggregator, O=Personal Software, C=CN' -noprompt 2>&1 | Out-File -LiteralPath (Join-Path $taskKeyDirectory 'key-generation.log') -Encoding UTF8
        $taskExitCode=$LASTEXITCODE; $ErrorActionPreference='Stop'
        if($taskExitCode -ne 0){throw 'Android signing key generation failed.'}
    }
    $taskBuildId=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    $taskBuild=Join-Path $taskRoot ('.local\android-release-builds\'+$taskBuildId)
    $taskOutput=Join-Path $taskRoot 'artifacts\android'
    New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
    $ErrorActionPreference='Continue'
    & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') publish (Join-Path $taskRoot 'src\AccessProbe.Android\AccessProbe.Android.csproj') -c Release --artifacts-path $taskBuild --output (Join-Path $taskBuild 'publish') --disable-build-servers --configfile (Join-Path $taskRoot 'NuGet.Config') ('-p:AndroidSdkDirectory='+$taskSdk) ('-p:JavaSdkDirectory='+$taskJdk) -p:NuGetAudit=false -p:AndroidKeyStore=true ('-p:AndroidSigningKeyStore='+$taskKey) ('-p:AndroidSigningKeyAlias='+$taskKeyAlias) -p:AndroidSigningStorePass=env:AGGREGATOR_SIGNING_PASSWORD -p:AndroidSigningKeyPass=env:AGGREGATOR_SIGNING_PASSWORD ('-p:ApplicationDisplayVersion='+$Version) ('-p:ApplicationVersion='+$VersionCode) 2>&1 | Out-File -LiteralPath (Join-Path $taskOutput 'build.log') -Encoding UTF8
    $taskExitCode=$LASTEXITCODE; $ErrorActionPreference='Stop'
    if($taskExitCode -ne 0){throw 'Android APK publish failed.'}
    $taskApk=Get-ChildItem -LiteralPath (Join-Path $taskBuild 'publish') -Filter '*-Signed.apk' -File | Select-Object -First 1
    if(-not $taskApk){throw 'Signed Android APK is missing.'}
    $taskTarget=Join-Path $taskOutput ('聚合平台-'+$Version+'-android.apk')
    Copy-Item -LiteralPath $taskApk.FullName -Destination $taskTarget -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'packaging\android\使用说明.txt') -Destination (Join-Path $taskOutput '使用说明.txt') -Force
    $ErrorActionPreference='Continue'
    & (Join-Path $taskSdk 'build-tools\36.0.0\apksigner.bat') verify --verbose --print-certs $taskTarget 2>&1 | Out-File -LiteralPath (Join-Path $taskOutput 'signature-checks.log') -Encoding UTF8
    $taskExitCode=$LASTEXITCODE; $ErrorActionPreference='Stop'
    if($taskExitCode -ne 0){throw 'Android APK signature verification failed.'}
    $taskStream = [IO.File]::OpenRead($taskTarget)
    $taskHasher = [Security.Cryptography.SHA256]::Create()
    try { $taskHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskStream)).Replace('-','').ToLowerInvariant() }
    finally { $taskStream.Dispose(); $taskHasher.Dispose() }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'package.json'),(@{schema=1;version=$Version;versionCode=$VersionCode;buildId=$taskBuildId;platform='android';minimumAndroid=9;minimumApi=28;targetApi=36;applicationId='cn.yuki.learningaggregator';abis=@('arm64-v8a','armeabi-v7a','x86_64');package=$taskTarget;bytes=(Get-Item -LiteralPath $taskTarget).Length;sha256=$taskHash;signature='verified';realAccountAcceptance='pending';runtimeTests='pending';builtAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
    Write-Output ('Android APK built: '+$taskTarget)
} finally { Remove-Item Env:\AGGREGATOR_SIGNING_PASSWORD -ErrorAction SilentlyContinue; $taskPassword=$null }
