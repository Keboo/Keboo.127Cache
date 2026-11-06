#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaselineCoverageDirectory,
    [string] $SolutionPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Keboo.127Cache.slnx')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = (Resolve-Path -LiteralPath $SolutionPath).Path
. (Join-Path $PSScriptRoot 'Get-CiCoverageReports.ps1')

function Assert-ProjectReport {
    param([string] $CoveragePath, [System.IO.FileInfo[]] $Reports, [string] $Project)
    $matches = @($Reports | Where-Object {
        $relative = [IO.Path]::GetRelativePath($CoveragePath, $_.FullName)
        $name = $relative.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))[0]
        $name -ceq $Project
    })
    if ($matches.Count -eq 0) { throw "No fixed-name report was generated for $Project." }
}

function Assert-CoveragePublication {
    param([string] $CoveragePath, [string] $ReportPath, [System.IO.FileInfo[]] $Reports)
    $inputDirectory = Join-Path $ReportPath 'inputs'
    $summary = Get-Content -LiteralPath (Join-Path $ReportPath 'SummaryGithub.md') -Raw
    $published = @(Get-ChildItem -LiteralPath $inputDirectory -Filter 'coverage.cobertura.xml' -File -Recurse)
    if ($published.Count -ne $Reports.Count) {
        throw "The artifact contains $($published.Count) coverage inputs, expected $($Reports.Count)."
    }
    foreach ($report in $Reports) {
        $relative = [IO.Path]::GetRelativePath($CoveragePath, $report.FullName)
        $copy = Join-Path $inputDirectory $relative
        $display = 'inputs/' + $relative.Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
        if (!(Test-Path -LiteralPath $copy -PathType Leaf) -or
            (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $report.FullName -Algorithm SHA256).Hash) {
            throw "The artifact is missing or changed coverage input: $display."
        }
        if (!$summary.Contains($display)) { throw "The coverage summary is missing input: $display." }
    }
}

$baselinePath = (Resolve-Path -LiteralPath $BaselineCoverageDirectory).Path
$baseline = @(Get-CiCoverageReports -CoverageDirectory $baselinePath)
foreach ($project in @('Keboo.127Cache.Tests', 'Keboo.127Cache.Proxy.Tests')) {
    Assert-ProjectReport -CoveragePath $baselinePath -Reports $baseline -Project $project
}

$id = [Guid]::NewGuid().ToString('N')
$root = Join-Path (Join-Path $repositoryRoot 'TestResults') "ci-discovery-$id"
$solutionCopy = Join-Path (Split-Path -Parent $solution) "ci-discovery-$id.slnx"
if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $solutionCopy)) {
    throw 'The discovery-test paths already exist.'
}

$originalGitHubOutput = $env:GITHUB_OUTPUT
try {
    $env:GITHUB_OUTPUT = $null
    $null = New-Item -ItemType Directory -Path $root -Force
    $projectSource = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" />
    <PackageReference Include="TUnit" />
  </ItemGroup>
</Project>
'@
    $testSource = @'
using TUnit.Assertions;
using TUnit.Core;

namespace CiDiscoveryProbeFixture;

public class Canary
{
    [Test]
    public async Task CI_DISCOVERY_SENTINEL()
    {
        var actual = GetType().Name;
        await Assert.That(actual).IsEqualTo("EXPECTED_NAME");
    }
}
'@
    $projects = @()
    foreach ($name in @('first', 'second')) {
        $projectDirectory = Join-Path $root $name
        $null = New-Item -ItemType Directory -Path $projectDirectory -Force
        $project = Join-Path $projectDirectory 'CiDiscoveryProbe.csproj'
        $test = Join-Path $projectDirectory 'Canary.cs'
        Set-Content -LiteralPath $project -Value $projectSource -Encoding utf8
        $expected = if ($name -eq 'first') { 'not-Canary' } else { 'Canary' }
        Set-Content -LiteralPath $test -Value $testSource.Replace('EXPECTED_NAME', $expected) -Encoding utf8
        $projects += $project
    }
    Copy-Item -LiteralPath $solution -Destination $solutionCopy
    foreach ($project in $projects) {
        & dotnet sln $solutionCopy add $project
        if ($LASTEXITCODE -ne 0) { throw 'Could not add a probe to the copied solution.' }
        $buildOutput = @(& dotnet build $project --configuration Release --nologo --verbosity quiet 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $buildOutput | ForEach-Object { Write-Host $_ }
            throw 'Could not build a discovery probe.'
        }
    }

    $pwsh = (Get-Process -Id $PID).Path
    $driver = Join-Path $PSScriptRoot 'Invoke-CiTests.ps1'
    $failureOutput = @(& $pwsh -NoProfile -File $driver -SolutionPath $solutionCopy `
        -CoverageDirectory (Join-Path $root 'failing-coverage') 2>&1)
    $failureExit = $LASTEXITCODE
    $failureText = $failureOutput -join "`n"
    if ($failureExit -eq 0 -or !$failureText.Contains('CI_DISCOVERY_SENTINEL')) {
        $failureOutput | ForEach-Object { Write-Host $_ }
        throw 'A newly added failing test project was not reported by the CI solution test command.'
    }
    Write-Host 'PASS: the solution test command failed on the newly added CI_DISCOVERY_SENTINEL test.'

    $test = Join-Path (Split-Path -Parent $projects[0]) 'Canary.cs'
    $testSource.Replace('EXPECTED_NAME', 'Canary') | Set-Content -LiteralPath $test -Encoding utf8
    $buildOutput = @(& dotnet build $projects[0] --configuration Release --no-restore --nologo --verbosity quiet 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $buildOutput | ForEach-Object { Write-Host $_ }
        throw 'Could not rebuild the passing discovery probe.'
    }
    $passingCoverage = Join-Path $root 'passing-coverage'
    $outputFile = Join-Path $root 'coverage-output.txt'
    $null = New-Item -ItemType File -Path $outputFile
    $env:GITHUB_OUTPUT = $outputFile
    $passingOutput = @(& $pwsh -NoProfile -File $driver -SolutionPath $solutionCopy `
        -CoverageDirectory $passingCoverage 2>&1)
    $passingExit = $LASTEXITCODE
    $env:GITHUB_OUTPUT = $null
    $passingText = $passingOutput -join "`n"
    if ($passingExit -ne 0) {
        $passingOutput | ForEach-Object { Write-Host $_ }
        throw 'The passing discovery probes were not executed successfully.'
    }
    foreach ($project in $projects) {
        $assembly = Join-Path -Path (Split-Path -Parent $project) -ChildPath 'bin' `
            -AdditionalChildPath 'Release', 'net10.0', 'CiDiscoveryProbe.dll'
        if (!$passingText.Contains($assembly)) { throw "The test module did not run: $assembly." }
    }
    $reports = @(Get-CiCoverageReports -CoverageDirectory $passingCoverage)
    if ($reports.Count -ne ($baseline.Count + 2)) {
        throw "Expected two new coverage reports beyond $($baseline.Count); found $($reports.Count)."
    }
    foreach ($project in @('Keboo.127Cache.Tests', 'Keboo.127Cache.Proxy.Tests')) {
        Assert-ProjectReport -CoveragePath $passingCoverage -Reports $reports -Project $project
    }
    $probeReports = @($reports | Where-Object {
        $relative = [IO.Path]::GetRelativePath($passingCoverage, $_.FullName)
        $name = $relative.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))[0]
        $name.StartsWith('CiDiscoveryProbe', [StringComparison]::Ordinal)
    })
    if ($probeReports.Count -ne 2) {
        throw 'Two added projects with the same name must each have an isolated coverage report.'
    }
    $outputLines = @(Get-Content -LiteralPath $outputFile | Where-Object { $_ -clike 'reports=*' })
    $expectedOutput = 'reports=' + (($reports | ForEach-Object FullName) -join ';')
    if ($outputLines.Count -ne 1 -or $outputLines[0] -cne $expectedOutput) {
        throw 'The ReportGenerator output does not list exactly the independently generated coverage reports.'
    }
    Write-Host "PASS: two added same-name projects ran and each produced a fixed-name report ($($baseline.Count) -> $($reports.Count))."

    $missingReport = $probeReports[0]
    $renamedReport = Join-Path $missingReport.DirectoryName 'wrong.cobertura.xml'
    Move-Item -LiteralPath $missingReport.FullName -Destination $renamedReport
    $rejected = $false
    try {
        $null = @(Get-CiCoverageReports -CoverageDirectory $passingCoverage)
    } catch {
        if ($_.Exception.Message -notlike '*Missing fixed-name Cobertura report*') { throw }
        $rejected = $true
    } finally {
        Move-Item -LiteralPath $renamedReport -Destination $missingReport.FullName
    }
    if (!$rejected) { throw 'A renamed fixed-name coverage report was accepted.' }
    Write-Host 'PASS: renaming an actual coverage report is rejected.'

    $record = @(Get-ChildItem -LiteralPath $missingReport.Directory.Parent.FullName -Filter 'ci-module-*.trx' -File)[0]
    $renamedRecord = $record.FullName + '.missing'
    Move-Item -LiteralPath $record.FullName -Destination $renamedRecord
    $rejected = $false
    try {
        $null = @(Get-CiCoverageReports -CoverageDirectory $passingCoverage)
    } catch {
        if ($_.Exception.Message -notlike '*no corresponding test module record*') { throw }
        $rejected = $true
    } finally {
        Move-Item -LiteralPath $renamedRecord -Destination $record.FullName
    }
    if (!$rejected) { throw 'A coverage report with a missing test module record was accepted.' }
    Write-Host 'PASS: losing a module record leaves an unpaired coverage report and is rejected.'

    $publication = Join-Path $root 'published-report'
    $null = New-Item -ItemType Directory -Path $publication
    Set-Content -LiteralPath (Join-Path $publication 'SummaryGithub.md') -Value '# Summary' -Encoding utf8
    & (Join-Path $PSScriptRoot 'Publish-CiCoverageInputs.ps1') `
        -CoverageDirectory $passingCoverage -ReportDirectory $publication
    Assert-CoveragePublication -CoveragePath $passingCoverage -ReportPath $publication -Reports $reports

    $relative = [IO.Path]::GetRelativePath($passingCoverage, $missingReport.FullName)
    $copy = Join-Path (Join-Path $publication 'inputs') $relative
    Remove-Item -LiteralPath $copy
    $rejected = $false
    try {
        Assert-CoveragePublication -CoveragePath $passingCoverage -ReportPath $publication -Reports $reports
    } catch {
        if ($_.Exception.Message -notlike '*artifact contains*coverage inputs*') { throw }
        $rejected = $true
    }
    if (!$rejected) { throw 'A missing input in the coverage artifact was accepted.' }
    Copy-Item -LiteralPath $missingReport.FullName -Destination $copy

    $summaryPath = Join-Path $publication 'SummaryGithub.md'
    $summary = Get-Content -LiteralPath $summaryPath -Raw
    $display = 'inputs/' + $relative.Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
    $mutatedSummary = $summary.Replace($display, 'omitted-input')
    if ($mutatedSummary -ceq $summary) { throw 'Could not mutate the coverage summary.' }
    Set-Content -LiteralPath $summaryPath -Value $mutatedSummary -Encoding utf8
    $rejected = $false
    try {
        Assert-CoveragePublication -CoveragePath $passingCoverage -ReportPath $publication -Reports $reports
    } catch {
        if ($_.Exception.Message -notlike '*coverage summary is missing input*') { throw }
        $rejected = $true
    }
    if (!$rejected) { throw 'A missing input in the coverage summary was accepted.' }
    Write-Host 'PASS: the artifact and summary checks reject missing real coverage inputs.'

    $driverSource = Get-Content -LiteralPath $driver -Raw
    $isolatedLayout = "'--results-directory-layout', 'per-module'"
    $flatLayout = "'--results-directory-layout', 'flat'"
    $mutatedDriver = $driverSource.Replace($isolatedLayout, $flatLayout)
    if ($mutatedDriver -ceq $driverSource) { throw 'Could not mutate the actual coverage layout.' }
    $mutatedDirectory = Join-Path $root 'mutated-driver'
    $null = New-Item -ItemType Directory -Path $mutatedDirectory
    $mutatedPath = Join-Path $mutatedDirectory 'Invoke-CiTests.ps1'
    Set-Content -LiteralPath $mutatedPath -Value $mutatedDriver -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Get-CiCoverageReports.ps1') -Destination $mutatedDirectory
    $flatOutput = @(& $pwsh -NoProfile -File $mutatedPath -SolutionPath $solution `
        -CoverageDirectory (Join-Path $root 'flat-coverage') 2>&1)
    $flatExit = $LASTEXITCODE
    if ($flatExit -eq 0 -or !($flatOutput -join "`n").Contains('Expected a nonempty per-module test record')) {
        $flatOutput | ForEach-Object { Write-Host $_ }
        throw 'The coverage guard did not reject the actual driver with a conflating flat layout.'
    }
    Write-Host 'PASS: a mutation of the actual driver to a conflating flat layout is rejected.'
} finally {
    $env:GITHUB_OUTPUT = $originalGitHubOutput
    if (Test-Path -LiteralPath $solutionCopy) { Remove-Item -LiteralPath $solutionCopy -Force }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
