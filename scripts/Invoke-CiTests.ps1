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

    # The coverage extension generates a distinct filename for each test module.
    $arguments += @('--results-directory', $coveragePath, '--coverage', '--coverage-output-format', 'cobertura')
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Solution test run failed with exit code $LASTEXITCODE." }

if ($CoverageDirectory) {
    $reports = @(Get-ChildItem -LiteralPath $coveragePath -Filter '*.cobertura.xml' -File -Recurse |
        Sort-Object -Property FullName)
    if ($reports.Count -eq 0 -or @($reports | Where-Object Length -eq 0).Count -ne 0) {
        throw "The solution test run did not produce nonempty Cobertura reports in $coveragePath."
    }
    Write-Host "Collected $($reports.Count) Cobertura reports from the solution."
    if ($env:GITHUB_OUTPUT) {
        $paths = ($reports | ForEach-Object FullName) -join ';'
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "reports=$paths" -Encoding utf8
    }
}
