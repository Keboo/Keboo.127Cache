#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $WorkflowPath = (Join-Path (Split-Path -Parent $PSScriptRoot) '.github/workflows/build-and-deploy.yml')
)

$ErrorActionPreference = 'Stop'

function Get-JobBlock {
    param([string] $Source, [string] $Name)
    $pattern = '(?ms)^  ' + [regex]::Escape($Name) +
        ':[ \t]*\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:[ \t]*\r?\n|\z)'
    $match = [regex]::Match($Source, $pattern)
    if (!$match.Success) { throw "Workflow job not found: $Name." }
    return $match.Groups['body'].Value
}

function Get-StepBlock {
    param([string] $Job, [string] $Name)
    $pattern = '(?ms)^      - name: ' + [regex]::Escape($Name) +
        '[ \t]*\r?\n(?<body>.*?)(?=^      - (?:name|uses):|\z)'
    $match = [regex]::Match($Job, $pattern)
    if (!$match.Success) { throw "Workflow step not found: $Name." }
    return $match.Groups['body'].Value
}

function Assert-Pattern {
    param([string] $Text, [string] $Pattern, [string] $Message)
    if ($Text -notmatch $Pattern) { throw $Message }
}

function Assert-CanonicalDownload {
    param([string] $Step, [string] $Consumer)
    Assert-Pattern $Step 'uses:[ \t]*actions/download-artifact@' "$Consumer must download an Actions artifact."
    Assert-Pattern $Step 'artifact-ids:[ \t]*\$\{\{[ \t]*needs\.package\.outputs\.artifact-id[ \t]*\}\}' `
        "$Consumer must download the canonical artifact ID."
    Assert-Pattern $Step 'merge-multiple:[ \t]*true' "$Consumer must extract the canonical artifact directly."
    Assert-Pattern $Step 'digest-mismatch:[ \t]*error' "$Consumer must fail on an artifact digest mismatch."
}

function Assert-CanonicalHashInput {
    param([string] $Step, [string] $Consumer)
    Assert-Pattern $Step 'EXPECTED_PACKAGE_SHA256:[ \t]*\$\{\{[ \t]*needs\.package\.outputs\.sha256[ \t]*\}\}' `
        "$Consumer must use the canonical package SHA-256."
}

function Assert-WorkflowContract {
    param([string] $Source)
    $package = Get-JobBlock $Source 'package'
    $build = Get-JobBlock $Source 'build'
    $deploy = Get-JobBlock $Source 'deploy-nuget'

    $pack = Get-StepBlock $package 'Pack canonical tool'
    Assert-Pattern $pack '(?m)^[ \t]*run:[ \t]*dotnet[ \t]+pack\b[^\r\n]*--output[ \t]+NuGet[ \t]*$' `
        'The package job must pack the canonical tool into NuGet.'
    if ([regex]::Matches($Source, '(?m)^[ \t]*(?:run:[ \t]*)?dotnet[ \t]+pack\b').Count -ne 1) {
        throw 'Only the canonical package job may pack the tool.'
    }

    $metadataTest = Get-StepBlock $package 'Test package metadata'
    Assert-Pattern $metadataTest 'Test-PackageMetadata\.ps1[^\r\n]*-PackageDirectory[ \t]+''NuGet''' `
        'The canonical package must pass its metadata and mutation tests.'
    $pushTest = Get-StepBlock $package 'Test publish outcome reporting offline'
    Assert-Pattern $pushTest 'Test-NuGetPushOutput\.ps1[^\r\n]*-PackageDirectory[ \t]+''NuGet''' `
        'The guarded push outcome must be tested with an offline stub.'
    if ($metadataTest -match '(?m)^[ \t]*if:' -or $pushTest -match '(?m)^[ \t]*if:') {
        throw 'Package metadata and offline push tests must run unconditionally.'
    }

    $fingerprint = Get-StepBlock $package 'Fingerprint canonical package'
    Assert-Pattern $fingerprint '(?m)^[ \t]*id:[ \t]*fingerprint[ \t]*$' 'The package fingerprint step must have an output ID.'
    Assert-Pattern $fingerprint '\$packages\.Count[ \t]*-ne[ \t]*1' 'The package job must fingerprint exactly one package.'
    Assert-Pattern $fingerprint 'Get-FileHash[^\r\n]*-Algorithm[ \t]+SHA256' 'The canonical package must be SHA-256 fingerprinted.'
    Assert-Pattern $package 'sha256:[ \t]*\$\{\{[ \t]*steps\.fingerprint\.outputs\.sha256[ \t]*\}\}' `
        'The producer must expose its package SHA-256.'
    $upload = Get-StepBlock $package 'Upload canonical NuGet artifact'
    Assert-Pattern $upload '(?m)^[ \t]*id:[ \t]*upload[ \t]*$' 'The canonical artifact upload must have an output ID.'
    Assert-Pattern $upload 'uses:[ \t]*actions/upload-artifact@' 'The canonical package must be uploaded as an Actions artifact.'
    Assert-Pattern $upload '(?m)^[ \t]*name:[ \t]*NuGet[ \t]*$' 'The canonical artifact must be named NuGet.'
    Assert-Pattern $upload '(?m)^[ \t]*path:[ \t]*NuGet[ \t]*$' 'The canonical artifact must contain the packed NuGet directory.'
    Assert-Pattern $upload 'if-no-files-found:[ \t]*error' 'Missing canonical packages must fail the upload.'
    Assert-Pattern $package 'artifact-id:[ \t]*\$\{\{[ \t]*steps\.upload\.outputs\.artifact-id[ \t]*\}\}' `
        'The producer must expose its immutable artifact ID.'
    if ($package.IndexOf('      - name: Fingerprint canonical package') -gt
        $package.IndexOf('      - name: Upload canonical NuGet artifact')) {
        throw 'The canonical package must be fingerprinted before it is uploaded.'
    }
    if ($package.IndexOf('      - name: Test package metadata') -gt
        $package.IndexOf('      - name: Fingerprint canonical package') -or
        $package.IndexOf('      - name: Test publish outcome reporting offline') -gt
        $package.IndexOf('      - name: Fingerprint canonical package')) {
        throw 'Package metadata and offline push tests must precede fingerprinting.'
    }

    Assert-Pattern $build '(?m)^    needs:[ \t]*package[ \t]*$' 'The matrix must wait for the canonical package.'
    Assert-Pattern $build '(?m)^    if:[ \t]*\$\{\{[ \t]*always\(\)[ \t]*\}\}[ \t]*$' `
        'The matrix must report failures even if the package job fails.'
    $readiness = Get-StepBlock $build 'Require canonical package'
    Assert-Pattern $readiness 'PACKAGE_RESULT:[ \t]*\$\{\{[ \t]*needs\.package\.result[ \t]*\}\}' `
        'The matrix must check the package job result.'
    Assert-Pattern $readiness '\$env:PACKAGE_RESULT[ \t]*-ne[ \t]*''success''' `
        'The matrix must fail when the package job fails.'
    Assert-Pattern $readiness 'PACKAGE_ARTIFACT_ID:[ \t]*\$\{\{[ \t]*needs\.package\.outputs\.artifact-id[ \t]*\}\}' `
        'The matrix must check the canonical artifact ID.'
    Assert-CanonicalHashInput $readiness 'The matrix readiness check'
    Assert-Pattern $build 'os:[ \t]*\[windows-latest,[ \t]*ubuntu-latest,[ \t]*macos-latest\]' `
        'The matrix must cover Windows, Linux, and macOS.'
    $linuxTests = Get-StepBlock $build 'dotnet test with coverage'
    Assert-Pattern $linuxTests "if:[ \t]*runner\.os[ \t]*==[ \t]*'Linux'" 'Linux must run the coverage step.'
    Assert-Pattern $linuxTests 'Invoke-CiTests\.ps1[^\r\n]*-CoverageDirectory' `
        'Linux coverage must test the whole solution through the shared test command.'
    $otherTests = Get-StepBlock $build 'dotnet test'
    Assert-Pattern $otherTests 'Invoke-CiTests\.ps1' 'Windows and macOS must use the solution test command.'
    $discovery = Get-StepBlock $build 'Test solution discovery guard'
    Assert-Pattern $discovery 'Test-CiDiscovery\.ps1[^\r\n]*-BaselineCoverageDirectory' `
        'The matrix must exercise the solution discovery guard.'
    $download = Get-StepBlock $build 'Download canonical NuGet artifact'
    Assert-CanonicalDownload $download 'The matrix'
    Assert-Pattern $download '(?m)^[ \t]*path:[ \t]*NuGet[ \t]*$' 'The matrix must extract the canonical artifact into NuGet.'
    $downloadedMetadata = Get-StepBlock $build 'Test downloaded package metadata'
    Assert-CanonicalHashInput $downloadedMetadata 'The matrix metadata test'
    Assert-Pattern $downloadedMetadata 'Test-PackageMetadata\.ps1[^\r\n]*-PackageDirectory[ \t]+''NuGet''[^\r\n]*-ExpectedSha256[ \t]+\$env:EXPECTED_PACKAGE_SHA256' `
        'The matrix must validate downloaded package metadata.'
    $smoke = Get-StepBlock $build 'Test packed tool installation and server'
    Assert-CanonicalHashInput $smoke 'The matrix smoke test'
    Assert-Pattern $smoke 'Test-PackedTool\.ps1[^\r\n]*-PackageDirectory[ \t]+''NuGet''[^\r\n]*-ExpectedSha256[ \t]+\$env:EXPECTED_PACKAGE_SHA256' `
        'The matrix must smoke-test the downloaded NuGet package against its canonical SHA-256.'
    $provenance = Get-StepBlock $build 'Test packed tool provenance guard'
    Assert-CanonicalHashInput $provenance 'The provenance mutation'
    Assert-Pattern $provenance 'Test-PackedToolProvenance\.ps1[^\r\n]*-ExpectedSha256[ \t]+\$env:EXPECTED_PACKAGE_SHA256' `
        'The matrix must exercise the package provenance guard.'
    $installState = Get-StepBlock $build 'Test install-state recovery'
    Assert-Pattern $installState 'Test-InstallState\.ps1' 'The matrix must retain the install-state test.'
    if ($build.IndexOf('      - name: Download canonical NuGet artifact') -gt
        $build.IndexOf('      - name: Test downloaded package metadata') -or
        $build.IndexOf('      - name: Test downloaded package metadata') -gt
        $build.IndexOf('      - name: Test packed tool installation and server')) {
        throw 'The matrix must download and validate the canonical package before smoke-testing it.'
    }

    Assert-Pattern $deploy '(?m)^    if:[ \t]*\$\{\{[ \t]*false[ \t]*\}\}[ \t]*$' `
        'Deployment must remain hard-disabled.'
    Assert-Pattern $deploy '(?m)^    needs:[ \t]*\[package,[ \t]*build\][ \t]*$' `
        'Deployment must depend on both the canonical package and the whole matrix.'
    $releaseDownload = Get-StepBlock $deploy 'Download canonical NuGet artifact'
    Assert-CanonicalDownload $releaseDownload 'Deployment'
    if ($releaseDownload -match '(?m)^[ \t]*path:') {
        throw 'Deployment must download the canonical package into the workspace used by dotnet nuget push.'
    }
    $releaseCheck = Get-StepBlock $deploy 'Verify release artifact identity'
    Assert-CanonicalHashInput $releaseCheck 'Deployment'
    Assert-Pattern $releaseCheck 'Get-FileHash[^\r\n]*-Algorithm[ \t]+SHA256' 'Deployment must hash the downloaded package.'
    Assert-Pattern $releaseCheck '\$actual[ \t]*-ne[ \t]*\$env:EXPECTED_PACKAGE_SHA256' `
        'Deployment must reject a package with a different SHA-256.'
    if ($deploy.IndexOf('      - name: Verify release artifact identity') -gt
        $deploy.IndexOf('      - name: NuGet login')) {
        throw 'Deployment must verify the artifact before obtaining publishing credentials.'
    }
    $releasePush = Get-StepBlock $deploy 'Push NuGet (Trusted Publishing)'
    Assert-Pattern $releasePush 'dotnet[ \t]+nuget[ \t]+push[ \t]+\$packages\[0\]\.FullName' `
        'Deployment must push the single verified package.'
    Assert-Pattern $releasePush '(?m)^            --source[^\r\n]*--skip-duplicate[^\r\n]*--no-symbols[^\r\n]*--force-english-output[^\r\n]*$' `
        'Deployment must skip duplicates, avoid symbol pushes, and force English output.'
    Assert-Pattern $releasePush 'NUGET_PUBLISH_OUTCOME=published' 'Deployment must report a new package.'
    Assert-Pattern $releasePush 'NUGET_PUBLISH_OUTCOME=duplicate' 'Deployment must report a duplicate package.'
}

function Assert-MutationRejected {
    param([string] $Source, [string] $ExpectedMessage)
    $rejected = $false
    try {
        Assert-WorkflowContract $Source
    } catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
        $rejected = $true
    }
    if (!$rejected) { throw "Workflow mutation was not rejected: $ExpectedMessage" }
    Write-Host "PASS: workflow mutation rejected: $ExpectedMessage"
}

$source = (Get-Content -LiteralPath $WorkflowPath -Raw).Replace("`r`n", "`n")
Assert-WorkflowContract $source
Write-Host 'PASS: the checked-in workflow uses one canonical package and tests the solution.'

$readiness = Get-StepBlock (Get-JobBlock $source 'build') 'Require canonical package'
$match = [regex]::Match($readiness, '(?ms)^        run: \|\n(?<commands>.*)\z')
if (!$match.Success) { throw 'The canonical package readiness command was not found.' }
$commands = @($match.Groups['commands'].Value -split "`n" | ForEach-Object {
    if ($_.StartsWith('          ')) { $_.Substring(10) } else { $_ }
}) -join "`n"
$readinessCheck = [scriptblock]::Create($commands)
$originalResult = $env:PACKAGE_RESULT
$originalArtifact = $env:PACKAGE_ARTIFACT_ID
$originalHash = $env:EXPECTED_PACKAGE_SHA256
try {
    $env:PACKAGE_RESULT = 'success'
    $env:PACKAGE_ARTIFACT_ID = '12345'
    $env:EXPECTED_PACKAGE_SHA256 = 'a' * 64
    & $readinessCheck
    foreach ($case in @('failed job', 'missing artifact', 'missing hash')) {
        $env:PACKAGE_RESULT = if ($case -eq 'failed job') { 'failure' } else { 'success' }
        $env:PACKAGE_ARTIFACT_ID = if ($case -eq 'missing artifact') { '' } else { '12345' }
        $env:EXPECTED_PACKAGE_SHA256 = if ($case -eq 'missing hash') { '' } else { 'a' * 64 }
        $rejected = $false
        try {
            & $readinessCheck
        } catch {
            if ($_.Exception.Message -notlike '*did not produce a valid artifact ID and SHA-256*') { throw }
            $rejected = $true
        }
        if (!$rejected) { throw "The matrix readiness check accepted a $case." }
        Write-Host "PASS: the matrix readiness check rejected a $case."
    }
} finally {
    $env:PACKAGE_RESULT = $originalResult
    $env:PACKAGE_ARTIFACT_ID = $originalArtifact
    $env:EXPECTED_PACKAGE_SHA256 = $originalHash
}

$matrixGuard = 'if: ${{ always() }}'
$mutated = $source.Replace($matrixGuard, 'if: ${{ success() }}')
if ($mutated -eq $source) { throw 'Could not mutate the matrix failure guard.' }
Assert-MutationRejected $mutated 'The matrix must report failures even if the package job fails'

$coverageCommand = "& (Join-Path 'scripts' 'Invoke-CiTests.ps1') -CoverageDirectory"
$fixedListCommand = "& (Join-Path 'scripts' 'FixedProjectList.ps1') -CoverageDirectory"
$mutated = $source.Replace($coverageCommand, $fixedListCommand)
if ($mutated -eq $source) { throw 'Could not mutate the Linux coverage command.' }
Assert-MutationRejected $mutated 'Linux coverage must test the whole solution'

$artifactBinding = 'artifact-ids: ${{ needs.package.outputs.artifact-id }}'
$index = $source.IndexOf($artifactBinding, [StringComparison]::Ordinal)
if ($index -lt 0) { throw 'Could not mutate the matrix artifact binding.' }
$mutated = $source.Remove($index, $artifactBinding.Length).Insert($index, 'artifact-ids: 12345')
Assert-MutationRejected $mutated 'The matrix must download the canonical artifact ID'

$index = $source.LastIndexOf($artifactBinding, [StringComparison]::Ordinal)
if ($index -lt 0) { throw 'Could not mutate the deployment artifact binding.' }
$mutated = $source.Remove($index, $artifactBinding.Length).Insert($index, 'artifact-ids: 12345')
Assert-MutationRejected $mutated 'Deployment must download the canonical artifact ID'

$hashBinding = 'EXPECTED_PACKAGE_SHA256: ${{ needs.package.outputs.sha256 }}'
$smokeIndex = $source.IndexOf('      - name: Test packed tool installation and server', [StringComparison]::Ordinal)
$index = if ($smokeIndex -ge 0) { $source.IndexOf($hashBinding, $smokeIndex, [StringComparison]::Ordinal) } else { -1 }
if ($index -lt 0) { throw 'Could not mutate the matrix package hash binding.' }
$mutated = $source.Remove($index, $hashBinding.Length).Insert($index, 'EXPECTED_PACKAGE_SHA256: independently-computed')
Assert-MutationRejected $mutated 'The matrix smoke test must use the canonical package SHA-256'

$metadataStep = $source.IndexOf('      - name: Test downloaded package metadata', [StringComparison]::Ordinal)
$metadataCommand = 'Test-PackageMetadata.ps1'
$index = if ($metadataStep -ge 0) { $source.IndexOf($metadataCommand, $metadataStep, [StringComparison]::Ordinal) } else { -1 }
if ($index -lt 0) { throw 'Could not mutate the downloaded package metadata check.' }
$mutated = $source.Remove($index, $metadataCommand.Length).Insert($index, 'DisabledMetadataCheck.ps1')
Assert-MutationRejected $mutated 'The matrix must validate downloaded package metadata'

$packCommand = 'run: dotnet pack Keboo.127Cache --configuration Release --no-build --output NuGet'
$mutated = $source.Replace($packCommand, 'run: dotnet --info')
$index = $mutated.IndexOf('      - name: Download canonical NuGet artifact', [StringComparison]::Ordinal)
if ($mutated -eq $source -or $index -lt 0) { throw 'Could not mutate the package producer.' }
$mutated = $mutated.Insert($index, "      - name: Independently pack tool`n        $packCommand`n`n")
Assert-MutationRejected $mutated 'The package job must pack the canonical tool'

$guard = 'if: ${{ false }}'
$mutated = $source.Replace($guard, 'if: ${{ true }}')
if ($mutated -eq $source) { throw 'Could not mutate the deployment guard.' }
Assert-MutationRejected $mutated 'Deployment must remain hard-disabled'

$pushOptions = '--skip-duplicate --no-symbols --force-english-output'
$mutated = $source.Replace($pushOptions, '--no-symbols --force-english-output')
if ($mutated -eq $source) { throw 'Could not mutate the duplicate-skip option.' }
Assert-MutationRejected $mutated 'Deployment must skip duplicates'
