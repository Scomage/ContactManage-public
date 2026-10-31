# Removes the program, its shortcut and its Apps entry. Your contacts data, backups and logs stay in
# %LOCALAPPDATA%\ContactManage; delete that folder yourself if you want them gone too.
$ErrorActionPreference = "Stop"

Get-Process ContactManage.App -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Remove-Item (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ContactManage.lnk") -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContactManage" -Recurse -ErrorAction SilentlyContinue

# This script lives inside the install folder, so a copy in %TEMP% finishes the delete after this process ends.
$target = $PSScriptRoot
$cleanup = Join-Path $env:TEMP "contactmanage-cleanup.cmd"
Set-Content $cleanup "@echo off`r`nping -n 3 127.0.0.1 >nul`r`nrmdir /s /q `"$target`"`r`ndel `"%~f0`"" -Encoding ascii
Start-Process cmd.exe -ArgumentList "/c `"$cleanup`"" -WindowStyle Hidden
Write-Host "ContactManage was removed."
