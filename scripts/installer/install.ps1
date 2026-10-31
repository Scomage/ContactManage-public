# Installs ContactManage for the current user only. No administrator rights needed.
# Copies the app to %LOCALAPPDATA%\Programs\ContactManage, adds a Start menu shortcut and an entry in Settings > Apps.
# Your contacts, backups and logs (%LOCALAPPDATA%\ContactManage) are never touched by install or update.
$ErrorActionPreference = "Stop"

$source = Join-Path $PSScriptRoot "app"
if (-not (Test-Path (Join-Path $source "ContactManage.App.exe"))) { throw "Run this from the unzipped ContactManage folder (the 'app' folder is missing)." }

$version = (Get-Content (Join-Path $PSScriptRoot "version.txt") -ErrorAction SilentlyContinue | Select-Object -First 1)
$target = Join-Path $env:LOCALAPPDATA "Programs\ContactManage"
$exe = Join-Path $target "ContactManage.App.exe"

Get-Process ContactManage.App -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "ContactManage is running; closing it to update."
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(5000)) { $_.Kill() }
}

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $source "*") $target -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $target -Force

$menu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $menu "ContactManage.lnk"))
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.Description = "Merge and de-duplicate contact folders"
$link.Save()

$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContactManage"
New-Item $key -Force | Out-Null
Set-ItemProperty $key DisplayName "ContactManage"
Set-ItemProperty $key DisplayVersion $version
Set-ItemProperty $key Publisher "Scomage"
Set-ItemProperty $key InstallLocation $target
Set-ItemProperty $key DisplayIcon $exe
Set-ItemProperty $key UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$target\uninstall.ps1`""
Set-ItemProperty $key NoModify 1 -Type DWord
Set-ItemProperty $key NoRepair 1 -Type DWord

Write-Host "Installed ContactManage $version to $target"
Write-Host "Start it from the Start menu (type ContactManage)."
