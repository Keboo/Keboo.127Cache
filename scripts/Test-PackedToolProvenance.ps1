#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ExpectedSha256
)

$ErrorActionPreference = 'Stop'
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File)
if ($packages.Count -ne 1) { throw 'The provenance test requires exactly one package.' }
$originalHash = (Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256).Hash
if ($originalHash -ne $ExpectedSha256) { throw 'The provenance test did not receive the canonical package.' }

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$root = Join-Path (Join-Path $repositoryRoot 'TestResults') "ci-provenance-$([Guid]::NewGuid().ToString('N'))"
if (Test-Path -LiteralPath $root) { throw "Provenance-test directory already exists: $root" }

try {
    $null = New-Item -ItemType Directory -Path $root -Force
    $mutatedPackage = Join-Path $root $packages[0].Name
    Copy-Item -LiteralPath $packages[0].FullName -Destination $mutatedPackage
    $archive = [IO.Compression.ZipFile]::Open($mutatedPackage, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $readme = $archive.GetEntry('README.md')
        if (!$readme) { throw 'The package must contain its README for the provenance mutation.' }
        $stream = $readme.Open()
        try {
            $null = $stream.Seek(0, [IO.SeekOrigin]::End)
            $bytes = [Text.Encoding]::UTF8.GetBytes("`nCI provenance mutation`n")
            $stream.Write($bytes, 0, $bytes.Length)
        } finally {
            $stream.Dispose()
        }
    } finally {
        $archive.Dispose()
    }

    $mutatedHash = (Get-FileHash -LiteralPath $mutatedPackage -Algorithm SHA256).Hash
    if ($mutatedHash -eq $originalHash) { throw 'The package mutation did not change its SHA-256.' }

    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/build-and-deploy.yml')
    $start = [Array]::IndexOf($workflow, '      - name: Verify release artifact identity')
    $run = $start
    while ($run -ge 0 -and $run -lt $workflow.Count -and $workflow[$run] -ne '        run: |') { $run++ }
    if ($start -lt 0 -or $run -ge $workflow.Count) { throw 'The release artifact verification step was not found.' }
    $end = $run + 1
    while ($end -lt $workflow.Count -and $workflow[$end] -notmatch '^      - (name|uses):') { $end++ }
    $commands = @($workflow[($run + 1)..($end - 1)] | ForEach-Object {
        if ($_.StartsWith('          ')) { $_.Substring(10) } else { $_ }
    }) -join "`n"
    $releaseCheck = [scriptblock]::Create($commands)
    $originalWorkspace = $env:GITHUB_WORKSPACE
    $originalExpected = $env:EXPECTED_PACKAGE_SHA256
    try {
        $env:EXPECTED_PACKAGE_SHA256 = $ExpectedSha256
        $env:GITHUB_WORKSPACE = (Resolve-Path -LiteralPath $PackageDirectory).Path
        & $releaseCheck 6>&1 | ForEach-Object { Write-Host "RELEASE_CHECK_CONTROL: $_" }

        $env:GITHUB_WORKSPACE = $root
        $releaseRejected = $false
        try {
            & $releaseCheck
        } catch {
            $message = $_.Exception.Message
            if ($message -notlike '*Release package SHA-256 mismatch*' -or
                $message -notlike "*$ExpectedSha256*" -or $message -notlike "*$mutatedHash*") {
                throw
            }
            $releaseRejected = $true
        }
        if (!$releaseRejected) { throw 'The release artifact verification accepted different package bytes.' }
        Write-Host 'PASS: the actual release verification step rejects the mutated package.'
    } finally {
        $env:GITHUB_WORKSPACE = $originalWorkspace
        $env:EXPECTED_PACKAGE_SHA256 = $originalExpected
    }

    $rejected = $false
    try {
        & (Join-Path $PSScriptRoot 'Test-PackedTool.ps1') -PackageDirectory $root -ExpectedSha256 $ExpectedSha256
    } catch {
        $message = $_.Exception.Message
        if ($message -notlike '*Package SHA-256 mismatch*' -or
            $message -notlike "*$ExpectedSha256*" -or $message -notlike "*$mutatedHash*") {
            throw
        }
        $rejected = $true
    }
    if (!$rejected) { throw 'The smoke test accepted a different package with the canonical SHA-256.' }
    Write-Host "PASS: a different package ($mutatedHash) was rejected against canonical SHA-256 $originalHash."

    & (Join-Path $PSScriptRoot 'Test-PackedTool.ps1') -PackageDirectory $root -ExpectedSha256 $mutatedHash 6>&1 |
        ForEach-Object { Write-Host "MUTATION_CONTROL: $_" }
    if ((Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'The canonical package was changed during the provenance test.'
    }
    Write-Host 'PASS: the mutated package is otherwise installable and the canonical artifact was unchanged.'
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
