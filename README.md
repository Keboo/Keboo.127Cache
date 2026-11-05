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

For example, `1.2.0` becomes
`1.2.0-pr.755.run.100.attempt.1.artifact.200`, while `1.2.0-beta.3+sha`
becomes `1.2.0-beta.3.pr.755.run.100.attempt.1.artifact.200`. Original
prerelease labels are retained and build metadata is dropped. NuGet's
four-part versions are also supported. The rewritten version is used in the
nuspec, core properties, feed metadata, and exact in-artifact dependency pins;
external dependency ranges are unchanged.

Rewriting invalidates a package signature, so the signature is removed and
logged; the served package is unsigned. Schema v2 keeps regenerated packages
separate from older rewritten caches. It uses short internal filenames without
changing public package versions and can still read packages from earlier v2
drafts. Before upgrading a saved catalog, it verifies the regenerated packages
for every known build. If one artifact is unavailable or invalid, that build is
logged and excluded while healthy builds remain available; the older catalog,
packages, and raw artifacts are retained. The next request retries migration.
An expired artifact can be recovered by restoring its matching verified raw ZIP
to the artifact cache.

The feed enumerates run IDs numerically, including IDs larger than
`Int32.MaxValue`. Registration page bounds instead follow NuGet's version
comparer so exact-version dependency resolution works. When that order differs
from numeric order, the feed uses one-version registration pages so each page's
bounds are valid under either ordering. Some clients, including
NuGet.Versioning 7.9, compare numeric prerelease identifiers that exceed
`Int32.MaxValue` lexically: a floating version can therefore choose run
`3000000000` over the newer run `10000000000`. Server-side ordering cannot
change that client comparison; use an exact version as a workaround. Guaranteeing
numeric floating selection in those clients would require changing the approved
version format or the clients themselves.

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
