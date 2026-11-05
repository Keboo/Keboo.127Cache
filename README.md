# Keboo.127Cache

`Keboo.127Cache` is a .NET global tool that serves NuGet packages produced by
completed GitHub Actions pull-request builds through a local NuGet v3 feed. It
discovers package IDs from each pipeline's `packages` artifact, so package lists
do not need to be maintained by hand.

## Requirements

- .NET 10 SDK/runtime
- GitHub CLI (`gh`) installed and authenticated with permission to read the
  repository's pull requests, workflow runs, and artifacts

The tool only downloads artifact contents when a feed request needs them.
At startup it queries open pull requests, completed successful workflow runs,
and artifact metadata, then caches that metadata locally.

## Install and run

Install the published global tool:

```powershell
dotnet tool install --global Keboo.127Cache
keboo-127cache serve
```

Or, from a source checkout:

```powershell
dotnet run --project .\Keboo.127Cache\Keboo.127Cache.csproj -- serve
```

The default feed is `http://localhost:55631/v3/index.json`. Check
`http://localhost:55631/health` to see whether the service is running.

To select a particular pull request and listen on the default address, copy
`proxy.example.json`, edit its `Proxy` section, then run:

```powershell
keboo-127cache serve --config .\proxy.json
```

GitHub access uses the normal `gh` authentication. The tool does not store a
GitHub token. The included `scripts/Install-PrBuild.ps1` can add the feed as a
NuGet source and install a selected PR build of the `vpk` global tool;
`scripts/Remove-PrBuild.ps1` restores the previous tool version and removes only
the managed source. Run `scripts/Test-InstallState.ps1` to exercise cleanup and
interrupted-install recovery behavior. `scripts/Smoke-Test.ps1` exercises a full
PR build install/restore/removal against an already-running proxy.

## Configuration

Configuration can be supplied in `appsettings.json`, a file passed to
`serve --config`, or environment variables using the `Proxy__` prefix.

| Setting | Default | Description |
| --- | --- | --- |
| `Repository` | `velopack/velopack` | GitHub owner/repository |
| `Workflow` | `build.yml` | Workflow filename to query |
| `PackagesArtifact` | `packages` | Run artifact containing `.nupkg` files |
| `VersionArtifact` | `build-version` | Artifact containing root `version.txt`, used by `/builds` |
| `PullRequests` | `[]` | Optional allowlist of open PR numbers |
| `RunIds` | `[]` | Additional completed successful PR run IDs to include |
| `RunLimit` | `100` | Number of recent successful PR runs to scan (1–500) |
| `RefreshSeconds` | `120` | Lifetime of cached GitHub discovery data (minimum 10) |
| `BaseUrl` | `http://localhost:55631` | Loopback URL advertised in feed metadata |
| `CacheDirectory` | Per-user local app data | Location for metadata, artifacts, and rewritten packages |

Example:

```json
{
  "Urls": "http://localhost:55631",
  "Proxy": {
    "Repository": "velopack/velopack",
    "Workflow": "build.yml",
    "PackagesArtifact": "packages",
    "VersionArtifact": "build-version",
    "PullRequests": [755],
    "RunIds": [],
    "BaseUrl": "http://localhost:55631",
    "RefreshSeconds": 120,
    "RunLimit": 100
  }
}
```

The tool serves the latest completed, successful run for each selected open PR.
Queued, running, failed, cancelled, and non-PR runs are excluded. Explicit run
IDs still have to belong to an open PR. The app refreshes GitHub discovery on
startup and after the in-memory cache expires. Startup lists run artifacts but
does not download their contents.

Run metadata and raw artifact ZIPs are cached by artifact ID. Downloaded ZIPs
are SHA256-verified and reused across restarts. On first package access, every
NuGet package in that build's `packages` artifact is discovered from its nuspec,
rewritten to an immutable PR/run-specific version, and cached. The feed exposes
only the packages actually present in each build. Dependencies on packages in
the same artifact are pinned to their corresponding rewritten versions.

Keep the normal NuGet feeds enabled for external dependencies; this local feed
contains only packages from the configured workflow artifacts. If using NuGet
Package Source Mapping, allow the desired package IDs from this local source.

The app refuses simultaneous instances sharing a cache directory, and refuses a
cache belonging to a different repository or workflow. Closed PRs disappear from
the feed on the next discovery refresh.

## Development

```powershell
dotnet build
dotnet test
dotnet pack .\Keboo.127Cache\Keboo.127Cache.csproj
```

The solution follows the template's `Keboo.127Cache` / `Keboo.127Cache.Tests`
layout. The proxy implementation is kept in the main tool project; proxy protocol
and artifact-cache tests live in `Keboo.127Cache.Proxy.Tests`.
