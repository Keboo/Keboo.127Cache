# Project Context

- **Owner:** Kevin Bost
- **Project:** Keboo.127Cache — a .NET global tool (`keboo-127cache serve`) that acts as a local NuGet v3 feed serving packages from PR pipeline artifacts. Goal: developers point NuGet at localhost to test PR builds easily. Key features: config file listing package sources (pipeline artifacts); rewrite package versions/manifests to avoid NuGet version conflicts; cache completed pipeline runs locally (artifacts are immutable once a run finishes); web frontend (started with the server) to view cached packages, trigger manual refresh, and evict cached items.
- **Stack:** .NET 10, C#, ASP.NET Core minimal APIs, GitHub CLI (`gh`) for GitHub Actions artifacts, TUnit test projects (Keboo.127Cache.Tests, Keboo.127Cache.Proxy.Tests), central package management, solution Keboo.127Cache.slnx
- **Created:** 2026-11-05

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-11-05 — Initial repository review (Lead)
- Build/test verified: `dotnet build Keboo.127Cache.slnx` → 0 warnings/0 errors; `dotnet test --solution Keboo.127Cache.slnx` → 23 passed / 0 failed (Proxy.Tests: 20 xUnit v3; Tests: 3 TUnit). SDK pinned 10.0.300 (global.json, MTP runner).
- Backlog: epic #32 lists issues #8–#31 by phase. Decisions/recommendations: `.squad/decisions/inbox/lead-initial-review.md`.
- Code map (Keboo.127Cache/): ProxyOptions.cs (single `Proxy` section, `IsValid()` one generic message, velopack defaults, `JsonSettings.WriteAtomicAsync`), GitHubClient.cs (`IGitHubClient`; `gh api` subprocess per call, 10-min timeout; records `WorkflowRun`, `Artifact(Id,Name,Expired,Digest)`), PackageCatalog.cs (discovery, `runs.json`/`catalog.json`, one `_gate` semaphore, memory-cache key), ArtifactCache.cs (`artifacts/{id}.zip`, global semaphore, re-verifies SHA256 on every hit), PackageCache.cs (staging dir → `packages/discovered/...`, global semaphore, throws on partial dir), PackageRewriter.cs (`VersionFor` = `{x.y.z[.r]}-pr.N.run.N.attempt.N.artifact.N`, pins in-artifact deps to `[v]`, rejects signed packages), FeedEndpoints.cs (flat/registration/query/autocomplete; query/registration open every nupkg), CatalogWarmupService.cs (throws on gh failure → host fails to start).
- Your issues: P0 #8 config schema, #9 provider seam + `sources/{name}/` cache layout; P1 #10 fork PRs (`pull_requests` empty for forks → match by head SHA), #11 offline start, #12 lazy inventory + per-artifact locks, #14 user config/init, #15 optional version artifact, #16 rewriter hardening, #20 eviction, #21 `/api` with CSRF protection; P2 #13 digest-less artifacts, #17 token auth, #26 observability, #30 Azure DevOps.
- Rules: cached data keyed by (source, run, attempt, artifact) is immutable (D1); open-PR membership is volatile; failed discovery never cached nor shown as empty success (existing tests enforce this).
- Tests for your code live in Keboo.127Cache.Proxy.Tests (xUnit v3, `FakeGitHub` fake with counters for queries/downloads).

### 2026-11-05 — Owner decisions applied (Lead)
- Config (#8/#14): flat `Sources[]` with `Name`, `Repository`, `Workflow`, `Artifact` (+ optional `VersionArtifact`, `Type`) and a minimal `Server` section; `PullRequests`/`RunIds`/`RunLimit` dropped. Scope = PR builds only (branch/manual runs = future).
- Rewriter (#16): keep original prerelease label everywhere — `1.2.0-beta.3` → `1.2.0-beta.3.pr.755.run.100.attempt.1.artifact.200` (nuspec, .psmdcp, dependency pins); strip signatures instead of rejecting; bump cache schema version. No source token (#9).
- Eviction (#20/#21): delete cache only, no tombstone; next refresh re-downloads an open-PR build whose artifact is still available.
- UI is Blazor → antiforgery for form posts alongside JSON-API CSRF checks. Final wording: `.squad/decisions/inbox/lead-owner-decisions-final.md`.