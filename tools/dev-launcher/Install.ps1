param(
    [string]$ProjectRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'FrevoHRMSLauncher')
)

$ErrorActionPreference = 'Stop'
foreach ($relative in @('payroll-ui/package.json', 'Payroll.API/Payroll.API.csproj', 'ess-mss/package.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $relative))) { throw "Project root does not contain $relative" }
}
New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
foreach ($name in @('Launcher.ps1', 'Worker.ps1', 'ProcessHelpers.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $InstallDirectory $name) -Force
}
$settingsFile = Join-Path $InstallDirectory 'settings.json'
if (-not (Test-Path -LiteralPath $settingsFile)) {
    @{
        ui = Join-Path $ProjectRoot 'payroll-ui'
        api = Join-Path $ProjectRoot 'Payroll.API'
        ess = Join-Path $ProjectRoot 'ess-mss'
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsFile -Encoding UTF8
}

# A local icon keeps the shortcut usable even when the project drive is disconnected.
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FrevoIconHandle {
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
}
'@
$bitmap = New-Object System.Drawing.Bitmap(64, 64)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#087bb8'))
$font = New-Object System.Drawing.Font('Segoe UI', 36, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$graphics.DrawString('F', $font, [System.Drawing.Brushes]::White, 16, 7)
$handle = $bitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($handle)
$iconPath = Join-Path $InstallDirectory 'Frevo.ico'
$stream = [System.IO.File]::Create($iconPath)
try { $icon.Save($stream) }
finally {
    $stream.Dispose(); $icon.Dispose()
    [FrevoIconHandle]::DestroyIcon($handle) | Out-Null
    $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Frevo HRMS Launcher.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = "$PSHOME\powershell.exe"
$shortcut.Arguments = '-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $InstallDirectory 'Launcher.ps1') + '"'
$shortcut.WorkingDirectory = $InstallDirectory
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Description = 'Start Payroll UI, API and ESS from one window.'
$shortcut.WindowStyle = 1
$shortcut.Save()
Write-Output "Desktop shortcut created: $shortcutPath"
Write-Output "Launcher installed: $InstallDirectory"
