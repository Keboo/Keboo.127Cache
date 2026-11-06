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

The tool discovers the latest completed, successful run for each selected open
PR and retains previously cached runs for that PR, even if they fall outside
the current run listing. Queued, running, failed, cancelled, and non-PR runs
are excluded. Explicit run IDs still have to belong to an open PR. The app
refreshes GitHub discovery on startup and after the in-memory cache expires.
Startup discovery lists run artifacts but does not download their contents.

Run metadata and raw artifact ZIPs are cached by artifact ID. A supplied
`sha256:` digest is verified before a ZIP is cached; mismatched, malformed, or
unsupported supplied digests fail closed. When no digest is supplied, the app
warns once per artifact per cache instance and records the first download's
SHA-256 in an adjacent `.zip.sha256` file. Cached bytes are checked against the
upstream digest or this local record whenever the raw ZIP is accessed, including
after a restart. Successful checks are remembered using file size and
timestamps, so unchanged ZIPs are not rehashed on every access. Already
rewritten packages are separate cached files and do not cause the raw ZIP to be
rechecked on each feed request.

The local record is published before its ZIP. Failed or cancelled downloads
are not published, and a record left by an interrupted publication must match
a subsequent download. A missing, invalid, or mismatched record for an
already-cached digest-less artifact is not silently replaced: restore the record
from a trusted backup, or explicitly evict both the ZIP and its record and
download again if the artifact is still available.

The locally recorded hash is **trust on first download**, not proof of the
provider's original content. Protect the cache directory from untrusted writers:
someone who can change both the ZIP and its record can forge a matching pair.
The verification memo also cannot detect a same-size modification whose
observed timestamps do not change (including due to filesystem timestamp
resolution) until the next full verification, such as after a restart. It
does not prevent changes between verification and use.

On first package access, every NuGet package in that build's `packages` artifact
is discovered from its nuspec, rewritten to an immutable PR/run-specific
version, and cached. The feed exposes only the packages actually present in each
build. Dependencies on packages in the same artifact are pinned to their
corresponding rewritten versions.

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
from historical `runs.json` metadata. The snapshot is specific to the
repository, workflow, artifact names, PR/run selections, and run limit. A
mismatched or corrupt snapshot is not replaced with guessed visibility.

Released caches without a snapshot can still be used offline when their
`runs.json` identifies the configured repository and workflow. The app filters
those historical runs by the configured artifact names and PR/run selections.
**Their open-PR membership is unknown**: they can include PRs closed long ago,
and omit other builds. No refresh time or authoritative snapshot is invented.
Aggregate search, autocomplete, and `/builds` return 503 in this legacy mode;
known readable packages and complete per-package indexes remain usable with an
explicit `unknown` membership header. If the catalog and run metadata disagree,
version indexes also return 503 rather than hiding a cached version. A
catalog-only cache cannot establish the workflow identity and is not guessed.
Live discovery can establish current membership and corroborate cached runs
listed by the configured workflow; known cached runs that cannot be
corroborated keep version indexes incomplete until their run metadata is
restored or validated.

If GitHub discovery or a required download fails, the server remains running
and serves usable packages from the last successful snapshot. Open-PR membership
is stale: a PR closed since that refresh can remain visible, while newly opened
PRs or newer builds can be missing. Feed requests with locally usable content
can serve it while a single shared discovery retry is in progress, without
waiting for the `gh` timeout. Each later request retries after a failure;
failures are not cached as an empty successful result. Once discovery succeeds,
the feed uses the newly refreshed membership. Historical metadata and cached
packages are not deleted when a PR disappears from the feed.

`GET /health` returns HTTP 200 with `status` (`ok` or `degraded`), a safe
`error` message, and `lastSuccessfulRefreshUtc` (or `null` if there has been no
successful snapshot). `membership` is `current`, `stale`, or `unknown`. Health
responds without waiting for GitHub and initially reports degraded while
discovery is pending. `ok` reflects the last successful refresh and no known
unresolved content failure, not a continuous connectivity or integrity check;
`/health` itself does not trigger a retry. The response also reports
`snapshotBuilds` and, after checking cached content, counts of
`availableCachedBuilds`, `unavailableBuilds`, `unavailablePackages`, and
`unavailableBuildMetadata`. A known content failure remains degraded after a
metadata refresh until a complete content check succeeds.

Degraded feed responses carry `X-127Cache-Status`, `X-127Cache-Membership`,
and a stale `Warning` header. The status is `degraded-partial` when content or
discovery is incomplete. Search, autocomplete, and `/builds` then return 503
instead of claiming complete results. Flat and registration version indexes
return 503 with problem details if a known version of that package is
unavailable, or an unavailable build's package inventory is unknown. A known
healthy package remains downloadable by ID and version; an unrelated package
index can succeed when the available inventories prove its cached versions are
complete. An unknown or missing package returns 503 rather than a misleading
404.

Missing or expired **unpinned** discovered builds are skipped without hiding
healthy builds, but discovery remains degraded and incomplete and no successful
snapshot is written. A successful per-package index in this case carries the
`degraded-partial` header: a skipped uncached build could have contained other
versions. An unavailable pinned build fails explicitly. Cached raw artifacts
can reconstruct missing rewritten packages offline, including artifacts that
have since expired; damaged or unreadable cache files are preserved. A
digest-less legacy ZIP without its SHA-256 record cannot be used for
reconstruction, although an already rewritten readable package remains usable.

When discovery fails and neither a matching nonempty snapshot nor a usable
legacy view is available, feed requests (including the service index) return
HTTP 503 with problem details and `Retry-After`, not an empty successful feed.
Check `/health` for the reason. If `gh` is missing, install it from
<https://cli.github.com/>; if authentication has expired, run `gh auth login`
and `gh auth status` and check repository access. Diagnostics do not include
raw CLI stderr or tokens.

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
The released-main disk-cache fixture can be regenerated from its pinned Git
revision with `scripts/New-ReleasedCacheFixture.ps1`.

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
