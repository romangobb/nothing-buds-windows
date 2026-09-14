#Requires -Version 5.1
<#
  Installs NothingBuds (unofficial local companion for Nothing/CMF earbuds).
  - Copies publish/ output to %LocalAppData%\Programs\NothingBuds
  - Creates a Start Menu shortcut
  - Optionally enables start-with-Windows (tray) and launches the app
  Usage: powershell -ExecutionPolicy Bypass -File install.ps1 [-Autostart] [-NoLaunch]
#>
param([switch]$Autostart, [switch]$NoLaunch)

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'publish'
$dst = Join-Path $env:LOCALAPPDATA 'Programs\NothingBuds'

if (-not (Test-Path (Join-Path $src 'NothingBuds.exe'))) {
  throw "publish output not found in $src. Run dotnet publish first (see README)."
}

Get-Process NothingBuds -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item (Join-Path $src '*') $dst -Force
Write-Host "Copied to $dst"

$shell = New-Object -ComObject WScript.Shell
$menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$lnk = $shell.CreateShortcut((Join-Path $menu 'NothingBuds.lnk'))
$lnk.TargetPath = Join-Path $dst 'NothingBuds.exe'
$lnk.WorkingDirectory = $dst
$lnk.Description = 'NothingBuds - unofficial local companion for Nothing/CMF earbuds'
$lnk.Save()
Write-Host 'Start Menu shortcut created.'

if ($Autostart) {
  $run = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
  Set-ItemProperty -LiteralPath $run -Name 'NothingBuds' -Value ('"' + (Join-Path $dst 'NothingBuds.exe') + '" --minimized')
  Write-Host 'Start-with-Windows enabled.'
}

if (-not $NoLaunch) {
  Start-Process -FilePath (Join-Path $dst 'NothingBuds.exe')
  Write-Host 'Launched. Look for the tray icon (bottom-right); click it to open the window.'
}
