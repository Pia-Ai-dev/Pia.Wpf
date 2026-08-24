namespace Pia.Logging;

/// <summary>
/// The locally-readable values the deterministic redaction tier keys on. Everything here is collected at
/// export time and passed in, so <see cref="LogRedactor"/> never reads the machine it is scrubbing.
/// </summary>
public sealed record RedactionKeys(
    string? RoamingRoot,
    string? LocalRoot,
    string? UserProfileRoot,
    string? MachineName,
    string? UserName,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> ProviderNames)
{
    public static RedactionKeys None { get; } = new(null, null, null, null, null, [], []);
}
