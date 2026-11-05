using System.CommandLine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Keboo._127Cache.Tests;

public class ProgramTests
{
    [Test]
    public async Task HelpListsServeCommand()
    {
        var command = Program.BuildCommandLine();
        using var output = new StringWriter();
        var result = command.Parse("--help");
        result.InvocationConfiguration.Output = output;

        var exitCode = await result.InvokeAsync();

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output.ToString()).Contains("serve");
    }

    [Test]
    public async Task ServeHelpListsConfigOption()
    {
        var command = Program.BuildCommandLine();
        using var output = new StringWriter();
        var result = command.Parse("serve --help");
        result.InvocationConfiguration.Output = output;

        var exitCode = await result.InvokeAsync();

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output.ToString()).Contains("--config");
    }

    [Test]
    public async Task ServeAcceptsConfigFilePath()
    {
        var result = Program.BuildCommandLine().Parse("serve --config proxy.json");

        await Assert.That(result.Errors).IsEmpty();
    }
}
