using Microsoft.Extensions.AI;

namespace Pia.Models;

/// <summary>One running interview: the mode being designed, the provider answering, and the transcript
/// so far. Created by the caller so the provider choice matches the editor it was launched from.</summary>
public sealed class AdvancedCreationSession(AdvancedCreationMode mode, Guid? providerId = null)
{
    public AdvancedCreationMode Mode { get; } = mode;

    public Guid? ProviderId { get; } = providerId;

    /// <summary>The persona the interview runs on, or null for the mode default. Held whole rather than by
    /// id: the turn needs its provider pin, model type and effort, and re-reading it per turn would let a
    /// persona edited mid-interview change the model the transcript was built on.</summary>
    public Persona? Persona { get; set; }

    /// <summary>The transcript handed to the provider each turn. Grows by one pair per answer.</summary>
    internal List<ChatMessage> Messages { get; } = [];

    /// <summary>How many times the model has asked. Bounds what the interview can cost.</summary>
    public int AskTurns { get; internal set; }
}
