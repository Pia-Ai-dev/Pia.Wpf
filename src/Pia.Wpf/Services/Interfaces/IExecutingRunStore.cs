namespace Pia.Services.Interfaces;

/// <summary>
/// A2: the in-process index of agent runs that are ACTUALLY EXECUTING right now, keyed by chat, so a chat
/// activation can decide SYNCHRONOUSLY whether a foreign writer owns the transcript — without awaiting a
/// store read that blocks on <c>AgentRunService</c>'s gate (which the executing run itself holds on every
/// step commit).
/// <para>
/// Populated from the LAUNCH BRACKETS, not from run state: <c>HeadlessRunLauncher</c>'s two dispatch
/// lambdas and <c>BackgroundAssistantTurnRunner.RunAsync</c>. Deriving it from run rows would mean reading
/// the store, which is the very thing that made the window a lock round-trip.
/// </para>
/// <para>
/// THREADING: written from the run pool (both brackets), read from the UI thread (chat activation and the
/// marshaled <c>RunChanged</c> handler). Implementations must therefore be thread-safe AND must never make
/// the UI thread wait — activation never blocking is the entire point.
/// </para>
/// <para>
/// FAILURE DIRECTION: a stale entry means a permanently dead composer (unrecoverable — re-activating a live
/// session does not re-seed the flag), a missing entry only means the pre-existing race, which is bounded by
/// the store-level merge. So every implementation and every caller must bias toward MISSING: register
/// narrowly, release from every path that could be last. Nothing here may throw — this is bookkeeping and a
/// fault must never fail a run.
/// </para>
/// </summary>
public interface IExecutingRunStore
{
    /// <summary>Opens <paramref name="runId"/>'s bracket on <paramref name="chatId"/>. Idempotent.</summary>
    void Register(Guid chatId, Guid runId);

    /// <summary>
    /// Closes <paramref name="runId"/>'s bracket. Idempotent and reverse-lookup capable on purpose: the
    /// <c>RunChanged</c> event carries no chat id, and it fires BEFORE the launcher's <c>finally</c> releases,
    /// so both sides call this and whichever runs first wins.
    /// </summary>
    void Release(Guid runId);

    /// <summary>True while ANY run has an open bracket on <paramref name="chatId"/>.</summary>
    bool IsExecuting(Guid chatId);

    /// <summary>True while ANY run has an open bracket at all.</summary>
    bool IsAnyExecuting { get; }

    /// <summary><see cref="IsAnyExecuting"/> ignoring <paramref name="runId"/> — for a terminal
    /// <c>RunChanged</c> handler, whose own bracket is still open when the event fires.</summary>
    bool IsAnyExecutingExcept(Guid runId);

    /// <summary>
    /// The chat <paramref name="runId"/> is bracketed on, or null when its bracket is closed. Lets a
    /// chat-id-less <c>RunChanged</c> handler work out which session the event can speak for.
    /// </summary>
    Guid? GetChatId(Guid runId);
}
