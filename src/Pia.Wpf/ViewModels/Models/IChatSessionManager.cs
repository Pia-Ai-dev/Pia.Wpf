using Pia.Models;

namespace Pia.ViewModels.Models;

/// <summary>
/// Owns the live <see cref="ChatSession"/> instances for one assistant window,
/// designates the active one, drives turns, persists via the chat store, and
/// re-raises per-session state/title changes. Scoped per assistant window so it
/// can inject the scoped run-loop collaborators the sessions need.
/// </summary>
public interface IChatSessionManager
{
    /// <summary>The session the visible view model currently mirrors.</summary>
    ChatSession? ActiveSession { get; }

    /// <summary>All sessions held this app-run (live or recently finished).</summary>
    IReadOnlyCollection<ChatSession> LiveSessions { get; }

    /// <summary>Raised when the active session changes (after the swap).</summary>
    event EventHandler<ChatSession?>? ActiveChanged;

    /// <summary>Raised when any owned session changes state.</summary>
    event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;

    /// <summary>Raised when any owned session gets a new title.</summary>
    event EventHandler<SessionTitleChangedEventArgs>? SessionTitleChanged;

    /// <summary>Create a fresh active session for a new/cleared chat.</summary>
    ChatSession GetOrCreateActiveForNewChat();

    /// <summary>Returns the live session for <paramref name="chatId"/>, or null if not live.</summary>
    ChatSession? TryGetLive(Guid chatId);

    /// <summary>Carry a title set outside a turn — a rename in the history view — into the live session,
    /// so the next persist does not write the old one back.</summary>
    void ApplyExternalTitle(Guid chatId, string title);

    /// <summary>Resume a chat: activate the live session if present, otherwise hydrate from the store.</summary>
    Task<ChatSession?> ActivateAsync(Guid chatId);

    /// <summary>Designate <paramref name="session"/> as active (clears Completed → Idle).</summary>
    void SetActive(ChatSession session);

    /// <summary>
    /// Prepare and start a turn for <paramref name="session"/> with the given user input.
    /// Resolves persona/provider/prompt, adds the user + assistant messages, and runs the loop.
    /// <paramref name="regenerationInstruction"/> (optional) is injected AI-side for a styled
    /// regeneration (e.g. "make it shorter") without changing the displayed user bubble, and
    /// <paramref name="attachedFileContext"/> (optional) the same way for attached file text.
    /// <paramref name="attachedFiles"/> (optional) is the displayable counterpart of that text — the
    /// names shown as chips under the user bubble, and the only part of an attachment that persists.
    /// <para>
    /// If <paramref name="session"/>'s attached run is parked asking the user a question (<c>needs-goal</c> /
    /// <c>needs-input</c>), this posts <paramref name="userText"/> as the answer and resumes that run instead
    /// of starting a turn.
    /// </para>
    /// </summary>
    /// <returns>False only when the send was REFUSED without consuming anything (a plan-approval park is
    /// pending), so the caller owns restoring its composer. Every other outcome, a failed setup included,
    /// is true.</returns>
    Task<bool> StartTurnAsync(
        ChatSession session, string userText, ImageAttachment? attachment, string? regenerationInstruction = null,
        bool planned = false, string? attachedFileContext = null,
        IReadOnlyList<AttachedFileRef>? attachedFiles = null);

    /// <summary>
    /// Detach the goal as an unattended headless Planned run (no live session). Additive to
    /// <see cref="StartTurnAsync"/> — the interactive path is untouched (G-6).
    /// <paramref name="workingSubpath"/> narrows the run's workspace source root exactly as the live
    /// turn path does; null provisions from the whole assistant files folder.
    /// </summary>
    Task StartBackgroundRunAsync(string goal, string? workingSubpath = null);

    /// <summary>Live state for <paramref name="chatId"/>, or <see cref="ChatState.Idle"/> if not live.</summary>
    ChatState GetState(Guid chatId);

    /// <summary>True while any owned session is mid-turn. Read on the UI thread only.</summary>
    bool IsAnyStreaming { get; }

    /// <summary>Persist the given session to the chat store (no-op when it has no messages).</summary>
    Task PersistAsync(ChatSession session);
}
