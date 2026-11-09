param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$previewRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'design\stageshow'
$previewUrl = 'http://127.0.0.1:4280/?workbench'
$previewLogs = Join-Path $previewRoot 'shots\windows-redesign'
$previewNode = (Get-Command node.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
if (-not (Test-Path -LiteralPath (Join-Path $previewRoot 'node_modules\vite\bin\vite.js'))) {
    throw 'Stageshow dependencies are missing. Run npm ci in design\stageshow first.'
}
function Test-PreviewReady {
    try {
        $response = Invoke-WebRequest -Uri $previewUrl -TimeoutSec 2 -UseBasicParsing
        return $response.StatusCode -eq 200 -and $response.Content.Contains('Stageshow')
    } catch { return $false }
}
if (-not (Test-PreviewReady)) {
    if ($VerifyOnly) { throw 'Stageshow preview is not running.' }
    New-Item -ItemType Directory -Path $previewLogs -Force | Out-Null
    $previewProcess = Start-Process -FilePath $previewNode -ArgumentList @('scripts/dev.mjs', '--host', '127.0.0.1', '--port', '4280', '--strictPort') -WorkingDirectory $previewRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $previewLogs 'launcher.log') -RedirectStandardError (Join-Path $previewLogs 'launcher-error.log')
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if (Test-PreviewReady) { break }
        $previewProcess.Refresh()
        if ($previewProcess.HasExited) { throw 'Stageshow could not start. See shots\windows-redesign\launcher-error.log.' }
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-PreviewReady)) { throw 'Stageshow did not become ready. See shots\windows-redesign\launcher-error.log.' }
}
Write-Output ('Stageshow preview: ' + $previewUrl)
Write-Output 'Open this address in your browser. All preview tasks are synthetic.'
