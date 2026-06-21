using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Ambient "current turn's token map", flowing down a single logical async turn
/// via <see cref="AsyncLocal{T}"/>. Each <c>RunTurnAsync</c> sets this to its own
/// session's <see cref="ITokenMapService"/> for the duration of the turn so the
/// <c>TokenizingAiClientService</c> decorator tokenizes/detokenizes against the
/// running session's map — even when two turns interleave on the shared UI thread
/// (each <c>await</c> continuation carries its own <c>ExecutionContext</c>, so the
/// value is isolated per logical turn).
///
/// This is purely the decorator's reach-around for the in-flight turn. Out-of-turn
/// token-map work (clear-on-new-chat, display detok, memory re-init) addresses the
/// owning session's map field directly and must NOT read this.
/// </summary>
public static class TokenMapAmbient
{
    private static readonly AsyncLocal<ITokenMapService?> _current = new();

    /// <summary>The current logical turn's token map, or null outside any turn.</summary>
    public static ITokenMapService? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
