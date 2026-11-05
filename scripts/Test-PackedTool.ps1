#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packageDirectoryPath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $packageDirectoryPath -Filter 'Keboo.127Cache.*.nupkg' -File)
if ($packages.Count -ne 1) {
    throw "Expected exactly one Keboo.127Cache package in $packageDirectoryPath; found $($packages.Count)."
}

$archive = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $manifests = @($archive.Entries | Where-Object { $_.FullName -match '^[^/\\]+\.nuspec$' })
    if ($manifests.Count -ne 1) { throw 'The packed tool must contain exactly one root nuspec.' }
    $reader = [IO.StreamReader]::new($manifests[0].Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally {
    $archive.Dispose()
}
$metadata = $manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
$id = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
$version = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
if ($id -ne 'Keboo.127Cache' -or [string]::IsNullOrWhiteSpace($version)) {
    throw 'The package nuspec does not identify a versioned Keboo.127Cache tool.'
}

function Invoke-DotNet {
    param([string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

$root = Join-Path (Join-Path $repositoryRoot 'TestResults') "packed tool-$([Guid]::NewGuid().ToString('N'))"
if (Test-Path -LiteralPath $root) { throw "Smoke-test directory already exists: $root" }
$null = New-Item -ItemType Directory -Path $root -Force
$originalPackages = $env:NUGET_PACKAGES
$server = $null
$stdout = $null
$stderr = $null
$passed = $false
try {
    $env:NUGET_PACKAGES = Join-Path $root 'nuget-packages'
    $nugetConfig = Join-Path $root 'NuGet.Config'
    $document = [xml] '<configuration><packageSources><clear /><add key="packed-tool" value="" /></packageSources></configuration>'
    $document.configuration.packageSources.add.SetAttribute('value', $packageDirectoryPath)
    $document.Save($nugetConfig)

    $fakeProject = Join-Path (Join-Path $PSScriptRoot 'FakeGh') 'FakeGh.csproj'
    $fakeDirectory = Join-Path $root 'fake-gh'
    $intermediate = (Join-Path $root 'fake-gh-obj') + [IO.Path]::DirectorySeparatorChar
    $intermediateProperty = "-p:BaseIntermediateOutputPath=$intermediate"
    Invoke-DotNet -Arguments @('restore', $fakeProject, '--configfile', $nugetConfig, '--nologo', $intermediateProperty)
    Invoke-DotNet -Arguments @('build', $fakeProject, '--configuration', 'Release', '--no-restore',
        '--output', $fakeDirectory, '--nologo', $intermediateProperty)

    $toolDirectory = Join-Path $root 'tools'
    Invoke-DotNet -Arguments @('tool', 'install', 'Keboo.127Cache', '--tool-path', $toolDirectory,
        '--version', $version, '--configfile', $nugetConfig)
    $toolName = if ($IsWindows) { 'keboo-127cache.exe' } else { 'keboo-127cache' }
    $fakeName = if ($IsWindows) { 'gh.exe' } else { 'gh' }
    $tool = Join-Path $toolDirectory $toolName
    if (!(Test-Path -LiteralPath $tool -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $fakeDirectory $fakeName) -PathType Leaf)) {
        throw 'The installed tool or fake gh executable is missing.'
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $port = ([Net.IPEndPoint] $listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
    $baseUrl = "http://127.0.0.1:$port"
    $serverConfig = Join-Path $root 'proxy.json'
    @{
        Urls = $baseUrl
        Proxy = @{
            Repository = 'ci-fixture/empty'
            Workflow = 'ci.yml'
            PackagesArtifact = 'packages'
            VersionArtifact = 'build-version'
            PullRequests = @()
            RunIds = @()
            BaseUrl = $baseUrl
            RefreshSeconds = 120
            CacheDirectory = (Join-Path $root 'cache')
        }
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $serverConfig -Encoding utf8

    $fakeLog = Join-Path $root 'fake-gh.log'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $tool
    $start.WorkingDirectory = $fakeDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('serve')
    $start.ArgumentList.Add('--config')
    $start.ArgumentList.Add($serverConfig)
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -match '^(Proxy__|Urls$|ASPNETCORE_URLS$|DOTNET_URLS$|GH_TOKEN$|GITHUB_TOKEN$|GH_ENTERPRISE_TOKEN$)') {
            $null = $start.Environment.Remove($key)
        }
    }
    $start.Environment['PATH'] = $fakeDirectory
    $start.Environment['KEBOO_CI_GH_LOG'] = $fakeLog
    $server = [Diagnostics.Process]::Start($start)
    if (!$server) { throw 'Failed to start the installed tool.' }
    $stdout = $server.StandardOutput.ReadToEndAsync()
    $stderr = $server.StandardError.ReadToEndAsync()

    $health = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        if ($server.HasExited) { throw "The installed tool exited before becoming ready ($($server.ExitCode))." }
        try {
            $health = Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 2 -NoProxy
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } until ($null -ne $health -or [DateTime]::UtcNow -ge $deadline)
    if ($null -eq $health) { throw "The installed tool did not become ready at $baseUrl within 60 seconds." }
    if ($health.status -ne 'ok' -or $health.repository -ne 'ci-fixture/empty') {
        throw "Unexpected health response: $($health | ConvertTo-Json -Compress)"
    }

    $index = Invoke-RestMethod -Uri "$baseUrl/v3/index.json" -TimeoutSec 5 -NoProxy
    $flat = @($index.resources | Where-Object {
        $_.'@type' -eq 'PackageBaseAddress/3.0.0' -and $_.'@id' -eq "$baseUrl/flat/"
    })
    if ($index.version -ne '3.0.0' -or $flat.Count -ne 1) {
        throw "Unexpected NuGet service index: $($index | ConvertTo-Json -Depth 10 -Compress)"
    }
    $buildsResponse = Invoke-WebRequest -Uri "$baseUrl/builds" -TimeoutSec 5 -NoProxy
    $builds = ConvertFrom-Json -InputObject $buildsResponse.Content -NoEnumerate
    if ($builds -isnot [array] -or $builds.Count -ne 0) {
        throw "The empty fixture returned unexpected builds: $($buildsResponse.Content)"
    }

    $requests = @(Get-Content -LiteralPath $fakeLog)
    foreach ($expected in @(
        'repos/ci-fixture/empty/pulls?state=open&per_page=100&page=1',
        'repos/ci-fixture/empty/actions/workflows/ci.yml/runs?event=pull_request&status=success&per_page=100&page=1'
    )) {
        if ($requests -notcontains $expected) { throw "The fake gh did not receive $expected." }
    }
    $passed = $true
    Write-Host "PASS: installed Keboo.127Cache $version from the local package and verified health, v3 index, and empty fake-source builds."
} finally {
    if ($server) {
        try {
            if (!$server.HasExited) {
                try { $server.Kill($true) } catch [InvalidOperationException] { }
            }
            $server.WaitForExit()
            if (!$passed) {
                if ($stdout) { Write-Host "Server stdout:`n$($stdout.GetAwaiter().GetResult())" }
                if ($stderr) { Write-Host "Server stderr:`n$($stderr.GetAwaiter().GetResult())" }
            }
        } finally {
            $server.Dispose()
        }
    }
    $env:NUGET_PACKAGES = $originalPackages
    Remove-Item -LiteralPath $root -Recurse -Force
}
