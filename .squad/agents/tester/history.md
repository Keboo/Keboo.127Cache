# Project Context

- **Owner:** Kevin Bost
- **Project:** Keboo.127Cache — a .NET global tool (`keboo-127cache serve`) that acts as a local NuGet v3 feed serving packages from PR pipeline artifacts. Goal: developers point NuGet at localhost to test PR builds easily. Key features: config file listing package sources (pipeline artifacts); rewrite package versions/manifests to avoid NuGet version conflicts; cache completed pipeline runs locally (artifacts are immutable once a run finishes); web frontend (started with the server) to view cached packages, trigger manual refresh, and evict cached items.
- **Stack:** .NET 10, C#, ASP.NET Core minimal APIs, GitHub CLI (`gh`) for GitHub Actions artifacts, xUnit test projects (Keboo.127Cache.Tests, Keboo.127Cache.Proxy.Tests), central package management, solution Keboo.127Cache.slnx
- **Created:** 2026-11-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-11-05 — Initial repository review (Lead)
- Build/test verified: `dotnet build Keboo.127Cache.slnx` → 0 warnings/0 errors; `dotnet test --solution Keboo.127Cache.slnx` → 23 passed / 0 failed (Proxy.Tests: 20 xUnit v3; Tests: 3 TUnit). SDK pinned 10.0.300 (global.json, MTP runner).
- Backlog: epic #32 lists issues #8–#31 by phase. Decisions/recommendations: `.squad/decisions/inbox/lead-initial-review.md`.
- Test projects: Keboo.127Cache.Tests (TUnit + Moq/AutoMock; ProgramTests.cs: 3 CLI parse/help tests) and Keboo.127Cache.Proxy.Tests (xUnit v3 mtp-v2 + NuGet.Protocol; ProxyTests.cs: 20 tests; FakeGitHub.cs builds real zip/nupkg fixtures with SHA256 digests, toggles FailDiscovery/FailDownloads/Expired/CorruptDownload, counts queries/downloads). Runner: Microsoft.Testing.Platform (global.json). Command: `dotnet test --solution Keboo.127Cache.slnx` → baseline 23/23 pass (2026-11-05).
- `HttpFeedHasConsistentIdentitiesAndDependencies` is one large end-to-end test (real Kestrel on a free port + NuGet.Protocol metadata/search/autocomplete) — candidate to split.
- Not covered: real `dotnet restore`/`tool install` against the feed, fork-PR runs, offline startup, digest-less artifacts, signed packages (only rejection), multi-source, concurrency of downloads, CSRF.
- Your issues: #18 NuGet client compatibility + rewriter golden tests (P1), #19 consolidate on one framework (Lead recommends xUnit v3, D9), #25 management API/eviction/UI validation suite. Feature issues list expected tests in their acceptance criteria — review them against those.
- Manual scripts: scripts/Smoke-Test.ps1 (live proxy, vpk PR install/remove), scripts/Test-InstallState.ps1 (mocked install-state tests, PowerShell 7.2).

### 2026-11-05 — Owner decisions applied (Lead)
- #18: add golden/E2E tests for preserved prerelease labels (`1.2.0-beta.3` → `1.2.0-beta.3.pr.755.run.100.attempt.1.artifact.200`, in version, .psmdcp and dependency pins; floating `1.2.0-beta.3.pr.755.*`) and for signed packages being served with the signature stripped (not rejected).
- #25: UI is Blazor → bUnit/HTTP rendering tests (Playwright only if needed), antiforgery rejection tests; eviction: remove → refresh re-downloads open-PR builds (download counter increments), closed/expired builds stay gone.
- Multi-source tests use sources with different repo/workflow/artifact (flat `Sources[]`, #8).
### 2026-11-05 — D9 resolved: TUnit only (owner, Kevin Bost)
- All tests standardize on **TUnit** (1.64.13, central). Proxy.Tests (xUnit v3 `xunit.v3.mtp-v2` 3.2.2, 20 `[Fact]`s) migrates under #19 (retitled "Migrate Keboo.127Cache.Proxy.Tests from xUnit to TUnit"); #18/#25 new tests are TUnit. My charter's "xUnit test projects" wording is stale.
- Migration surface is mechanical but large: ~160 `Assert.*` calls → `await Assert.That(...)` (35 `Assert.Single` returning the item, 5 `ThrowsAsync`, 3 `Throws` → prefer `ThrowsExactly` to keep xUnit's exact-type semantics), `TestContext.Current.CancellationToken` → TUnit `TestContext`/injected `CancellationToken`, `IDisposable` per-test temp dir works as-is. No Theory/fixtures/ITestOutputHelper/WebApplicationFactory.
- Risk: TUnit runs tests in parallel by default (xUnit serialized within a class) — check temp dirs, Kestrel free-port, static state; `[NotInParallel]` only with reason.
- Coverage: CI passes `-- --coverage --coverage-output-format cobertura`; Proxy.Tests has no `Microsoft.Testing.Extensions.CodeCoverage` ref today — TUnit meta-package brings it; verify cobertura is produced for both projects after migration.
