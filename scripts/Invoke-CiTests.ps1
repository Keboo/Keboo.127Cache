#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $SolutionPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Keboo.127Cache.slnx'),
    [string] $CoverageDirectory
)

$ErrorActionPreference = 'Stop'
$solution = (Resolve-Path -LiteralPath $SolutionPath).Path
$arguments = @('test', '--solution', $solution, '--configuration', 'Release', '--no-build')

if ($CoverageDirectory) {
    $coveragePath = [IO.Path]::GetFullPath($CoverageDirectory, (Get-Location).Path)
    if (Test-Path -LiteralPath $coveragePath) {
        if (!(Test-Path -LiteralPath $coveragePath -PathType Container) -or
            @(Get-ChildItem -LiteralPath $coveragePath -Force).Count -ne 0) {
            throw "Coverage directory must be empty: $coveragePath"
        }
    } else {
        $null = New-Item -ItemType Directory -Path $coveragePath -Force
    }

    # Per-process TRX records let the coverage guard detect missing or shared reports.
    $arguments += @(
        '--results-directory', $coveragePath,
        '--results-directory-layout', 'per-module',
        '--coverage', '--coverage-output-format', 'cobertura',
        '--coverage-output', (Join-Path 'TestResults' 'coverage.cobertura.xml'),
        '--report-trx', '--report-trx-filename', 'ci-module-{pid}.trx'
    )
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Solution test run failed with exit code $LASTEXITCODE." }

if ($CoverageDirectory) {
    . (Join-Path $PSScriptRoot 'Get-CiCoverageReports.ps1')
    $reports = @(Get-CiCoverageReports -CoverageDirectory $coveragePath)
    Write-Host "Collected $($reports.Count) Cobertura reports from the solution."
    $reports | ForEach-Object { Write-Host "COVERAGE_REPORT=$($_.FullName)" }
    if ($env:GITHUB_OUTPUT) {
        $paths = ($reports | ForEach-Object FullName) -join ';'
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "reports=$paths" -Encoding utf8
    }
}
