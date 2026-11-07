namespace Keboo._127Cache.Proxy.Tests;

public sealed class DotNetToolClientTests
{
    [Test]
    [Category("EndToEnd")]
    public async Task RewrittenToolInstallsAndRunsFromProxy()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        await using var fixture = new NuGetClientFixture();
        var originalPackage = await fixture.CreateToolPackageAsync(cancellationToken);
        await fixture.StartAsync(includeOlderRun: false, cancellationToken, originalPackage);
        const string version = "3.1.4-pr.755.run.100.attempt.1.artifact.2";

        await fixture.InstallToolAsync("Example.Tool", version, cancellationToken);
        var installed = await fixture.ListToolsAsync(cancellationToken);
        await Assert.That(installed).Contains("example.tool");
        await Assert.That(installed).Contains(version);
        await Assert.That((await fixture.RunToolAsync("example-fixture-tool", cancellationToken)).Trim()).IsEqualTo("example-tool-executed");
    }
}