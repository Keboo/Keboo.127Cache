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
- No UI exists yet. Host: ASP.NET Core minimal APIs in Keboo.127Cache (Microsoft.NET.Sdk.Web), started by `keboo-127cache serve`; default `http://localhost:55631`, loopback-only (`AllowedHosts` localhost/127.0.0.1/[::1], `BaseUrl` must be loopback). Endpoints registered in ProxyExtensions.cs (`MapProxy`) and FeedEndpoints.cs.
- Existing data endpoints: `/health` ({status, repository}), `/builds` (BuildEntry[]: runId, attempt, pullRequest, headSha, originalVersion, version, packages artifact, versionArtifact). New `/api` comes from #21 (Backend).
- Your issues: #22 UI shell (sources/builds/packages), #23 refresh + remove actions, #24 package detail + copy-paste consume snippets. Depends on #21 (API) and #20 (eviction).
- Constraints: assets must be embedded in the global-tool package and work offline (no CDN); state-changing calls must carry CSRF protection (D7). UI tech recommendation (Proposed, awaiting Kevin): Razor Components static SSR, no Node toolchain (D6) — confirm before building.
- Rewritten versions look like `1.3.67-pr.755.run.100.attempt.1.artifact.200`; show PR/run links to `https://github.com/{repo}/pull/{n}`.

### 2026-11-05 — Owner decisions applied (Lead)
- UI tech decided: **Blazor** (#22) — Razor Components in the same Kestrel process, static SSR by default, Interactive Server only where live updates are needed, no WebAssembly/Node; antiforgery for form posts; bUnit/HTTP rendering tests.
- Remove (#23): confirmation must say an open-PR build is re-downloaded on the next refresh; closed/expired builds don't return.
- Versions shown/snippets (#24) include the preserved prerelease label, e.g. `1.2.0-beta.3.pr.755.run.100.attempt.1.artifact.200`, floating `1.2.0-beta.3.pr.755.*`; signed originals are served unsigned.