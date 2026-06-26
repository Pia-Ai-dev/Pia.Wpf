namespace Pia.Models.Flow;

/// <summary>Discriminator for <see cref="FlowAction"/>, used for persistence and severity tests.</summary>
public enum FlowActionKind
{
    OpenChat,
    // Dormant: legacy research-history briefing link. The research view was removed; the value is
    // retained only so persisted FlowActionKind ordinals don't shift. Reconstructs to no action.
    OpenBriefing,
    OpenTodo,
    ReminderSnooze,
    ReminderDismiss,
    Invoke,
}

/// <summary>
/// A typed deep-link carried by a Flow item (design §5). Never a flattened string.
/// The id-carrying variants are re-derivable and survive a reload; <see cref="InvokeAction"/>
/// wraps a live delegate and is therefore non-serializable (its item is always Durable = false).
/// </summary>
public abstract record FlowAction(string Label)
{
    public abstract FlowActionKind Kind { get; }

    /// <summary>True when the action can be reconstructed from persisted state (everything except Invoke).</summary>
    public bool IsReDerivable => Kind != FlowActionKind.Invoke;

    /// <summary>The backing entity id for id-carrying actions; null for <see cref="InvokeAction"/>.</summary>
    public virtual Guid? EntityId => null;
}

/// <summary>Open the assistant chat with the given id (via IWindowManagerService.ShowAssistantChat).</summary>
public sealed record OpenChatAction(Guid ChatId, string Label) : FlowAction(Label)
{
    public override FlowActionKind Kind => FlowActionKind.OpenChat;
    public override Guid? EntityId => ChatId;
}

/// <summary>Navigate to the todo board, focusing the given todo.</summary>
public sealed record OpenTodoAction(Guid TodoId, string Label) : FlowAction(Label)
{
    public override FlowActionKind Kind => FlowActionKind.OpenTodo;
    public override Guid? EntityId => TodoId;
}

/// <summary>Snooze the given reminder (via IReminderService.SnoozeAsync).</summary>
public sealed record ReminderSnoozeAction(Guid ReminderId, string Label) : FlowAction(Label)
{
    public override FlowActionKind Kind => FlowActionKind.ReminderSnooze;
    public override Guid? EntityId => ReminderId;
}

/// <summary>Dismiss the given reminder (via IReminderService.DismissAsync).</summary>
public sealed record ReminderDismissAction(Guid ReminderId, string Label) : FlowAction(Label)
{
    public override FlowActionKind Kind => FlowActionKind.ReminderDismiss;
    public override Guid? EntityId => ReminderId;
}

/// <summary>
/// Preserves the legacy snackbar <c>onAction</c> callback. Wraps a live delegate, so an item
/// whose action is <see cref="InvokeAction"/> is always Durable = false and never written to disk.
/// </summary>
public sealed record InvokeAction(Action Callback, string Label) : FlowAction(Label)
{
    public override FlowActionKind Kind => FlowActionKind.Invoke;
}
