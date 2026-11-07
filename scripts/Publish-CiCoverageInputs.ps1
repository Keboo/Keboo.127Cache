#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CoverageDirectory,
    [Parameter(Mandatory)]
    [string] $ReportDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Get-CiCoverageReports.ps1')

$coveragePath = (Resolve-Path -LiteralPath $CoverageDirectory).Path
$reportPath = (Resolve-Path -LiteralPath $ReportDirectory).Path
$summary = Join-Path $reportPath 'SummaryGithub.md'
if (!(Test-Path -LiteralPath $summary -PathType Leaf)) {
    throw "ReportGenerator summary not found: $summary."
}

$reports = @(Get-CiCoverageReports -CoverageDirectory $coveragePath)
$inputDirectory = Join-Path $reportPath 'inputs'
if (Test-Path -LiteralPath $inputDirectory) {
    throw "Coverage input directory already exists: $inputDirectory."
}

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('')
$lines.Add('## Cobertura inputs')
$lines.Add('')
$lines.Add('The CodeCoverage artifact contains these independently generated reports:')
$lines.Add('')
foreach ($report in $reports) {
    $relative = [IO.Path]::GetRelativePath($coveragePath, $report.FullName)
    $destination = Join-Path $inputDirectory $relative
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    Copy-Item -LiteralPath $report.FullName -Destination $destination
    if ((Get-FileHash -LiteralPath $report.FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
        throw "Copied coverage input differs from its source: $destination."
    }

    $display = 'inputs/' + $relative.Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
    if ($display.Contains("`n") -or $display.Contains("`r")) {
        throw "Coverage input path cannot be represented in the summary: $relative."
    }
    $fence = '`'
    while ($display.Contains($fence)) { $fence += '`' }
    $lines.Add("- $fence $display $fence")
    Write-Host "COVERAGE_ARTIFACT=$display"
}
Add-Content -LiteralPath $summary -Value $lines -Encoding utf8
