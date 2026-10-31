<#
  Builds the installable package: dist\ContactManage-<version>-win-x64.zip
  The zip holds the self-contained app (no .NET needed on the target PC) plus Install.cmd / install.ps1 / uninstall.ps1.
  A copy named ContactManage-win-x64.zip (no version) is written next to it; the release workflow attaches both.
  Run from anywhere:  powershell -File scripts\package.ps1 [-Version 1.0.0]
#>
param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "stage"
$zip = Join-Path $dist "ContactManage-$Version-win-x64.zip"

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "app") | Out-Null

dotnet publish (Join-Path $root "src\ContactManage.App") -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $stage "app")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $PSScriptRoot "installer\*") $stage
Set-Content (Join-Path $stage "version.txt") $Version -Encoding ascii
Copy-Item (Join-Path $root "docs\QUICKSTART.md") (Join-Path $stage "README-first.md")
# Same page as a PDF, easier to read; rebuild docs\README-first.pdf when QUICKSTART.md changes.
$pdf = Join-Path $root "docs\README-first.pdf"
if (Test-Path $pdf) { Copy-Item $pdf $stage }

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

# Same file under a name without the version, so .../releases/latest/download/ContactManage-win-x64.zip never changes.
Copy-Item $zip (Join-Path $dist "ContactManage-win-x64.zip")
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
