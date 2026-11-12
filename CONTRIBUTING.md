# Contributing

## System isolation

Nothing in this repository may change the state of the machine outside the
repository. This applies to the application, tests, scripts, CI helpers and
agent-driven work. Write only inside the repository (gitignored output such as
`bin`, `obj`, `artifacts`, `TestResults`) or inside a uniquely named temporary
directory that the code creates and deletes itself.

Do not, even temporarily:

- Change user or machine environment variables (`setx`,
  `[Environment]::SetEnvironmentVariable(..., 'User' | 'Machine')`, registry
  `Environment` keys). Set variables only on a child `ProcessStartInfo` or in
  the current process.
- Write to the registry, install or trust certificates (`dotnet dev-certs
  --trust`), add firewall rules, URL ACLs, services, scheduled tasks or startup
  entries, or edit the hosts file.
- Install global tools, workloads, SDKs or runtimes, or edit the user-level
  `NuGet.Config` or global package sources. Use `--tool-path`, `--configfile`
  and per-test `NUGET_PACKAGES` / `NUGET_HTTP_CACHE_PATH` directories.
- Run stress, soak or constrained-CPU loops on a developer machine.

Watch for implicit side effects of tools. The .NET SDK appends
`<DOTNET_CLI_HOME>\.dotnet\tools` to the **user PATH** the first time it runs
with a new `DOTNET_CLI_HOME`. `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` does not stop
this. Every process that overrides `DOTNET_CLI_HOME` must also set:

```csharp
start.Environment["DOTNET_CLI_HOME"] = home;
start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
start.Environment["DOTNET_NOLOGO"] = "1";
```

`SystemIsolationTests` enforces the PATH opt-out. Without it, each test run added
hundreds of entries until the user PATH exceeded Windows' 32,767-character
environment limit. New processes then failed with `0xc0000017`, which broke
Task Manager, MMC and SmartScreen, and a reboot did not fix it. If a change
needs machine-level setup, document it as a manual step for the developer
instead of automating it.

## Test conventions

Both test projects use the centrally versioned TUnit packages and the
Microsoft.Testing.Platform runner. Name tests for the behavior they verify, use
`[Test]` with `async Task`, and await every assertion. Preserve ordered sequence
comparisons where order matters, and use `ThrowsExactly<T>()` when the exact
exception type is part of the contract. Pass the injected `CancellationToken` or
`TestContext.Current!.Execution.CancellationToken` to cancellable operations.
Compare record sequences using each record's equality rather than structural
equivalence, which can miss mismatches in aliased objects. Assert `GetType()`
when the runtime type must be exact; `IsTypeOf<T>()` also accepts subclasses.

Tests run in parallel by default. Give each test its own uniquely named temporary
directory, fake dependencies, and cache instances; dispose the fixture and remove
its directory after the test. Avoid shared mutable state. For HTTP tests, let
Kestrel bind loopback port `0`, then use its assigned address for the client and
advertised feed URL. Use `[NotInParallel]` only when isolation is not possible,
and document the shared resource that requires it.

Run the complete solution:

```powershell
dotnet test --solution Keboo.127Cache.slnx
```

The real NuGet-client tests are tagged `EndToEnd` and run as part of the full
suite. To run only those tests:

```powershell
dotnet test --project (Join-Path 'Keboo.127Cache.Proxy.Tests' 'Keboo.127Cache.Proxy.Tests.csproj') -- --treenode-filter '/*/*/*/*[Category=EndToEnd]'
```

For the solution-wide Cobertura collection used by CI, use .NET SDK 10.0.403
or a later 10.0 feature version and supply a new, empty coverage directory:

```powershell
dotnet build --configuration Release
& (Join-Path 'scripts' 'Invoke-CiTests.ps1') -CoverageDirectory (Join-Path 'TestResults' 'ci-coverage')
```

The solution run discovers every test project. MTP isolates results by project
and target, then writes the fixed `TestResults/coverage.cobertura.xml` filename
within each module's directory. For example, on x64 the two current reports are:

```text
TestResults\ci-coverage\Keboo.127Cache.Tests\net10.0_x64\TestResults\coverage.cobertura.xml
TestResults\ci-coverage\Keboo.127Cache.Proxy.Tests\net10.0_x64\TestResults\coverage.cobertura.xml
```

CI checks that every test module has its own report, passes all reports to
ReportGenerator without a fixed project list, and includes the original inputs
in the `CodeCoverage` artifact and its pull-request summary.

Project names need not be unique: the SDK may hash both result directories when
names collide. The discovery guard maps each report to the module path in its
TRX record instead of inferring identity from the directory name. After the
solution coverage run, exercise the duplicate-name and probe-prefix regressions:

```powershell
& (Join-Path 'scripts' 'Test-CiDiscoveryRegression.ps1') -BaselineCoverageDirectory (Join-Path 'TestResults' 'ci-coverage')
```

To generate project-local reports with a fixed filename instead:

```powershell
dotnet build --configuration Release
Push-Location Keboo.127Cache.Tests
dotnet test --configuration Release --no-build -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
Pop-Location
Push-Location Keboo.127Cache.Proxy.Tests
dotnet test --configuration Release --no-build -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
Pop-Location
```

Run each fixed-filename coverage command from its project directory to prevent
one report from overwriting the other. The reports are
`Keboo.127Cache.Tests\TestResults\coverage.cobertura.xml` and
`Keboo.127Cache.Proxy.Tests\TestResults\coverage.cobertura.xml`.
