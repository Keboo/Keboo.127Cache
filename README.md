# Keboo.127Cache

`Keboo.127Cache` is a .NET global tool that serves NuGet packages produced by
completed GitHub Actions pull-request builds through a local NuGet v3 feed. It
discovers package IDs from each pipeline's configured package artifact, so
package lists do not need to be maintained by hand.

## Requirements

- .NET 10 SDK/runtime
- For a configured source, GitHub CLI (`gh`) installed and authenticated with
  permission to read its repository's pull requests, workflow runs, and artifacts
  for live discovery and downloads. An existing offline snapshot can be served
  without `gh`.

The tool only downloads artifact contents when a feed request needs them.
At startup it begins querying open pull requests, completed successful workflow
runs, and artifact metadata in the background; the listener does not wait for
GitHub. With no configured sources, the server starts with an empty feed and
does not contact GitHub.

## Install and run

Once a version is published, install the global tool:

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

To configure a source, copy `proxy.example.json`, replace the example repository,
workflow, and artifact with your own, then run:

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

## Web UI

`serve` also hosts a web UI at `http://localhost:55631/` (the configured `Server.BaseUrl`);
the startup log prints its URL. Pass `--open` to launch it in your default browser:

```powershell
keboo-127cache serve --open
```

- **Overview** (`/`): the copyable feed URL, per-source status (repository, workflow, artifact, last
  refresh, error), cache size, and a builds table grouped source, pull request, run with package ids,
  rewritten and original versions, cache state (downloaded, partially downloaded, metadata only), size
  and age, plus links to the pull request and run on GitHub.
- **Packages** (`/packages`): every package id with its served versions.
- **Sources** (`/sources`): view the effective source list and writable configuration file, and add, edit, or remove
  sources without restarting the server. Changes are validated, persisted to the writable config layer and hot-applied
  to the feed. Validation errors are shown beside their fields; stale forms are rejected if the config file changed.
  Higher-precedence source settings are identified and make the page read-only. Editing a source's repository,
  workflow, or either artifact clears its cache to prevent stale builds; renaming retains the old cache unless
  explicitly removed. Removing a source keeps its cache unless permanent cache deletion is selected. The optional
  VersionArtifact can be cleared by leaving it empty. JSON comments are not preserved when the source list is saved.
- **Refresh**: a "Refresh all sources" button and a per-source Refresh button rediscover builds from
  GitHub (same as `POST /api/refresh`). The page shows a busy state while it runs, then the result per
  source (builds found, or the error) and the updated tables.
- **Remove** (`/remove`): each cached build has a Remove link, and "Remove closed-PR builds" removes the
  builds of closed pull requests. Both first show a confirmation (a dry run) with the number of bytes that
  will be reclaimed and, per build, whether it comes back. Removal deletes the cache only: a build of an
  open pull request is downloaded again by the next refresh; a build of a closed pull request or with
  expired artifacts cannot be restored. The result page lists what was removed.

It is a Blazor Web App (Razor Components) in the same process using static server-side rendering:
pages work without JavaScript (a small script only adds the Copy button and the refresh busy state; actions are
plain POST forms protected by ASP.NET Core antiforgery tokens), there is no WebAssembly,
no Node toolchain and no CDN. The stylesheet and script are embedded in the tool and served from
`/_ui/`, so the browser makes no external requests. Components call `IManagementService` and
`ICacheEvictionService` directly. The UI owns `/`, `/packages` and `/remove`; the feed, `/api`, `/health` and
`/builds` keep their routes. UI requests get the same Host (DNS rebinding) and Origin checks as `/api`,
and the antiforgery key ring is stored in `{CacheDirectory}/.data-protection`, never in the user profile.

## Configuration

Configuration layers have the following precedence, from lowest to highest:

1. Built-in defaults and the tool's `appsettings.json`.
2. The optional per-user file: `%APPDATA%\Keboo.127Cache\config.json` on Windows;
   `$XDG_CONFIG_HOME/keboo-127cache/config.json`, or
   `~/.config/keboo-127cache/config.json` when `XDG_CONFIG_HOME` is unset or not
   an absolute path, elsewhere.
3. An explicit JSON file passed to `serve --config` (the file must exist).
4. Environment variables, using `__` between section names, for example
   `Server__BaseUrl` or `Sources__0__Repository`.

Each JSON `Sources` array replaces an earlier array, including when it is empty.
In the same JSON layer, a `Sources` array takes precedence over all legacy
`Proxy` source fields. For environment variables, legacy `Proxy__` source fields
first override source 0, retaining its other inherited fields (or using legacy
defaults when no source exists). Indexed `Sources__0__` fields then override the
corresponding fields, so a modern environment field wins only when both forms
set the same field. An explicitly configured source name stays unchanged; a
legacy-derived name follows the resulting repository.

A JSON `Sources` value must be an array, not a scalar or an object with numeric
keys. A JSON `Server` value must be an object; `{}` preserves inherited settings.
Partial environment overrides do not hide malformed section shapes. A complete
later source configuration can replace an earlier malformed `Sources` value.
An empty or absent `Sources` array is valid; there is no default repository.

| Setting | Default | Description |
| --- | --- | --- |
| `Server.BaseUrl` | `http://localhost:55631` | HTTP(S) loopback origin for listening and feed metadata; no trailing slash |
| `Server.CacheDirectory` | `null` (per-user local app data) | Location for metadata, artifacts, and rewritten packages |
| `Server.RefreshSeconds` | `120` | Lifetime of cached GitHub discovery data (minimum 10 seconds) |
| `Server.EvictClosedPullRequestsAfterDays` | `null` (off) | When set (minimum 1), cached builds of pull requests closed for at least this many days are removed automatically, at startup and then hourly. See [Removing cached builds](#removing-cached-builds) |
| `Sources` | `[]` | Flat list of independently configured sources |
| `Sources[n].Name` | Required | Stable, case-insensitively unique filesystem key of up to 255 ASCII letters, digits, `.`, `_`, `-`, excluding reserved names and trailing `.` |
| `Sources[n].Repository` | Required | GitHub `owner/repository` |
| `Sources[n].Workflow` | Required | Workflow filename to query |
| `Sources[n].Artifact` | Required | Run artifact containing `.nupkg` files |
| `Sources[n].VersionArtifact` | `null` | Optional artifact containing root `version.txt`, used for the legacy `/builds` version |
| `Sources[n].Type` | `GitHubActions` | Source type; only `GitHubActions` is currently supported |

`/builds` supports the existing `vpk` installer scripts. Without
`VersionArtifact`, it downloads the packages artifact on its first request and
uses the `vpk` package's own version; builds without `vpk` are omitted from
`/builds`. All packages remain available through the feed with their own versions.

Example (the same schema can contain several independent entries):

```json
{
  "Server": {
    "BaseUrl": "http://localhost:55631",
    "CacheDirectory": null,
    "RefreshSeconds": 120
  },
  "Sources": [
    {
      "Name": "widgets",
      "Repository": "contoso/widgets",
      "Workflow": "ci.yml",
      "Artifact": "packages"
    }
  ]
}
```

Every source is served from one aggregated feed. Each source keeps its own
cache under `{CacheDirectory}/sources/{Name}/` (`runs.json`, `catalog.json`,
`artifacts/`, `packages/`, `inventories/`, …); a build is discovered, downloaded
and rewritten only by its own source, and its project URL and metadata come from
that source's repository. Rewritten versions carry no source token: the unique
artifact id already keeps versions from different sources apart. If two sources
nevertheless produce the same package id and version, the first configured
source wins and the collision is logged. Forks are simply additional sources.
Only pull-request builds are discovered.

If one source fails discovery, the others keep serving, but feeds that would be
incomplete without it (version indexes, search, `/builds`) return 503 rather
than silently omitting its packages. `/health` lists each source under
`sources`.

A cache created by an earlier version (flat `catalog.json`, `runs.json`,
`artifacts/` … directly under `CacheDirectory`) is moved into the directory of
the single configured source whose repository and workflow match it. If no
source, or more than one, matches, it is left untouched and a warning is
logged. The cache schema version is unchanged.

The legacy `Proxy` section still maps to one source and logs a deprecation
warning. `Proxy.Repository` supplies the source name from its repository
component (prefixed when needed for filesystem safety), and
`Proxy.PackagesArtifact` maps to `Artifact`; legacy server fields map to
`Server`. The old default workflow, packages artifact, and version
artifact are retained for legacy configurations, but a repository is required.
`Proxy.PullRequests`, `Proxy.RunIds`, and `Proxy.RunLimit` are ignored, each
with a warning. Across layers, the higher-precedence source configuration wins
subject to the environment-field behavior above. Use `Server` and `Sources`
for new configurations.

The tool serves the latest completed, successful run for each open PR. Queued,
running, failed, cancelled, and non-PR runs are excluded. The app refreshes
GitHub discovery on startup and after the in-memory cache expires. Startup lists
run artifacts but does not download their contents.

When GitHub omits a run's PR association, as it often does for forks, the app
matches the current open PR head SHA and, when available, its head repository
and branch. Ambiguous unpinned matches are skipped with a warning; ambiguous
explicitly pinned runs report an error rather than being attributed to the wrong
PR. Previously discovered cached runs remain available while the PR stays open.

For an explicitly pinned fork run whose SHA is no longer the PR head, the app
reuses an exact cached association when available. Otherwise, it checks that the
head repository and branch have belonged to only that PR and that the run SHA
appears in its current commit history. GitHub returns at most 250 PR commits;
force pushes or branch reuse can also prevent verification. In those cases the
app reports an error instead of guessing the association.

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
a subsequent download. Concurrent cache instances download independently, then
coordinate publication using a stable per-artifact `.zip.lock` file. The lock
file may remain after a process exits; do not remove it or its parent directory
while any cache instance is running. On Unix, this coordination checks an
exclusive advisory `flock` on the held file handle, independently of .NET's
file-sharing lock setting. A non-contention lock error prevents publication.
All cache processes must use this corrected locking protocol; other writers and
older versions must not share the cache. The filesystem must actually enforce
these locks; the application cannot detect a filesystem that silently grants
ineffective locks. Filesystems shared between machines must enforce those locks
across all participating machines.
Interrupted processes may leave unique `.tmp` files, which are ignored and may
be removed only when the cache is not in use. A missing, invalid, or mismatched
record for an already-cached digest-less artifact is not silently replaced:
restore the record from a trusted backup, or explicitly evict both the ZIP and
its record and download again if the artifact is still available.

The locally recorded hash is **trust on first download**, not proof of the
provider's original content. Protect the cache directory from untrusted writers:
someone who can change both the ZIP and its record can forge a matching pair.
The verification memo also cannot detect a same-size modification whose
observed timestamps do not change (including due to filesystem timestamp
resolution) until the next full verification, such as after a restart. It
does not prevent changes between verification and use.

On first package access, every NuGet package in that build's configured artifact
is discovered from its nuspec, rewritten to an immutable PR/run-specific
version, and cached. The feed exposes only the packages actually present in each
build. Dependencies on packages in the same artifact are pinned to their
corresponding rewritten versions. Before use, each cached nupkg's ZIP entries
are read in full and checked for their recorded lengths and CRCs; a readable
nuspec alone does not establish a healthy payload. CRCs detect corruption, not
malicious replacement by someone who can also change the recorded CRC.

For example, `1.2.0` becomes
`1.2.0-pr.755.run.r0000000000000000100.attempt.1.artifact.200`, while `1.2.0-beta.3+sha`
becomes `1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.artifact.200`. Original
prerelease labels are retained and build metadata is dropped. NuGet's
four-part versions are also supported. The rewritten version is used in the
nuspec, core properties, feed metadata, and exact in-artifact dependency pins;
external dependency ranges are unchanged.

Rewriting invalidates a package signature, so the signature is removed and
logged; the served package is unsigned. Schema v4 regenerates the complete
inventory from each SHA256-verified raw artifact rather than trusting a
potentially incomplete v1 or v2 inventory. Rewritten packages use short
internal filenames without changing their public versions. An integrity record
binds the inventory to the artifact digest and records the SHA256 of each
rewritten package; package identities and hashes are checked before a build is
advertised or served. These checks detect accidental corruption, not malicious
changes by someone who can also edit the integrity record.

Before upgrading a saved catalog, the tool compares each inventory with the
complete verified raw artifact for every known build, including closed PRs. If
one artifact is unavailable or invalid, that build is logged and excluded while
healthy builds remain available; the older catalog, packages, and raw artifacts
are retained. The next request retries migration. Restore an expired artifact's
matching raw ZIP to the artifact cache to resume migration. After migration, an
intact v4 cache remains usable without the raw artifact.
For a corrupt v4 cache, restore the matching inventory, integrity record, and
package bytes from a trusted backup. Alternatively, first preserve the affected
build directory elsewhere, then remove that directory only if its verified raw
artifact is available; the next request can regenerate it.

NuGet clients commonly write `<id>.<version>.nupkg.sha512` in their global
package cache. On Windows, if this filename would exceed the 255-character
(UTF-16 code unit) component limit, the entire build is logged and excluded.
On Unix-like systems, the guard uses the 255-byte UTF-8 component limit. The
approved public version is not shortened. Producing a new build with a shorter
original prerelease label or package ID can avoid this limit, but this does not
guarantee compatibility with stricter client or filesystem component/total-path
limits.

The feed enumerates run IDs numerically, including IDs larger than
`Int32.MaxValue`. Registration page bounds instead follow NuGet's version
comparer so exact-version dependency resolution works. When that order differs
from numeric order, the feed uses one-version registration pages so each page's
bounds are valid under either ordering. Some clients, including
NuGet.Versioning 7.9, compare oversized numeric prerelease identifiers lexically.
The rewritten `run` token avoids that path by using an `r` prefix plus a
fixed-width 19-digit decimal value (for example `r0000000003000000000`), so
floating version selection and real SDK restores choose newer run IDs correctly
under the approved format.

Keep the normal NuGet feeds enabled for external dependencies; this local feed
contains only packages from the configured workflow artifacts. If using NuGet
Package Source Mapping, allow the desired package IDs from this local source.

The app refuses simultaneous instances sharing a cache directory, and refuses a
cache belonging to a different repository or workflow. Changing a configured
artifact may require a different cache directory; cached run metadata is not
overwritten. When an open PR falls outside the recent discovery window, its
latest known cached run remains visible until a newer successful run is found.
Closed PRs disappear from the feed on the next discovery refresh.

## Removing cached builds

Removal deletes cached data only. Nothing records that a build was removed (no tombstone), so the
next discovery refresh downloads and rewrites it again, with the same version, whenever its pull
request is still open, it is the latest successful run and its artifacts have not expired.
Builds of closed pull requests and builds with expired artifacts do not come back, so removing
those frees the space permanently.

`ICacheEvictionService` (registered by `AddProxy`) is the programmatic surface used by management
tooling:

| Method | Purpose |
| --- | --- |
| `GetDiskUsageAsync()` / `GetSourceDiskUsageAsync(source)` | Bytes per source and per build (artifacts, rewritten packages, closed-since time, partial-cache flag) |
| `RemoveBuildAsync(source, runId, attempt, dryRun)` | Remove one run attempt |
| `RemovePullRequestAsync(source, pullRequest, dryRun)` | Remove every cached run of a pull request |
| `RemoveSourceAsync(source, dryRun)` | Empty one source, including unreferenced files |
| `RemoveClosedPullRequestsAsync(source?, closedFor?, dryRun)` | Remove builds of pull requests GitHub reports as closed (nothing is removed if GitHub is unreachable) |
| `ApplyRetentionPolicyAsync()` | Apply `Server.EvictClosedPullRequestsAfterDays` |

Every removal returns the bytes reclaimed and, per build, whether it will reappear
(`WillReappear`, `PullRequestClosed`, `ArtifactExpired`, `SupersededByNewerRun`,
`NotAvailableUpstream` or `Unknown` when GitHub could not be queried). `dryRun` reports the same
without deleting. A removal waits for an in-flight download of the same build to finish before
deleting it, and files in use are left in place and reported as an error for that build.
"Closed for N days" is measured from when 127Cache first observed the pull request closed
(`closed-pull-requests.json` in the source directory), because GitHub discovery only reports open
pull requests.

## Management API

The same process serves a JSON API under `/api` (every response carries `X-127Cache-Api-Version: 1`).
Errors are always `application/problem+json` (404 unknown source/route, 400 bad input, 403 rejected
cross-site request, 409 a build was in use, 503 discovery unavailable). `/builds` is unchanged and
still serves the `Install-PrBuild.ps1` scripts.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/sources` | name, repository, workflow, artifact, status (`ok`/`degraded`/`failed`), `lastRefreshUtc`, `lastError`, `editable` |
| `GET /api/sources/configuration` | where changes are saved, whether they can be, layers that override the file, `ETag`, current definitions |
| `POST /api/sources` | add a source (201); body `{name, repository, workflow, artifact, versionArtifact?, type?}` |
| `PUT /api/sources/{source}` | replace a source's definition (rename allowed) |
| `DELETE /api/sources/{source}[?removeCache=true]` | remove a source from the configuration |
| `POST /api/sources/reload` | re-read the configuration files and apply them |
| `GET /api/builds[?source=]` | per source/PR/run: version, size, `cachedState` (`cached`/`partial`/`notCached`), `served`; never downloads |
| `GET /api/packages[?source=]` | package id to versions, with source, PR, run, attempt (same content as the feed) |
| `POST /api/refresh[?source=]` | force rediscovery, bypassing the memory cache; concurrent calls for a source share one discovery (`coalesced`) |
| `DELETE /api/builds/{source}/{runId}/{attempt}` | remove one cached build |
| `DELETE /api/pull-requests/{source}/{pullRequest}` | remove every cached run of a pull request |
| `DELETE /api/sources/{source}/cache` | empty one source's cache |
| `DELETE /api/closed-pull-requests[?source=&closedForDays=]` | remove builds of closed pull requests |

All `DELETE`s accept `?dryRun=true` and delegate to `ICacheEvictionService`; the response lists
`bytesReclaimed`, `removedCount` and per build `willReappear`/`reappearance` (see above). Removing a
build that is not cached succeeds with `removedCount` 0. The UI should call the injected
`IManagementService` (`GetSourcesAsync`, `GetBuildsAsync`, `GetPackagesAsync`, `RefreshAsync`) and
`ICacheEvictionService` directly rather than HTTP.

### Managing sources

Sources can be added, edited and removed while the server runs; changes are written to the
configuration file and applied immediately, with no restart. The file written is the `--config` file
if one was given, otherwise the per-user `config.json` (created if missing). Only the `Sources` array
is rewritten (legacy `Proxy` source fields are migrated into it); other settings are kept, but
**comments in the file are lost**. Writes are atomic.

- A layer after the file (environment variables, or a `Sources` array in a later layer) overrides it. Then
  `GET /api/sources/configuration` reports `isEditable: false` with the reason and `shadowingLayers`, and
  changes return 409. Layers below the file are listed in `overriddenLayers` and do not block editing.
- Invalid input returns 400 with field-level `errors` (`name`, `repository`, `workflow`, `artifact`,
  `versionArtifact`, `type`); nothing is written.
- Changes are serialized. Send `If-Match` with the `ETag` from the configuration endpoint to detect
  edits made outside the UI (412); `hasExternalChanges` and `POST /api/sources/reload` resync.
- Editing a source's repository, workflow or either artifact empties that source's cache; unchanged sources
  keep their state. Removing (or renaming) a source keeps its cache on disk unless `removeCache=true`,
  which uses `ICacheEvictionService.RemoveSourceAsync` and returns 409 when a build is in use.
- The `/sources` page provides the static SSR forms for these operations and uses antiforgery tokens plus
  the same Host/Origin checks as other UI requests. It calls `ISourceConfigurationService` directly.

### Cross-site request protection

Any web page can send requests to a loopback server, so `/api` enforces:

- **Host**: the `Host` header must be `localhost`, a loopback address or the host of `Server.BaseUrl`
  (blocks DNS rebinding), for every method.
- **Origin**: on `POST`/`DELETE` an `Origin` header, if sent, must equal this server's origin, and
  `Sec-Fetch-Site` must be `same-origin` or `none`.
- **Custom header**: on `POST`/`DELETE` the request must send `X-127Cache-Client` (any non-empty
  value). Browsers only send custom headers cross-origin after a CORS preflight, which the server
  never grants.

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:55631/api/refresh -Headers @{ 'X-127Cache-Client' = 'script' }
```

Blazor form posts do not use the custom header. They post to the UI's own endpoints and use
ASP.NET Core antiforgery (`UseAntiforgery()` plus the antiforgery token in the form); they can also
call `ManagementRequestGuard.ValidateSameOrigin` for the Host/Origin checks.

## Offline use and health

Offline behavior applies only to discovery **outages** (GitHub API unreachable or failing).
While discovery succeeds, the feed serves the latest run of each open PR plus pinned
RunIds; a skipped, expired, missing or failed build is logged and isolated, and healthy
builds keep serving without a 503. Earlier retained runs and snapshots are served only
during an outage.

Before going offline, complete at least one successful discovery and request
the desired packages so their artifacts or rewritten packages are cached. The
app saves the **visible build set** from each successful discovery separately
from historical `runs.json` metadata. The snapshot is specific to the
repository, workflow, artifact names, PR/run selections, and run limit. A
corrupt snapshot or one from a different source is not replaced with guessed
visibility. If only selections or the run limit change, source-matching
historical runs may still be filtered and served offline, but their membership
is explicitly `unknown`; excluded selections remain excluded.

Released caches without a snapshot can still be used offline when their
`runs.json` identifies the configured repository and workflow. The app filters
those historical runs by the configured artifact names and PR/run selections.
**Their open-PR membership is unknown**: they can include PRs closed long ago,
and omit other builds. No refresh time or authoritative snapshot is invented.
Aggregate search, autocomplete, and `/builds` return 503 in this legacy mode;
known readable packages and complete per-package indexes remain usable with an
explicit `unknown` membership and `legacy-unverified` provenance. A legacy
`runs.json` workflow label does not prove whether a run was originally added
through a repository-wide pin. Known unverified pinned runs are excluded until
live workflow and artifact association verification succeeds; other legacy runs
may also have originated from an unrecorded pin. Operators requiring strict
workflow provenance should not use this unverified legacy fallback. A
catalog-only cache cannot establish workflow identity and is not guessed.
If catalog and run metadata disagree, version indexes return 503 rather than
hiding a cached version. Live discovery can corroborate matching runs and their
artifact identities; uncorroborated metadata remains incomplete.

If GitHub discovery or a required download fails, the server remains running
and serves usable packages from the last successful snapshot. Open-PR membership
is stale: a PR closed since that refresh can remain visible, while newly opened
PRs or newer builds can be missing. Feed requests with locally usable content
can serve it while a single shared discovery retry is in progress, without
waiting for the `gh` timeout. Each later request retries after a failure;
failures are not cached as an empty successful result. Once discovery succeeds,
the feed uses the newly refreshed membership. Historical metadata and cached
packages are not deleted when a PR disappears from the feed. A blocked download,
catalog gate, or offline artifact repair does not make unrelated cached requests
wait indefinitely; requests fall back to verifiable local content or return 503
after a short bounded wait. A cold feed with blocked discovery returns 503.
Problem details distinguish content or cached verification still in progress from
a known failure. Once pending content preparation succeeds, health can recover
without another feed request. Shutdown cancels and waits for background cache
work to release its files.

The app retains a source- and build-bound package inventory under `inventories`
after successful materialization. It can therefore distinguish an unavailable
Beta-only build from one that might contain another Alpha version, even if its
rewritten directory and raw ZIP are later removed. The inventory includes a
local SHA-256 checksum to detect accidental changes; it is not an authenticated
provider statement. Someone able to change both its contents and checksum can
forge it, so protect the entire cache from untrusted writers. Invalid or
disagreeing inventories cannot prove index completeness.

When discovery has observed an unavailable build, inconsistent metadata, or a
pinned workflow mismatch, an `incomplete-discovery.json` marker preserves that
uncertainty across restarts without replacing the last successful snapshot. A
complete live refresh removes the marker. An unreadable marker fails closed
rather than becoming a successful empty discovery. Transient read errors are
retried, allowing a valid marker to restore its partial state; invalid metadata
remains blocked until a complete live refresh.

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
metadata refresh until a complete content check succeeds. `provenance` is
`verified`, `legacy-unverified`, `incomplete`, or `unknown`; verification
describes the run/workflow and artifact association, not a package signature.

Degraded responses based on an available cached view carry `X-127Cache-Status`,
`X-127Cache-Membership`, `X-127Cache-Provenance`, and a stale `Warning` header.
The status is `degraded-partial` when content or discovery is incomplete. Search,
autocomplete, and `/builds` then return 503 instead of claiming complete
results. Flat and registration version indexes return 503 with problem details
if a known version of that package is unavailable, or an unavailable build's
package inventory is unknown. A known healthy package remains downloadable by
ID and version; an unrelated package index can succeed with `degraded-partial`
when retained inventories prove its cached versions are complete. An unknown
or missing package returns 503 rather than a misleading 404.

Missing or expired **unpinned** discovered builds are skipped without hiding
healthy builds, but discovery remains degraded and incomplete and no successful
snapshot is written. An unknown skipped build could contain any package, so
version indexes return 503; an already-known healthy package remains
downloadable by its exact ID and version. An unavailable pinned build fails
explicitly. Cached raw artifacts can reconstruct missing rewritten packages
offline, including artifacts that have since expired; damaged or unreadable
cache files are preserved. A
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
& (Join-Path 'scripts' 'Test-PackageMetadata.ps1') -PackageDirectory NuGet
& (Join-Path 'scripts' 'Test-NuGetPushOutput.ps1') -PackageDirectory NuGet
$package = Get-ChildItem -Path NuGet -Filter '*.nupkg' -File
$sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
& (Join-Path 'scripts' 'Test-PackedTool.ps1') -PackageDirectory NuGet -ExpectedSha256 $sha256
& (Join-Path 'scripts' 'Test-InstallState.ps1')
```

Installer compatibility tests require PowerShell 7.2 or later (`pwsh`). They
simulate the `dotnet tool` commands and do not install a global tool.

The solution follows the template's `Keboo.127Cache` / `Keboo.127Cache.Tests`
layout. The proxy implementation is kept in the main tool project; proxy protocol
and artifact-cache tests live in `Keboo.127Cache.Proxy.Tests`, with additional
artifact-integrity regression tests in `Keboo.127Cache.Tests`. The TUnit projects
also build a small fake `gh` executable for process-level offline and
authentication tests.
The released-main disk-cache fixture can be regenerated from its pinned Git
revision with `scripts/New-ReleasedCacheFixture.ps1`.
See [CONTRIBUTING.md](CONTRIBUTING.md) for test conventions and coverage commands.
On Linux and macOS, the artifact-integrity tests require a C compiler available
as `cc` to build their native `flock` fault injector.

The packed-tool smoke test installs the one package in the output directory using
an isolated local-only NuGet source and cache. It starts the installed command with
an isolated fake `gh`, then checks health, the v3 service index, and an empty
build list without GitHub credentials or remote repository access. CI runs this
test and the install-state script on Windows, Linux, and macOS. A single Ubuntu
job packs and uploads the `NuGet` artifact; each OS downloads that same artifact
by its immutable ID and verifies its SHA-256 before and after installation. The
disabled deployment job also requires the matrix to pass and verifies the same
artifact and hash before any publishing step.

Linux tests the entire solution with coverage; each test module gets its own
`TestResults/coverage.cobertura.xml`, without a fixed project list. The merged
report, its original inputs, and their paths are included in the `CodeCoverage`
artifact and pull-request summary. CI also adds failing and passing test
projects to a copy of the solution to verify discovery, and mutates a valid
package to verify that the smoke test rejects the wrong artifact. The package
job and each OS check the actual nuspec, README, license, icon, version and tool
contents, and mutate copies of the package to test those checks. Text
comparisons accept equivalent LF and CRLF line endings. The package job also
tests future push outcome reporting with a local `dotnet` stub: no login,
network push, or publishing credential is used.

## Release procedure and safety

Publishing is paused: the `deploy-nuget` job in
`.github/workflows/build-and-deploy.yml` is guarded by `if: ${{ false }}`.
Neither pushes to `main` nor manual dispatches can log in to NuGet or publish
with this workflow revision. PRs and pushes still build, test and upload the
canonical `NuGet` artifact; a green build or merged version bump is **not** a
published release while the guard is in place.

The package version has one source of truth: `<Version>` in
`Keboo.127Cache/Keboo.127Cache.csproj`. To prepare a release, bump that version
in a PR and check its package artifact and the Windows, Linux and macOS results.
No tag is needed to choose the version.

The intended push-to-publish procedure requires **separate owner authorization**
and a reviewed change to the hard-false guard first. The authorized condition
must limit publishing to pushes to `main`, never PRs or manual dispatches. That
reviewed change must also update the hard-false assertions in the workflow
contract and offline push test; otherwise the package job will correctly fail.
Once that change is authorized and enabled, merging a version bump to `main` submits
the canonical, matrix-tested package to NuGet using `--skip-duplicate`: an
existing version is skipped, and a new version is accepted for publication.
Confirm the package, version, Trusted Publishing setup and trigger scope before
enabling the guard; merging that change can itself trigger a publish. The push
step reports a new package or a duplicate, and fails rather than guessing if
NuGet's output is ambiguous.

The current workflow does not create Git tags or GitHub Releases. Do not rerun
older, unguarded workflow runs or dispatch an older, unguarded ref: the guard
applies to this workflow revision only. The push-to-publish criterion in #27
remains incomplete until publication is separately authorized and verified.
