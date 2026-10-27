$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $root ('.local\recovery-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $testRoot 'src\AccessProbe') | Out-Null
Set-Content (Join-Path $testRoot 'PRD.md') 'Isolated login recovery smoke test.'
Set-Content (Join-Path $testRoot 'src\AccessProbe\AccessProbe.csproj') '<Project />'
$env:DOTNET_ROOT = Join-Path $root '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TEMP = Join-Path $root '.local\temp'
$env:TMP = $env:TEMP
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RecoveryInput {
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr parameter);
}
'@
$dll = Join-Path $root 'src\AccessProbe\bin\Debug\net10.0-windows\AccessProbe.dll'
$process = Start-Process (Join-Path $env:DOTNET_ROOT 'dotnet.exe') -ArgumentList ('"' + $dll + '" --project-root "' + $testRoot + '"') -PassThru -WorkingDirectory $root
function Find-Control($window, $id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Ready {
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw 'Window exited unexpectedly.' }
        if ($process.MainWindowHandle -eq 0) { continue }
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $ready = $true
        foreach ($id in @('LoginButton','RefreshButton','ClearButton','CollectButton')) {
            $control = Find-Control $window $id
            if ($null -eq $control -or -not $control.Current.IsEnabled) { $ready = $false }
        }
        if ($ready) { return $window }
    }
    throw 'Action buttons did not recover within 20 seconds.'
}
try {
    $window = Wait-Ready
    for ($cycle = 1; $cycle -le 2; $cycle++) {
        $button = Find-Control $window 'ClearButton'
        $button.SetFocus()
        $null = [RecoveryInput]::PostMessage($process.MainWindowHandle, 0x100, [IntPtr]32, [IntPtr]0)
        $null = [RecoveryInput]::PostMessage($process.MainWindowHandle, 0x101, [IntPtr]32, [IntPtr]0)
        $confirmed = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            Start-Sleep -Milliseconds 100
            $yes = Find-Control ([System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)) '6'
            if ($null -ne $yes) {
                $handle = [IntPtr]$yes.Current.NativeWindowHandle
                if ($handle -eq [IntPtr]::Zero) { continue }
                $null = [RecoveryInput]::PostMessage($handle, 0x00F5, [IntPtr]0, [IntPtr]0)
                $confirmed = $true
                break
            }
        }
        if (-not $confirmed) { throw 'Clear confirmation dialog not found.' }
        Start-Sleep -Milliseconds 300
        $window = Wait-Ready
        $status = Find-Control $window 'StatusText'
        if ($status.Current.Name -notmatch '\u767b\u5f55\u6570\u636e\u5df2\u6e05\u7406') { throw ('Unexpected status: ' + $status.Current.Name) }
        Write-Output "PASS: clear cycle $cycle completed; all four actions enabled."
    }
    $refresh = Find-Control $window 'RefreshButton'
    $refresh.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    $window = Wait-Ready
    Write-Output 'PASS: refresh after clearing accepted; actions remain available. QR login not tested.'
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 500
        $status = Find-Control $window 'StatusText'
        if ($status.Current.Name -notmatch '\u6b63\u5728') { break }
    }
    Write-Output ('Anonymous page status: ' + (Find-Control $window 'StatusText').Current.Name)
    Write-Output ('Navigation category: ' + (Find-Control $window 'DiagnosticText').Current.Name)
} finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        Write-Output ('Test window closed: ' + $process.WaitForExit(10000))
    }
    Write-Output ('Isolated test profile retained under: ' + $testRoot)
}
