#Requires -Version 5.1
# Removes NothingBuds installed by install.ps1. Keeps %AppData%\NothingBuds\devices.json
# unless -WipeData is passed.
param([switch]$WipeData)
$ErrorActionPreference = 'SilentlyContinue'
Get-Process NothingBuds -ErrorAction SilentlyContinue | Stop-Process -Force
$run = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -LiteralPath $run -Name 'NothingBuds' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NothingBuds.lnk') -Force
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'Programs\NothingBuds')
if ($WipeData) { Remove-Item -Recurse -Force (Join-Path $env:APPDATA 'NothingBuds') }
Write-Host 'NothingBuds uninstalled.'
