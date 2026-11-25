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

    /// <summary>Starts a turn, or answers the attached run's parked question with <paramref name="userText"/>. Attachment
    /// text and a regeneration instruction ride AI-side only; of an attachment, only <paramref name="attachedFiles"/> persists.</summary>
    /// <returns>False only when a pending plan approval refused the send, so the caller restores its composer.</returns>
    Task<bool> StartTurnAsync(
        ChatSession session, string userText, IReadOnlyList<ImageAttachment>? attachments,
        string? regenerationInstruction = null,
        bool planned = false, string? attachedFileContext = null,
        IReadOnlyList<AttachedFileRef>? attachedFiles = null,
        IReadOnlyList<PendingFileAttachment>? files = null);

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
