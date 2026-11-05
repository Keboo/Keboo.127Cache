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
$baseline = @(Get-ChildItem -LiteralPath $BaselineCoverageDirectory -Filter '*.cobertura.xml' -File -Recurse)
if ($baseline.Count -eq 0) { throw 'The baseline solution run has no coverage reports.' }

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
    $project = Join-Path $root 'CiDiscoveryProbe.csproj'
    $test = Join-Path $root 'Canary.cs'
    @'
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
'@ | Set-Content -LiteralPath $project -Encoding utf8
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
    $testSource.Replace('EXPECTED_NAME', 'not-Canary') | Set-Content -LiteralPath $test -Encoding utf8
    Copy-Item -LiteralPath $solution -Destination $solutionCopy
    & dotnet sln $solutionCopy add $project
    if ($LASTEXITCODE -ne 0) { throw 'Could not add the probe to the copied solution.' }

    $buildOutput = @(& dotnet build $project --configuration Release --nologo --verbosity quiet 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $buildOutput | ForEach-Object { Write-Host $_ }
        throw 'Could not build the discovery probe.'
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

    $testSource.Replace('EXPECTED_NAME', 'Canary') | Set-Content -LiteralPath $test -Encoding utf8
    $buildOutput = @(& dotnet build $project --configuration Release --no-restore --nologo --verbosity quiet 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $buildOutput | ForEach-Object { Write-Host $_ }
        throw 'Could not rebuild the passing discovery probe.'
    }
    $passingCoverage = Join-Path $root 'passing-coverage'
    $passingOutput = @(& $pwsh -NoProfile -File $driver -SolutionPath $solutionCopy `
        -CoverageDirectory $passingCoverage 2>&1)
    $passingExit = $LASTEXITCODE
    $passingText = $passingOutput -join "`n"
    if ($passingExit -ne 0 -or !$passingText.Contains('CiDiscoveryProbe.dll')) {
        $passingOutput | ForEach-Object { Write-Host $_ }
        throw 'The passing discovery probe was not executed successfully.'
    }
    $reports = @(Get-ChildItem -LiteralPath $passingCoverage -Filter '*.cobertura.xml' -File -Recurse)
    if ($reports.Count -ne ($baseline.Count + 1)) {
        throw "Expected one new coverage report beyond $($baseline.Count); found $($reports.Count)."
    }
    Write-Host "PASS: the added passing project ran and produced its own coverage report ($($baseline.Count) -> $($reports.Count))."
} finally {
    $env:GITHUB_OUTPUT = $originalGitHubOutput
    if (Test-Path -LiteralPath $solutionCopy) { Remove-Item -LiteralPath $solutionCopy -Force }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
