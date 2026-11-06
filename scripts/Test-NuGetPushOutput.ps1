#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path (Join-Path (Join-Path $repositoryRoot '.github') 'workflows') 'build-and-deploy.yml'
$workflow = @(Get-Content -LiteralPath $workflowPath)
$job = [Array]::IndexOf($workflow, '  deploy-nuget:')
if ($job -lt 0) { throw 'The deploy-nuget job was not found.' }
$end = $job + 1
while ($end -lt $workflow.Count -and $workflow[$end] -notmatch '^  [A-Za-z0-9_-]+:') { $end++ }
$jobLines = @($workflow[$job..($end - 1)])
if ($jobLines -cnotcontains '    if: ${{ false }}') {
    throw 'The offline push test requires the deployment job to remain hard-disabled.'
}
$step = [Array]::IndexOf($jobLines, '      - name: Push NuGet (Trusted Publishing)')
if ($step -lt 0) { throw 'The guarded NuGet push step was not found.' }
$run = $step + 1
while ($run -lt $jobLines.Count -and $jobLines[$run] -ne '        run: |') { $run++ }
if ($run -ge $jobLines.Count) { throw 'The guarded NuGet push command was not found.' }
$commands = [Collections.Generic.List[string]]::new()
for ($i = $run + 1; $i -lt $jobLines.Count; $i++) {
    if ($jobLines[$i].StartsWith('          ')) {
        $commands.Add($jobLines[$i].Substring(10))
    } elseif ([string]::IsNullOrWhiteSpace($jobLines[$i])) {
        $commands.Add('')
    } else {
        break
    }
}
if ($commands.Count -eq 0) { throw 'The guarded NuGet push command is empty.' }
$pushStep = [scriptblock]::Create(($commands -join "`n"))

$packageDirectoryPath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $packageDirectoryPath -Filter '*.nupkg' -File)
if ($packages.Count -ne 1) { throw 'The offline push test requires exactly one package.' }
if (Test-Path -LiteralPath 'Env:NUGET_PUSH_API_KEY') {
    throw 'The offline push test refuses an environment containing a publishing credential.'
}

$originalWorkspace = $env:GITHUB_WORKSPACE
$originalExitCode = $global:LASTEXITCODE
$placeholder = 'offline-stub-only'
$env:GITHUB_WORKSPACE = $packageDirectoryPath
$env:NUGET_PUSH_API_KEY = $placeholder

function dotnet {
    $script:dotnetCalls++
    $script:dotnetArguments = @($args)
    $global:LASTEXITCODE = $script:stubExitCode
    foreach ($line in $script:stubOutput) { Write-Output $line }
}

$duplicateLine = "Package '$($packages[0].Name)' already exists at feed 'https://api.nuget.org/v3/index.json'."
$cases = @(
    @{ Name = 'new package'; ExitCode = 0; Output = @('  Created https://example.invalid/ 20ms', 'Your package was pushed.'); Outcome = 'published' }
    @{ Name = 'existing version'; ExitCode = 0; Output = @('  Conflict https://example.invalid/ 20ms', $duplicateLine); Outcome = 'duplicate' }
    @{ Name = 'failed push'; ExitCode = 1; Output = @('Your package was pushed.'); Error = 'NuGet push failed with exit code 1' }
    @{ Name = 'unknown success'; ExitCode = 0; Output = @('Request completed.'); Error = 'publication outcome was ambiguous' }
    @{ Name = 'unconfirmed conflict'; ExitCode = 0; Output = @('  Conflict https://example.invalid/ 20ms'); Error = 'publication outcome was ambiguous' }
    @{ Name = 'contradictory output'; ExitCode = 0; Output = @('Your package was pushed.', $duplicateLine); Error = 'publication outcome was ambiguous' }
)

try {
    foreach ($case in $cases) {
        $script:dotnetCalls = 0
        $script:dotnetArguments = @()
        $script:stubExitCode = $case.ExitCode
        $script:stubOutput = $case.Output
        $messages = [Collections.Generic.List[string]]::new()
        $failure = $null
        try {
            & $pushStep 6>&1 | ForEach-Object { $messages.Add($_.ToString()) }
        } catch {
            $failure = $_.Exception.Message
        }

        $expectedArguments = @(
            'nuget', 'push', $packages[0].FullName, '--api-key', $placeholder,
            '--source', 'https://api.nuget.org/v3/index.json',
            '--skip-duplicate', '--no-symbols', '--force-english-output'
        )
        if ($script:dotnetCalls -ne 1 -or
            ($script:dotnetArguments -join "`0") -cne ($expectedArguments -join "`0")) {
            throw "The $($case.Name) case did not invoke the offline stub exactly once with the expected arguments."
        }
        $output = $messages -join "`n"
        if ($case.ContainsKey('Error')) {
            if (!$failure -or $failure -notlike "*$($case.Error)*") {
                throw "The $($case.Name) case did not fail with the expected explanation: $failure"
            }
            if ($output -match 'NUGET_PUBLISH_OUTCOME=') {
                throw "The $($case.Name) case claimed a publish outcome despite failing."
            }
        } else {
            if ($failure) { throw "The $($case.Name) case unexpectedly failed: $failure" }
            if ($output -notmatch "NUGET_PUBLISH_OUTCOME=$($case.Outcome):" -or
                @([regex]::Matches($output, 'NUGET_PUBLISH_OUTCOME=')).Count -ne 1) {
                throw "The $($case.Name) case did not report exactly the expected outcome."
            }
        }
        Write-Host "PASS: offline push stub handled $($case.Name)."
    }
} finally {
    $env:GITHUB_WORKSPACE = $originalWorkspace
    Remove-Item -LiteralPath 'Env:NUGET_PUSH_API_KEY' -ErrorAction SilentlyContinue
    $global:LASTEXITCODE = $originalExitCode
}
