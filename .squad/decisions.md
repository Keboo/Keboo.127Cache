# Squad Decisions

## Active Decisions

### 2026-11-05: Owner decisions on initial review questions (authoritative)

**Source:** kevin-bost-via-copilot-owner-decisions (id 9667113e-00a3-4641-a8ad-a51fb277d27f) · loadGuidance: ALWAYS

#### 2026-11-05T11:06:47-08:00: User directive
**By:** Kevin Bost (via Copilot)
**What:** Resolves the 6 open questions from Lead's initial review (see "Lead — Initial review decisions" below):
1. Config: a flat list of configured sources. Each source may point at a different repo, pipeline/workflow, and artifact. No separate grouping concepts required beyond that list.
2. Version rewriting: preserve the original prerelease label on ALL rewritten packages and manifests (nuspec versions and dependency ranges).
3. Signed packages: stripping the signature during rewriting is expected/accepted.
4. Web UI technology: Blazor.
5. Publishing: run publish on all pushes (to main) using `dotnet nuget push --skip-duplicate`, so a new package is only actually published when the version has changed.
6. Removing a cached build for a PR that is still open: the next refresh downloads it again (no hide/tombstone).
**Why:** User request — captured for team memory

### 2026-11-05: Lead — Initial review decisions

Author: Lead · Requested by: Kevin Bost · Tracking epic: #32

Status legend: **Accepted** = team-internal engineering rule, effective now. **Accepted (owner)** = confirmed by Kevin Bost in the owner decisions above, which are authoritative where they overlap. No items remain Proposed (D9 was the last; accepted by the owner on 2026-11-05).

#### D1. Immutability rule for cached builds — Accepted
A build is cacheable only when the pipeline run is `completed` + `success`. Once cached, everything keyed by
(source, run id, attempt, artifact id) — raw artifact ZIP, rewritten packages, package inventory — is immutable and never
re-downloaded or re-rewritten. Open-PR membership and "which run is latest" are *volatile* and are recomputed on refresh;
they decide visibility, never cache validity. Only explicit user eviction (or an opt-in policy) deletes cached data.
- Owner clarification (#6): evicting a cached build whose PR is still open is not tombstoned — the next refresh downloads it again.

#### D2. Cache layout is source-scoped — Accepted (implementation in #9)
`{CacheDirectory}/sources/{sourceName}/{runs.json,catalog.json,artifacts/,packages/}` with a single `.lock` at
`{CacheDirectory}`. Source `Name` is the stable key (filesystem-safe, case-insensitively unique). Existing single-source
caches must be migrated or rejected with a clear message. Any change to the rewritten-version format or on-disk layout bumps
a cache schema version.

#### D3. Config schema direction — Accepted (owner) (#8, #14)
```json
{
  "Server": { "BaseUrl": "http://localhost:55631", "CacheDirectory": null, "RefreshSeconds": 120 },
  "Sources": [
    { "Name": "velopack", "Type": "GitHubActions", "Repository": "velopack/velopack", "Workflow": "build.yml",
      "PackagesArtifact": "packages", "VersionArtifact": null, "PullRequests": [], "RunIds": [], "RunLimit": 100 }
  ]
}
```
- Owner decision: `Sources` is a flat list; each source may point at a different repo, pipeline/workflow, and artifact. No additional grouping concepts.
- Legacy `Proxy` section maps to one source + deprecation warning.
- Per-user default config file (`%APPDATA%/Keboo.127Cache/config.json` on Windows, `$XDG_CONFIG_HOME`/`~/.config/keboo-127cache/config.json` elsewhere) loaded automatically; `--config` overrides; env vars last.
- No repository-specific defaults (remove `velopack/velopack`).
- Accepted (owner, 2026-11-05): PR builds only for now; branch / manual-run builds are a possible future option. Final schema wording: see "Lead — Final wording for owner-decided items" below.

#### D4. Provider seam — Accepted, kept minimal (#9, #30)
One small `IBuildSource` (discover completed successful PR builds → artifacts → download). Implement GitHub Actions only;
Azure DevOps (#30) is the second implementation that validates the seam. No speculative plugin system.

#### D5. Rewritten version scheme — Accepted (owner) (#16)
Current: `{major.minor.patch[.rev]}-pr.{pr}.run.{run}.attempt.{attempt}.artifact.{artifactId}`; in-artifact deps pinned `[v]`.
Keep the shape (sortable by PR/run, SemVer 2, unique).
- (a) Accepted (owner): preserve the original prerelease label on ALL rewritten packages and manifests (nuspec versions and dependency ranges). Exact encoding (Lead suggested e.g. appending `.orig.beta.3`) is an implementation detail.
- (b) Accepted (2026-11-05): no source token; cross-source collisions are detected and logged. See final wording below.
- Signed packages — Accepted (owner): strip the signature during rewriting (rewritten package is necessarily unsigned).

#### D6. Web UI technology — Accepted (owner): Blazor (#22)
Owner decision: **Blazor**. Lead's original recommendation was Razor Components (Blazor) with static SSR (plus tiny
progressive-enhancement JS / enhanced forms) hosted in the same Kestrel process at `/` — ships in the ASP.NET Core shared
framework (no extra packages, no Node toolchain in CI, small tool package), works offline, built-in antiforgery for forms,
testable with bUnit/HTTP tests. Rejected alternatives: embedded static HTML/JS (or htmx) over `/api`; a Node-built SPA.

#### D7. Management API + CSRF — Accepted (#21)
UI and scripts share one JSON API under `/api`. The server stays loopback-only. State-changing endpoints must reject
cross-site requests (Origin/Host validation + required custom header or antiforgery token) because any web page can
target localhost. `/builds` stays for script compatibility until #29 replaces the scripts.

#### D8. Offline behaviour — Accepted (#11)
Discovery failure must never prevent startup or hide already-cached immutable builds; the degraded state must be visible
(`/health`, UI). A failed discovery is never cached and never presented as an authoritative empty feed.

#### D9. Single test framework — Accepted (owner): TUnit only (#19)
Owner decision (Kevin Bost, 2026-11-05): all tests use **TUnit** only; Keboo.127Cache.Proxy.Tests (currently xUnit v3)
migrates to TUnit per #19, and new tests are written in TUnit. (Lead had recommended xUnit v3 — not adopted.) See
"Tester — D9 TUnit migration notes" below.

#### D10. Release publishing — Accepted (owner), supersedes Lead's proposal (#27)
Owner decision: run publish on all pushes to main using `dotnet nuget push --skip-duplicate`, so a package is only actually
published when its version has changed. (Lead had proposed tag-driven versions, e.g. MinVer `vX.Y.Z`, publishing only from
release tags — not adopted.) Main/PR builds continue producing a downloadable `NuGet` artifact.

### 2026-11-05: Lead — Final wording for owner-decided items

Author: Lead · Requested by: Kevin Bost · Tracking epic: #32
Supersedes the corresponding parts of "Lead — Initial review decisions" (D3, D5, D6, D10, D1 clarification) above.
Status of every item below: **Accepted (Kevin Bost, 2026-11-05)**; Lead-level details marked (Lead).

#### D3. Config schema — Accepted (Kevin Bost, 2026-11-05) (#8, #9, #14)
The config is just a list of sources; each source names its own repository, pipeline/workflow and artifact. No grouping
concept or other extra structure; only a minimal top-level `Server` section.
```json
{
  "Server": { "BaseUrl": "http://localhost:55631", "CacheDirectory": null, "RefreshSeconds": 120 },
  "Sources": [
    { "Name": "velopack", "Repository": "velopack/velopack", "Workflow": "build.yml", "Artifact": "packages" }
  ]
}
```
- (Lead) Required per source: `Name`, `Repository`, `Workflow`, `Artifact`. Optional: `VersionArtifact` (#15), `Type`
  (default `GitHubActions`, only needed once #30 lands). `PullRequests`/`RunIds`/`RunLimit` are dropped from the new schema.
- **Scope: PR builds only.** Branch / manual-run builds are a possible future option, not a requirement. (Replaces the
  "Still open (Proposed)" bullet.)

#### D5. Rewritten version scheme — Accepted (Kevin Bost, 2026-11-05) (#16, #18, #24)
- Original prerelease label kept on ALL rewritten packages and manifests (version, nuspec, `.psmdcp`, dependency ranges).
- (Lead) Encoding: `1.2.0` → `1.2.0-pr.{pr}.run.{run}.attempt.{attempt}.artifact.{id}`;
  `1.2.0-beta.3` → `1.2.0-beta.3.pr.{pr}.run.{run}.attempt.{attempt}.artifact.{id}`, i.e. label first, then the existing suffix. Build metadata
  (`+sha`) dropped. Format change bumps the cache schema version (D2).
- (Lead) (b) Source token: **not added** — GitHub artifact ids are globally unique; cross-source collisions are detected and
  logged. Revisit only if #30 (Azure DevOps) can collide. (Replaces the "(b) Proposed" bullet.)
- Signed packages: signature stripped during rewrite; never rejected.

#### D6. Web UI — Accepted (Kevin Bost, 2026-11-05): Blazor (#21–#25)
(Lead) Blazor Web App (Razor Components) in the same Kestrel process; static SSR by default, Interactive Server only where
live updates are needed; no WebAssembly; no Node toolchain; antiforgery for form posts; bUnit/HTTP rendering tests.

#### D10. Release publishing — Accepted (Kevin Bost, 2026-11-05) (#27, #31)
Publish on every push to `main` with `dotnet nuget push --skip-duplicate`; only a version change publishes. Version lives
in source; release = bump version and push. Not tag-driven.

#### D11. Eviction of open-PR builds — Accepted (Kevin Bost, 2026-11-05) (#20, #21, #23, #25)
Removing a cached build deletes cached data only (no tombstone). If its PR is still open and the artifact is still
available, the next refresh downloads it again; closed-PR / expired-artifact builds do not return.

### 2026-11-05T11:16:27-08:00: Owner decision — Test framework: TUnit

#### 2026-11-05T11:16:27-08:00: User directive
**By:** Kevin Bost (via Copilot)
**What:** Standardize all tests on TUnit. Resolves D9 (#19): Keboo.127Cache.Proxy.Tests (currently xUnit) migrates to TUnit; new tests use TUnit only.
**Why:** User request — captured for team memory

### 2026-11-05: Tester — D9 TUnit migration notes

#### D9. Single test framework — Accepted (owner) — Kevin Bost, 2026-11-05: TUnit only

Author: Tester · Requested by: Kevin Bost · Issue: #19 · Supersedes the Lead's "Proposed" recommendation (xUnit v3).

##### Decision
All test projects use **TUnit** (central version 1.64.13). Keboo.127Cache.Proxy.Tests (xUnit v3, 20 tests) migrates to TUnit; Keboo.127Cache.Tests already uses TUnit. All new tests (#18, #25, feature issues) are written in TUnit.

##### Consequences / migration notes
- Remove `xunit.v3.mtp-v2` from Proxy.Tests csproj and Directory.Packages.props; add `TUnit` + the same global `<Using>`s as Keboo.127Cache.Tests.
- `[Fact]` → `[Test]`; `Assert.*` (~160 calls) → `await Assert.That(...)` (methods become `async Task`); `Assert.Throws*` → `Throws`/`ThrowsExactly` (prefer `ThrowsExactly` to preserve xUnit exact-type semantics); `Assert.Single` → `HasSingleItem()` + `.Single()`.
- `TestContext.Current.CancellationToken` → TUnit `TestContext.Current!.CancellationToken` or injected `CancellationToken` parameter.
- Per-test `IDisposable` temp directory pattern works unchanged (TUnit creates an instance per test).
- TUnit parallelizes all tests by default (xUnit serialized within a class) — audit temp dirs, Kestrel ports and static state; use `[NotInParallel]` only with a stated reason.
- CI coverage (`--coverage --coverage-output-format cobertura`) must still produce `TestResults/coverage.cobertura.xml` for both projects; TUnit meta-package carries the MTP code-coverage extension.
- Team docs mentioning xUnit (Tester charter "What I Own", team.md/history stack lines) should be updated by Scribe/owners to say TUnit.

##### Issues updated (2026-11-05)
- #19 retitled "Migrate Keboo.127Cache.Proxy.Tests from xUnit to TUnit", body rewritten with acceptance criteria + migration map.
- #18 context notes TUnit for new tests; #18/#25/#32 links to #19 retitled. (#28 had no xUnit references.)

### 2026-11-05: Owner directive — Backlog execution safety and commit attribution

**Source:** kevin-bost-via-copilot-backlog-execution-safety-and-commit-attribution-40e4aa7f (id 40e4aa7f-767e-48bb-825c-2e27492fd97b) · loadGuidance: ALWAYS

#### 2026-11-05T11:38:14.932-08:00: User directive for this backlog run
**By:** Kevin Bost (via Copilot)
**What:** Never overwrite unrelated modifications or rewrite history. Do not deploy, publish a package, merge to main, or close an issue without completion and necessary authorization; configuring release automation is in scope. Any commits created for this work must end with `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
**Why:** User request — capture the execution and attribution constraints for the team.

### 2026-11-05: Owner directive — Backlog merge authorization and release safety

**Source:** kevin-bost-via-copilot-backlog-merge-authorization-and-release-safety-4356ee0e (id 4356ee0e-bfad-4f78-8a31-7dae0eee9e08) · loadGuidance: ALWAYS

#### 2026-11-05T11:38:14.932-08:00: Updated user directive for this backlog run
**By:** Kevin Bost (via Copilot)
**What:** The earlier no-merge instruction is superseded: autonomously merge independently verified, completed backlog PRs to main in dependency order, resolving conflicts and continuing to other items. Do not bypass branch protections or leave main broken. Do not publish packages or deploy unless separately authorized. Every commit created must end with `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
**Why:** User explicitly authorized merging, while maintaining a separate publication/deployment authorization boundary.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
