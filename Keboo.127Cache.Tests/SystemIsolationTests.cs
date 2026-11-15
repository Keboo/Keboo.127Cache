using System.Text.RegularExpressions;

namespace Keboo._127Cache.Tests;

// Code in this repository must not change machine or user state outside the repository.
// See "System isolation" in CONTRIBUTING.md.
public sealed partial class SystemIsolationTests
{
    [Test]
    public async Task EveryDotnetCliHomeOverrideOptsOutOfUserPathChanges()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsGenerated(Path.GetRelativePath(root, f))))
        {
            var text = await File.ReadAllTextAsync(file);
            var overrides = CliHomeAssignment().Count(text);
            var optOuts = GlobalToolsPathOptOut().Count(text);
            if (overrides > optOuts)
                offenders.Add($"{Path.GetRelativePath(root, file)} sets DOTNET_CLI_HOME {overrides} time(s) but DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false only {optOuts} time(s)");
        }

        await Assert.That(offenders).IsEmpty();
    }

    private static bool IsGenerated(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or "artifacts" or "TestResults" or ".git" or "node_modules");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Keboo.127Cache.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the repository root.");
    }

    [GeneratedRegex("""(\[\s*"DOTNET_CLI_HOME"\s*\]|\$env:DOTNET_CLI_HOME)\s*=(?!=)""")]
    private static partial Regex CliHomeAssignment();

    [GeneratedRegex("""(\[\s*"DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"\s*\]|\$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH)\s*=\s*["']false["']""", RegexOptions.IgnoreCase)]
    private static partial Regex GlobalToolsPathOptOut();
}
