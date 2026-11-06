# Keboo.127Cache

`Keboo.127Cache` is a .NET global tool that serves NuGet packages produced by
completed GitHub Actions pull-request builds through a local NuGet v3 feed. It
discovers package IDs from each pipeline's `packages` artifact, so package lists
do not need to be maintained by hand.

## Requirements

- .NET 10 SDK/runtime
- GitHub CLI (`gh`) installed and authenticated with permission to read the
  repository's pull requests, workflow runs, and artifacts for live discovery
  and downloads. An existing offline snapshot can be served without `gh`.

The tool only downloads artifact contents when a feed request needs them.
At startup it begins querying open pull requests, completed successful workflow
runs, and artifact metadata in the background; the listener does not wait for
GitHub.

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
startup and after the in-memory cache expires. Startup discovery lists run
artifacts but does not download their contents.

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

## Offline use and health

Before going offline, complete at least one successful discovery and request
the desired packages so their artifacts or rewritten packages are cached. The
app saves the **visible build set** from each successful discovery separately
from historical `runs.json` metadata. It will not guess visibility from
historical runs: caches created by an older version need one successful refresh
with this version before they can be used offline. The snapshot is specific to
the repository, workflow, artifact names, PR/run selections, and run limit.
Changing these settings requires another successful refresh.

If GitHub discovery or a required download fails, the server remains running
and serves usable packages from the last successful snapshot. Open-PR membership
is stale: a PR closed since that refresh can remain visible, while newly opened
PRs or newer builds can be missing. Each later feed request retries discovery;
failures are not cached as an empty successful result. Once discovery succeeds,
the feed uses the newly refreshed membership. Historical metadata and cached
packages are not deleted when a PR disappears from the feed.

`GET /health` returns HTTP 200 with `status` (`ok` or `degraded`), a safe
`error` message, and `lastSuccessfulRefreshUtc` (or `null` if there has been no
successful snapshot). It responds without waiting for GitHub and initially
reports degraded while discovery is pending. `ok` reflects the last successful
refresh, not a continuous connectivity check; `/health` itself does not trigger
a retry. The response also reports `snapshotBuilds` and, after checking an
offline snapshot, counts of `availableCachedBuilds`, `unavailableBuilds`,
`unavailablePackages`, and `unavailableBuildMetadata`.

Stale feed responses carry `X-127Cache-Status: degraded` and a stale `Warning`
header. If only part of the snapshot is usable, the status header is
`degraded-partial`: cached packages remain downloadable by ID and version, but
search, autocomplete, and `/builds` return 503 because they cannot enumerate a
complete result. An unknown or missing package also returns 503 in this state
rather than a misleading 404. Cached raw artifacts can reconstruct missing
rewritten packages offline, including artifacts that have since expired; damaged
or unreadable cache files are preserved. Missing expired artifacts cannot be
downloaded. A discovered build with missing or uncached expired required
artifacts makes discovery incomplete; the app falls back to the last usable
snapshot or returns 503 rather than silently omitting that build.

When there is no matching successful snapshot, the snapshot contains no
builds, or none of its packages are locally usable, feed requests (including
the service index) return HTTP 503 with problem details and `Retry-After`, not
an empty successful feed. Check `/health` for the reason. If `gh` is missing,
install it from <https://cli.github.com/>; if authentication has expired, run
`gh auth login` and `gh auth status` and check repository access. Diagnostics do
not include raw CLI stderr or tokens.

## Development

```powershell
dotnet build --configuration Release
dotnet test --configuration Release --no-build
dotnet pack Keboo.127Cache --configuration Release --no-build --output NuGet
$package = Get-ChildItem -Path NuGet -Filter '*.nupkg' -File
$sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
& (Join-Path 'scripts' 'Test-PackedTool.ps1') -PackageDirectory NuGet -ExpectedSha256 $sha256
& (Join-Path 'scripts' 'Test-InstallState.ps1')
```

The solution follows the template's `Keboo.127Cache` / `Keboo.127Cache.Tests`
layout. The proxy implementation is kept in the main tool project; proxy protocol
and artifact-cache tests live in `Keboo.127Cache.Proxy.Tests`. The TUnit project
also builds a small fake `gh` executable for process-level offline and
authentication tests.

The packed-tool smoke test installs the one package in the output directory using
an isolated local-only NuGet source and cache. It starts the installed command with
an isolated fake `gh`, then checks health, the v3 service index, and an empty
build list without GitHub credentials or remote repository access. CI runs this
test and the install-state script on Windows, Linux, and macOS. A single Ubuntu
job packs and uploads the `NuGet` artifact; each OS downloads that same artifact
by its immutable ID and verifies its SHA-256 before and after installation. The
disabled deployment job also requires the matrix to pass and verifies the same
artifact and hash before any publishing step.

Linux tests the entire solution with coverage; the coverage extension generates a
separate report per test module, without a fixed project list. CI also adds a
failing test project to a copy of the solution to verify discovery, and mutates
a valid package to verify that the smoke test rejects the wrong artifact.

## Release safety

Publishing is paused: the `deploy-nuget` job in
`.github/workflows/build-and-deploy.yml` has an unconditional false condition.
For this workflow revision, neither pushes to `main` nor manual dispatches can
log in to NuGet or publish, regardless of credentials. CI still restores, builds,
tests, packs, and uploads the `NuGet` and coverage artifacts.

Only after separate owner authorization to publish, submit a reviewed change to
restore the original non-PR condition (`github.event_name != 'pull_request'`) or
a narrower authorized condition. Restoring the original condition permits
publishing on the push to `main` that merges the change, and on manual dispatches
from any branch. Confirm the package, version, and trigger scope before merging.
The guard applies to this workflow revision: do not rerun older, unguarded
workflow runs or dispatch an older, unguarded ref. Release work in #27 remains
open.
