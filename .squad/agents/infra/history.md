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
- Packaging: Keboo.127Cache.csproj (Web SDK, `PackAsTool`, `ToolCommandName` keboo-127cache, `PackageId` Keboo.127Cache, hard-coded `<Version>0.0.1</Version>`). Directory.Build.props (net10/C# 14, nullable, packs README.md + LICENSE.md), Directory.Packages.props (CPM + transitive pinning), global.json (SDK 10.0.300, MTP runner), NuGet.config (nuget.org only, source mapping `*`).
- CI: .github/workflows/build-and-deploy.yml — ubuntu-only build/test(coverage→ReportGenerator)/pack; deploy-nuget pushes on every non-PR run (main push) via Trusted Publishing (`NuGet/login@v1`, secret NUGET_USER) with `--skip-duplicate` → effectively publishes once. pr-code-coverage-comment.yml posts coverage on PRs. Dependabot auto-merge job exists; 4 Dependabot PRs open (#4–#7).
- Scripts (PowerShell 7.2, vpk-specific): Common.ps1, Install-PrBuild.ps1, Remove-PrBuild.ps1, Smoke-Test.ps1, Start-Proxy.ps1, Test-InstallState.ps1.
- Cache dir default: `LocalApplicationData/Keboo.127Cache/cache` (cross-platform via SpecialFolder); script state `LocalApplicationData/Keboo.127Cache/install`.
- Your issues: #27 release versioning/publishing (D10 proposed: tag-driven, MinVer), #28 cross-platform CI matrix + packed-tool install smoke test, #29 scripts → CLI `feed add|remove`/`tool install --pr`, #31 docs overhaul.

### 2026-11-05 — Owner decisions applied (Lead)
- Release (#27): publish on every push to `main` with `dotnet nuget push --skip-duplicate`; only a version change publishes. Not tag-driven (MinVer/NBGV dropped). Release = bump the in-source version and push; optional tag/GitHub Release only when a new version actually publishes.
- Docs (#31) must cover: flat `Sources` config (PR builds only), preserved prerelease labels, signature stripping, eviction re-download semantics, push-to-publish release procedure.
### 2026-11-05 — Test framework: TUnit only (Tester, owner decision D9)
- Proxy.Tests migrates from xUnit v3 to TUnit (#19). CI `dotnet test ... -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml` must keep producing `**/TestResults/coverage.cobertura.xml` for both projects (TUnit meta-package includes the MTP code-coverage extension; Proxy.Tests currently has no explicit CodeCoverage ref). `xunit.v3.mtp-v2` leaves Directory.Packages.props. No workflow change expected; verify on the migration PR (and in #28's matrix).
