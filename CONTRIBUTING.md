# Contributing

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
