# Builds the HTML coverage report from the coverage files of the last test run.
# `dotnet test tests/ContactManage.Tests` calls this automatically when the tests pass. When a test fails,
# MSBuild skips that step, so run this script by hand (or let CI do it) to see the report anyway.
#
#   powershell -File scripts/coverage-report.ps1
#
# Output: coverage-report/index.html (open this), plus Summary.txt and SummaryGithub.md next to it.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$results = Join-Path $root 'TestResults'
if (-not (Get-ChildItem -Path $results -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue)) {
    Write-Host "No coverage files in $results. Run: dotnet test tests/ContactManage.Tests"
    exit 1
}

dotnet tool restore | Out-Null
dotnet tool run reportgenerator -- `
    "-reports:$results\**\coverage.cobertura.xml" `
    "-targetdir:$root\coverage-report" `
    "-historydir:$root\coverage-history" `
    "-reporttypes:Html;HtmlSummary;MarkdownSummaryGithub;TextSummary" `
    "-assemblyfilters:+ContactManage.*;-ContactManage.Tests;-ContactManage.UiTests" `
    "-title:ContactManage" `
    -verbosity:Warning
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Coverage report: $root\coverage-report\index.html"
