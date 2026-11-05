namespace Keboo._127Cache.Tests;

internal sealed class ConfigurationEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _previous;

    public ConfigurationEnvironment(Dictionary<string, string?> variables)
    {
        var names = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => new[] { "Proxy", "Sources", "Server" }.Any(section =>
                name.StartsWith(section + "__", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase)
                || name.Equals(section, StringComparison.OrdinalIgnoreCase)))
            .Concat(variables.Keys).Distinct(StringComparer.Ordinal).ToArray();
        _previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
        foreach (var (name, value) in variables) Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous) Environment.SetEnvironmentVariable(name, value);
    }
}