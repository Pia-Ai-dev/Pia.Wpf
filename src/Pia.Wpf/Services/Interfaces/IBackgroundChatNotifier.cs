using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// Cross-window (singleton) surface that raises a notification — Windows toast +
/// in-app toast — when a background (non-active) assistant chat changes to a
/// state worth surfacing (WaitingForTool / Completed / Error). The toast carries
/// a link that activates the chat inside the single assistant window.
/// </summary>
public interface IBackgroundChatNotifier
{
    /// <summary>
    /// Surface a background state change. <paramref name="displayTitle"/> is shown
    /// to the user (allowed) but never logged (CLAUDE.md). No-ops for states that
    /// should not notify.
    /// </summary>
    void NotifyStateChange(Guid chatId, string displayTitle, ChatState state);
}
