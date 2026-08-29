namespace Pia.Models;

/// <summary>Build version for user-facing display and file provenance, independent of the updater.</summary>
public static class AppVersionInfo
{
    /// <summary>Nerdbank.GitVersioning's informational version without the "+commit" suffix, e.g. "1.3.1000".</summary>
    public static string Version { get; } = Strip(ThisAssembly.AssemblyInformationalVersion);

    public static string Generator => $"Pia {Version}";

    private static string Strip(string informational)
    {
        var plus = informational.IndexOf('+');
        var v = plus >= 0 ? informational[..plus] : informational;
        return string.IsNullOrWhiteSpace(v) ? "0.0" : v;
    }
}
