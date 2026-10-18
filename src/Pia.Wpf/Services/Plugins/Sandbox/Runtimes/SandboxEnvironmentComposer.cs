namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>A container's environment is rebuilt from the registry, so every user variable outside the allowlist is blanked.</summary>
public static class SandboxEnvironmentComposer
{
    private static readonly HashSet<string> KeptNames = new(StringComparer.OrdinalIgnoreCase) { "Path", "TEMP", "TMP" };

    private static readonly HashSet<string> MergedByThePlan = new(StringComparer.OrdinalIgnoreCase) { "PATH", "NODE_OPTIONS" };

    public static Dictionary<string, string> Compose(
        IReadOnlyList<string> userRegistryNames,
        IReadOnlyDictionary<string, string> planEnvironment,
        IReadOnlyDictionary<string, string>? userEnvironment)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in NamesToBlank(userRegistryNames))
            environment[name] = string.Empty;

        foreach (var (name, value) in planEnvironment)
            environment[name] = value;

        foreach (var (name, value) in userEnvironment ?? new Dictionary<string, string>())
        {
            if (string.Equals(name, SandboxLaunchSpecBuilder.RuntimeDirectoryVariable, StringComparison.OrdinalIgnoreCase))
                continue;
            if (MergedByThePlan.Contains(name) && planEnvironment.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            environment[name] = value;
        }

        return environment;
    }

    public static IReadOnlyList<string> NamesToBlank(IReadOnlyList<string> userRegistryNames) =>
        [.. userRegistryNames
            .Where(name => !string.IsNullOrEmpty(name) && !IsKept(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool IsKept(string name) =>
        KeptNames.Contains(name) || name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase);
}
