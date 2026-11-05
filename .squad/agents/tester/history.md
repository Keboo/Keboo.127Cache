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
