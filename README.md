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

On first package access, every NuGet package in that build's `packages` artifact
is discovered from its nuspec, rewritten to an immutable PR/run-specific
version, and cached. The feed exposes only the packages actually present in each
build. Dependencies on packages in the same artifact are pinned to their
corresponding rewritten versions.

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
intact v3 cache remains usable without the raw artifact.
For a corrupt v3 cache, restore the matching inventory, integrity record, and
package bytes from a trusted backup. Alternatively, first preserve the affected
build directory elsewhere, then remove that directory only if its verified raw
artifact is available; the next request can regenerate it.

NuGet clients commonly write `<id>.<version>.nupkg.sha512` in their global
package cache. If this filename would exceed 255 UTF-8 bytes, the entire build
is logged and excluded rather than advertising a package that cannot be
installed on common filesystems. The approved public version is not shortened.
Producing a new build with a shorter original prerelease label or package ID
can avoid this limit. A shorter component does not guarantee success on clients
or filesystems with stricter component or total-path limits.

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

The solution follows the template's `Keboo.127Cache` / `Keboo.127Cache.Tests`
layout. The proxy implementation is kept in the main tool project; proxy protocol
and artifact-cache tests live in `Keboo.127Cache.Proxy.Tests`, with additional
artifact-integrity regression tests in `Keboo.127Cache.Tests`.
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
