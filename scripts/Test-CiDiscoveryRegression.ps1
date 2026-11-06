#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaselineCoverageDirectory,
    [ValidateSet('Duplicate', 'Prefix')]
    [string[]] $Scenario = @('Duplicate', 'Prefix'),
    [string] $DiscoveryScript = (Join-Path $PSScriptRoot 'Test-CiDiscovery.ps1')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'Keboo.127Cache.slnx'
. (Join-Path $PSScriptRoot 'Get-CiCoverageReports.ps1')

function Get-ProjectTargetPath {
    param([string] $Project)
    $output = @(& dotnet msbuild $Project -nologo '-property:Configuration=Release' '-getProperty:TargetPath' 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1) {
        $output | ForEach-Object { Write-Host $_ }
        throw "Could not evaluate the test module for $Project."
    }
    $target = [IO.Path]::GetFullPath($output[0].ToString().Trim())
    if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw "Test module does not exist: $target." }
    return $target
}

function Get-ActualModuleReport {
    param([string] $CoveragePath, [string] $ModulePath)
    $comparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $matches = @(
        foreach ($record in Get-ChildItem -LiteralPath $CoveragePath -Filter 'ci-module-*.trx' -File -Recurse) {
            [xml] $trx = Get-Content -LiteralPath $record.FullName -Raw
            $codeBases = @($trx.SelectNodes("//*[local-name()='TestMethod']/@codeBase"))
            if (@($codeBases | Where-Object { $comparer.Equals($_.Value, $ModulePath) }).Count -gt 0) {
                $report = Join-Path $record.DirectoryName (Join-Path 'TestResults' 'coverage.cobertura.xml')
                if (!(Test-Path -LiteralPath $report -PathType Leaf)) {
                    throw "The actual module record has no fixed-name report: $($record.FullName)."
                }
                $report
            }
        }
    )
    if ($matches.Count -ne 1) {
        throw "Expected one actual report for module $ModulePath; found $($matches.Count)."
    }
    return $matches[0]
}

$baseline = @(Get-CiCoverageReports -CoverageDirectory $BaselineCoverageDirectory)
$id = [Guid]::NewGuid().ToString('N')
$root = Join-Path (Join-Path $repositoryRoot 'TestResults') "ci-discovery-regression-$id"
$solutionCopies = @()
$originalGitHubOutput = $env:GITHUB_OUTPUT

try {
    $env:GITHUB_OUTPUT = $null
    $null = New-Item -ItemType Directory -Path $root
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

public class CiDiscoveryRegression
{
    [Test]
    public async Task AddedProjectIsDiscovered()
    {
        await Assert.That(GetType().Name).IsEqualTo("CiDiscoveryRegression");
    }
}
'@
    $pwsh = (Get-Process -Id $PID).Path
    foreach ($case in $Scenario) {
        $name = if ($case -eq 'Duplicate') { 'Keboo.127Cache.Tests' } else { 'CiDiscoveryProbeAdditional' }
        $projectDirectory = Join-Path $root $case
        $null = New-Item -ItemType Directory -Path $projectDirectory
        $project = Join-Path $projectDirectory "$name.csproj"
        Set-Content -LiteralPath $project -Value $projectSource -Encoding utf8
        Set-Content -LiteralPath (Join-Path $projectDirectory 'Regression.cs') -Value $testSource -Encoding utf8
        $buildOutput = @(& dotnet build $project --configuration Release --nologo --verbosity quiet 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $buildOutput | ForEach-Object { Write-Host $_ }
            throw "Could not build the $case regression project."
        }

        $solutionCopy = Join-Path $repositoryRoot "ci-discovery-regression-$id-$case.slnx"
        if (Test-Path -LiteralPath $solutionCopy) { throw "Solution copy already exists: $solutionCopy." }
        $solutionCopies += $solutionCopy
        Copy-Item -LiteralPath $solution -Destination $solutionCopy
        & dotnet sln $solutionCopy add $project
        if ($LASTEXITCODE -ne 0) { throw "Could not add the $case project to the copied solution." }

        $coverage = Join-Path $root "$case-coverage"
        $driverOutput = @(& $pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-CiTests.ps1') `
            -SolutionPath $solutionCopy -CoverageDirectory $coverage 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $driverOutput | ForEach-Object { Write-Host $_ }
            throw "The $case solution coverage run failed."
        }
        $reports = @(Get-CiCoverageReports -CoverageDirectory $coverage)
        if ($reports.Count -ne ($baseline.Count + 1)) {
            throw "The $case solution generated $($reports.Count) reports; expected $($baseline.Count + 1)."
        }

        $module = Get-ProjectTargetPath $project
        $report = Get-ActualModuleReport -CoveragePath $coverage -ModulePath $module
        Write-Host "REGRESSION_MODULE=$module"
        Write-Host "REGRESSION_REPORT=$report"
        if ($case -eq 'Duplicate') {
            $originalProject = Join-Path $repositoryRoot 'Keboo.127Cache.Tests' 'Keboo.127Cache.Tests.csproj'
            $originalModule = Get-ProjectTargetPath $originalProject
            $originalReport = Get-ActualModuleReport -CoveragePath $coverage -ModulePath $originalModule
            $comparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
            if ($comparer.Equals($module, $originalModule) -or $comparer.Equals($report, $originalReport)) {
                throw 'Same-name projects must have distinct module identities and coverage reports.'
            }
            Write-Host "REGRESSION_MODULE=$originalModule"
            Write-Host "REGRESSION_REPORT=$originalReport"
        }

        $guardOutput = @(& $pwsh -NoProfile -File $DiscoveryScript `
            -SolutionPath $solutionCopy -BaselineCoverageDirectory $coverage 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $guardOutput | ForEach-Object { Write-Host $_ }
            throw "The discovery guard rejected the valid $case solution."
        }
        $guardText = $guardOutput -join "`n"
        foreach ($expected in @(
                'PASS: the solution test command failed on the newly added CI_DISCOVERY_SENTINEL test.',
                'PASS: two added same-name projects ran and each produced a fixed-name report',
                'PASS: changing an actual module record identity is rejected as missing and ambiguous.',
                'PASS: renaming an actual coverage report is rejected.',
                'PASS: losing a module record leaves an unpaired coverage report and is rejected.',
                'PASS: two module records cannot share one coverage report.',
                'PASS: the artifact and summary checks reject missing real coverage inputs.'
            )) {
            if (!$guardText.Contains($expected)) { throw "The $case discovery guard did not verify: $expected" }
        }
        Write-Host "PASS: the discovery guard accepts the actual $case solution and rejects missing reports."
    }
} finally {
    $env:GITHUB_OUTPUT = $originalGitHubOutput
    foreach ($solutionCopy in $solutionCopies) {
        if (Test-Path -LiteralPath $solutionCopy) { Remove-Item -LiteralPath $solutionCopy -Force }
    }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
