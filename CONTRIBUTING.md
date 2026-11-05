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

For the same per-project Cobertura reports consumed by CI:

```powershell
dotnet build --configuration Release
Push-Location Keboo.127Cache.Tests
dotnet test --configuration Release --no-build -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
Pop-Location
Push-Location Keboo.127Cache.Proxy.Tests
dotnet test --configuration Release --no-build -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
Pop-Location
```

Run each coverage command from its project directory: a single solution-wide run
uses the same `TestResults` directory for both processes and can overwrite one
report. The reports are `Keboo.127Cache.Tests\TestResults\coverage.cobertura.xml`
and `Keboo.127Cache.Proxy.Tests\TestResults\coverage.cobertura.xml`.
