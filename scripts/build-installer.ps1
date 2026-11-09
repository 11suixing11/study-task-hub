param([string]$ReleaseVersion, [string]$PackageVersion = '1.1.1')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'artifacts\current.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $ReleaseVersion) { $ReleaseVersion = $taskManifest.version }
if ($ReleaseVersion -notmatch '^\d{8}-\d{6}-[a-f0-9]{8}$') { throw 'Release version is invalid.' }
if ($PackageVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Package version is invalid.' }
$taskRelease = Join-Path $taskRoot ('artifacts\releases\' + $ReleaseVersion)
$taskVerification = Get-Content -LiteralPath (Join-Path $taskRelease 'verification\verification.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskVerification.outcome -ne 'passed' -or $taskVerification.version -ne $ReleaseVersion) { throw 'A verified release is required.' }
$env:DOTNET_ROOT = Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_NOLOGO='1'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $taskRoot '.tools\nuget\http-cache'
$env:TEMP=Join-Path $taskRoot '.local\temp'; $env:TMP=$env:TEMP
$taskOutput = Join-Path $taskRoot ('artifacts\installers\' + $ReleaseVersion)
$taskWork = Join-Path $taskRoot ('.local\installer-builds\' + $ReleaseVersion)
$taskPayload = Join-Path $taskWork 'payload'
New-Item -ItemType Directory -Force -Path $taskOutput,$taskWork | Out-Null
$ErrorActionPreference = 'Continue'
& (Join-Path $env:DOTNET_ROOT 'dotnet.exe') publish (Join-Path $taskRoot 'src\AccessProbe\AccessProbe.csproj') --configuration Release --runtime win-x64 --self-contained true --output $taskPayload --artifacts-path (Join-Path $taskWork 'build') --disable-build-servers --configfile (Join-Path $taskRoot 'NuGet.Config') -p:NuGetAudit=false -p:PublishTrimmed=false -p:RuntimeFrameworkVersion=10.0.13 ('-p:Version=' + $PackageVersion) -v:minimal 2>&1 | Out-File -LiteralPath (Join-Path $taskOutput 'publish.log') -Encoding UTF8
$taskExitCode = $LASTEXITCODE; $ErrorActionPreference = 'Stop'
if ($taskExitCode -ne 0) { throw 'Self-contained Windows publish failed.' }
[IO.File]::WriteAllText((Join-Path $taskPayload 'installed-app.json'), (@{schema=1;version=$ReleaseVersion;application='LearningTaskAggregator'} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $taskRoot 'packaging\windows\使用说明.txt') -Destination $taskPayload
& python (Join-Path $taskRoot 'scripts\collect-windows-licenses.py') --payload $taskPayload
if ($LASTEXITCODE -ne 0) { throw 'Published dependency license collection failed.' }
$taskCompiler = Join-Path $taskRoot '.tools\packaging\inno-6.7.4\ISCC.exe'
$taskWebView = Join-Path $taskRoot '.tools\packaging\downloads\MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path -LiteralPath $taskCompiler) -or -not (Test-Path -LiteralPath $taskWebView)) { throw 'Inno Setup compiler and Microsoft WebView2 bootstrapper are required.' }
$ErrorActionPreference = 'Continue'
& $taskCompiler ('/DPayloadDirectory=' + $taskPayload) ('/DBuildVersion=' + $ReleaseVersion) ('/DPackageVersion=' + $PackageVersion) ('/DPackageOutput=' + $taskOutput) ('/DWebViewBootstrapper=' + $taskWebView) (Join-Path $taskRoot 'packaging\windows\聚合平台.iss') 2>&1 | Out-File -LiteralPath (Join-Path $taskOutput 'installer-build.log') -Encoding UTF8
$taskExitCode = $LASTEXITCODE; $ErrorActionPreference = 'Stop'
if ($taskExitCode -ne 0) { throw 'Windows installer compiler failed.' }
$taskSetup = Join-Path $taskOutput ('聚合平台-Setup-' + $ReleaseVersion + '-win-x64.exe')
if (-not (Test-Path -LiteralPath $taskSetup)) { throw 'Installer output is missing.' }
$taskResult=@{schema=1;version=$ReleaseVersion;packageVersion=$PackageVersion;platform='windows-x64';selfContained=$true;runtimeVersion='10.0.13';setup=$taskSetup;bytes=(Get-Item -LiteralPath $taskSetup).Length;builtAtUtc=[DateTimeOffset]::UtcNow.ToString('o');verification='pending';productionDataModified=$false}
[IO.File]::WriteAllText((Join-Path $taskOutput 'package.json'), ($taskResult | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Output ('Windows installer built: ' + $taskSetup)
