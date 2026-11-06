$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path 'TestResults' ("released-cache-fixture-" + [Guid]::NewGuid().ToString('N'))
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$originalTmpDir = $env:TMPDIR

Push-Location $repo
try {
    New-Item -ItemType Directory -Force $scratch | Out-Null
    $env:TEMP = (Resolve-Path $scratch).Path
    $env:TMP = $env:TEMP
    $env:TMPDIR = $env:TEMP
    $archive = Join-Path $scratch 'baseline.zip'
    $source = Join-Path $scratch 'source'
    $generated = Join-Path $scratch 'generated'
    $revision = '76253625ee24b80301555e4663c4fb6b99bd6317'
    git archive --format=zip "--output=$archive" $revision Keboo.127Cache Keboo.127Cache.Proxy.Tests `
        Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.config global.json LICENSE.md README.md
    if ($LASTEXITCODE -ne 0) { throw "Cannot archive released revision $revision." }
    Expand-Archive -Path $archive -DestinationPath $source
    $generator = Join-Path $source 'LegacyFixture'
    New-Item -ItemType Directory $generator | Out-Null
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup><ProjectReference Include="..\Keboo.127Cache\Keboo.127Cache.csproj" /><Compile Include="..\Keboo.127Cache.Proxy.Tests\FakeGitHub.cs" Link="FakeGitHub.cs" /></ItemGroup>
</Project>
'@ | Set-Content (Join-Path $generator 'LegacyFixture.csproj')
    @'
using Keboo._127Cache;
using Keboo._127Cache.Proxy.Tests;
using Microsoft.Extensions.Caching.Memory;

var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var github = new FakeGitHub(root);
var options = new ProxyOptions { Repository = "fixture/project", Workflow = "build.yml", CacheDirectory = Path.Combine(root, "cache"), PullRequests = [] };
using var memory = new MemoryCache(new MemoryCacheOptions());
var artifacts = new ArtifactCache(options, github);
var catalog = new PackageCatalog(options, github, memory, artifacts, new PackageCache(options, artifacts));
github.OpenPullRequests = [new(755)];
github.Runs = [new(99, 1, "old", "completed", "success", "pull_request", [new(755)])];
await catalog.GetPackagesAsync(CancellationToken.None);
memory.Compact(1);
github.Runs = [new(100, 1, "head", "completed", "success", "pull_request", [new(755)]), github.Runs[0]];
var packages = await catalog.GetPackagesAsync(CancellationToken.None);
if (packages.Length != 7 || packages.Select(package => package.Build.RunId).Distinct().Count() != 2)
    throw new InvalidOperationException("The released baseline did not populate both expected builds.");
'@ | Set-Content (Join-Path $generator 'Program.cs')
    dotnet run --project (Join-Path $generator 'LegacyFixture.csproj') --configuration Release -- $generated
    if ($LASTEXITCODE -ne 0) { throw 'Released cache generation failed.' }
    $cache = Join-Path $generated 'cache'
    if (Test-Path (Join-Path $cache 'visible-runs.json')) { throw 'The baseline unexpectedly created a snapshot.' }
    $destination = Join-Path 'Keboo.127Cache.Tests' 'Fixtures' 'ReleasedMain-7625362.zip'
    Compress-Archive -Path (Join-Path $cache '*') -DestinationPath $destination -Force
    Get-FileHash -LiteralPath $destination -Algorithm SHA256
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    $env:TMPDIR = $originalTmpDir
    if (Test-Path $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    Pop-Location
}
