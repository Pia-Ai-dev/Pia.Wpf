using System.Text.Json;
using Pia.Models;

namespace Pia.Services;

/// <summary>Records the Privacy value the tokenization decision was taken from. Static because the
/// decorator is transient but its owners live for the process, so no instance can answer for the others.</summary>
internal static class TokenizationLatch
{
    private static int _latched;
    private static string? _decidedFrom;

    internal static bool IsLatched => Volatile.Read(ref _latched) != 0;

    /// <summary>A null value means the decision never read the setting, so anything later counts as stale.</summary>
    internal static void Latch(PrivacySettings? decidedFrom)
    {
        Volatile.Write(ref _decidedFrom, Serialize(decidedFrom));
        Volatile.Write(ref _latched, 1);
    }

    internal static bool IsStale(PrivacySettings? current) =>
        IsLatched && Volatile.Read(ref _decidedFrom) != Serialize(current);

    internal static void Reset()
    {
        Volatile.Write(ref _decidedFrom, null);
        Volatile.Write(ref _latched, 0);
    }

    private static string? Serialize(PrivacySettings? privacy) =>
        privacy is null ? null : JsonSerializer.Serialize(privacy);
}
