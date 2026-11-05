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
- Architecture: single project `Keboo.127Cache` (Web SDK, PackAsTool, command `keboo-127cache serve [--config]`). Program.cs → `AddProxy`/`MapProxy` (ProxyExtensions.cs). Flow: `CatalogWarmupService` (blocking IHostedService) → `PackageCatalog.GetRunArtifactsAsync` (gh: open PRs → successful `pull_request` runs → artifacts; persisted `runs.json`; IMemoryCache TTL `RefreshSeconds`) → `GetBuildsAsync` (downloads version artifact `version.txt`, persists `catalog.json`) → `PackageCache` (downloads packages ZIP via `ArtifactCache` with SHA256 digest check, rewrites each nupkg via `PackageRewriter`, persists `packages/discovered/{pr}.{run}.{attempt}.{artifact}/packages.json`) → `FeedEndpoints` (NuGet v3 flat/registration/query/autocomplete).
- Single-source only (`Proxy` section); cache refuses other repo/workflow. GitHub via `gh api` subprocess per call.
- Key gaps → issues: multi-source config #8, provider seam/cache layout #9, fork PRs invisible (bug) #10, offline start (bug) #11, eager downloads + global locks (bug) #12, digest-less artifacts (bug) #13, user config/init #14, optional version artifact #15, rewriter hardening #16, token auth #17, eviction #20, /api #21, UI #22–#24, observability #26, release/CI #27–#28, scripts→CLI #29, ADO #30, docs #31.
- Proposed decisions awaiting Kevin: config schema (D3), version scheme (D5), UI tech = Razor Components static SSR (D6), release versioning (D10).
- 4 open Dependabot PRs (#4–#7) exist; not triaged by this review.
