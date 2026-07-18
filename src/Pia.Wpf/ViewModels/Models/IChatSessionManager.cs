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

    /// <summary>Resume a chat: activate the live session if present, otherwise hydrate from the store.</summary>
    Task<ChatSession?> ActivateAsync(Guid chatId);

    /// <summary>Designate <paramref name="session"/> as active (clears Completed → Idle).</summary>
    void SetActive(ChatSession session);

    /// <summary>
    /// Prepare and start a turn for <paramref name="session"/> with the given user input.
    /// Resolves persona/provider/prompt, adds the user + assistant messages, and runs the loop.
    /// <paramref name="regenerationInstruction"/> (optional) is injected AI-side for a styled
    /// regeneration (e.g. "make it shorter") without changing the displayed user bubble.
    /// </summary>
    Task StartTurnAsync(
        ChatSession session, string userText, ImageAttachment? attachment, string? regenerationInstruction = null,
        bool planned = false);

    /// <summary>Live state for <paramref name="chatId"/>, or <see cref="ChatState.Idle"/> if not live.</summary>
    ChatState GetState(Guid chatId);

    /// <summary>Persist the given session to the chat store (no-op when it has no messages).</summary>
    Task PersistAsync(ChatSession session);
}
