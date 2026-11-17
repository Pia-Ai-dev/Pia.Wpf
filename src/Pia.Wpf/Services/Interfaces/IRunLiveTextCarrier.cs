namespace Pia.Services.Interfaces;

public enum RunLiveTextKind
{
    Text,
    Reasoning,

    /// <summary>A tool round ended: the next text is a fresh model turn.</summary>
    RoundBreak,

    /// <summary>The step's exchange ended; anything shown for it goes.</summary>
    Ended,
}

/// <summary>One streamed piece of a running step. <see cref="Text"/> is user content: never stored, never logged.</summary>
public sealed record RunLiveTextChunk(Guid RunId, Guid? StepId, RunLiveTextKind Kind, string Text);

/// <summary>Relays a headless step's streamed text to whatever panel shows that run; keeps nothing.</summary>
public interface IRunLiveTextCarrier
{
    event Action<RunLiveTextChunk>? TextAppended;

    void Publish(RunLiveTextChunk chunk);
}
