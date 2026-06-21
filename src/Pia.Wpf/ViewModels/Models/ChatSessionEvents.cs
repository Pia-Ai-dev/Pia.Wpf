using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>
/// The fully-resolved inputs for a single assistant turn. The manager prepares
/// these (persona/provider resolution, prompt composition) on the UI thread and
/// hands them to <see cref="ChatSession.RunTurnAsync"/> so the session does not
/// need the content-producing scoped collaborators itself.
/// </summary>
public sealed class ChatTurnRequest
{
    /// <summary>The user message already added to the session's <c>Messages</c>.</summary>
    public required AssistantMessage UserMessage { get; init; }

    /// <summary>The streaming assistant message already added to the session's <c>Messages</c>.</summary>
    public required AssistantMessage AssistantMessage { get; init; }

    /// <summary>Resolved provider for this turn (already cloned for reasoning-effort overrides).</summary>
    public required AiProvider Provider { get; init; }

    /// <summary>The composed system prompt + tool set + flags for this turn.</summary>
    public required AssistantTurnSetup TurnSetup { get; init; }

    /// <summary>@-commands parsed from the user input (used to strip commands from the AI-visible user message).</summary>
    public required IReadOnlyList<AtCommand> AtCommands { get; init; }

    /// <summary>Whether PII tokenization is active for this turn.</summary>
    public required bool TokenizationEnabled { get; init; }
}

/// <summary>Raised by <see cref="ChatSession"/> when its <see cref="ChatState"/> changes.</summary>
public sealed class ChatStateChangedEventArgs : EventArgs
{
    public required ChatState OldState { get; init; }
    public required ChatState NewState { get; init; }
}

/// <summary>Re-raised by the manager when a session it owns changes state.</summary>
public sealed class SessionStateChangedEventArgs : EventArgs
{
    public required Guid? ChatId { get; init; }
    public required ChatState OldState { get; init; }
    public required ChatState NewState { get; init; }
    public required bool IsActive { get; init; }
}

/// <summary>Re-raised by the manager when a session it owns gets a new title.</summary>
public sealed class SessionTitleChangedEventArgs : EventArgs
{
    public required ChatSession Session { get; init; }
    public required string? Title { get; init; }
    public required bool IsActive { get; init; }
}

/// <summary>Raised when a turn reaches any terminal state.</summary>
public sealed class TurnCompletedEventArgs : EventArgs
{
    /// <summary>
    /// True when the turn streamed to completion without an exception (matches today's
    /// "end of try" reach). Cancelled / errored / empty turns are false — used to gate
    /// follow-up generation exactly as before.
    /// </summary>
    public required bool Succeeded { get; init; }
}

/// <summary>Raised when an accepted write-action tool call succeeded.</summary>
public sealed class ToolSucceededEventArgs : EventArgs
{
    public required string SuccessTitle { get; init; }
    public required string Description { get; init; }
}

/// <summary>Raised when a turn ends in a handled error (not cancellation).</summary>
public sealed class RunFailedEventArgs : EventArgs
{
    public required RunFailureKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }

    /// <summary>For a vision rejection, the user text to restore into the composer (active VM only).</summary>
    public string? RestoreInputText { get; init; }
}

/// <summary>Distinguishes the snackbar appearance/duration the active VM should use.</summary>
public enum RunFailureKind
{
    Timeout,
    Truncated,
    VisionRejected,
    Generic,
    Empty,
}
