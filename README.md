# Keboo.127Cache

`Keboo.127Cache` is a .NET global tool that serves NuGet packages produced by
completed GitHub Actions pull-request builds through a local NuGet v3 feed. It
discovers package IDs from each pipeline's configured package artifact, so
package lists do not need to be maintained by hand.

## Requirements

- .NET 10 SDK/runtime
- For a configured source, GitHub CLI (`gh`) installed and authenticated with
  permission to read its repository's pull requests, workflow runs, and artifacts

The tool only downloads artifact contents when a request needs them. At startup
it queries open pull requests, completed successful workflow runs, and artifact
metadata, then caches that metadata locally. With no configured sources, the
server starts with an empty feed and does not contact GitHub.

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

**Multi-source serving is not implemented yet:** a list with multiple valid
sources passes configuration validation, but starting the server fails with a
clear message until source aggregation and source-scoped caches are implemented
in [#9](https://github.com/Keboo/Keboo.127Cache/issues/9). Configure at most one
source in the meantime; the tool never silently serves only the first entry.

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
while any cache instance is running. On Unix, this coordination uses advisory
file locks and requires a filesystem that supports them. Publication fails closed
if exclusive locking is unavailable or disabled; other writers and older
versions that do not honor the lock must not share the cache. Filesystems shared
between machines must enforce those locks across all participating machines.
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
corresponding rewritten versions.

Keep the normal NuGet feeds enabled for external dependencies; this local feed
contains only packages from the configured workflow artifacts. If using NuGet
Package Source Mapping, allow the desired package IDs from this local source.

The app refuses simultaneous instances sharing a cache directory, and refuses a
cache belonging to a different repository or workflow. Changing a configured
artifact may require a different cache directory; cached run metadata is not
overwritten. When an open PR falls outside the recent discovery window, its
latest known cached run remains visible until a newer successful run is found.
Closed PRs disappear from the feed on the next discovery refresh.

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
and artifact-cache tests live in `Keboo.127Cache.Proxy.Tests`.
See [CONTRIBUTING.md](CONTRIBUTING.md) for test conventions and coverage commands.

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
