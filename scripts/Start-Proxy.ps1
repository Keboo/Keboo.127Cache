#Requires -Version 7.2
[CmdletBinding()]
param([string] $Config)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\Keboo.127Cache\Keboo.127Cache.csproj'
$arguments = @('run', '--project', $project, '--no-launch-profile', '--', 'serve')
if ($Config) { $arguments += @('--config', [IO.Path]::GetFullPath($Config)) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Proxy exited with code $LASTEXITCODE." }
