namespace Pia.Models;

public enum RoutineFillErrorKind
{
    /// <summary>A supplied name matches no slot on the blueprint — a typo that must not silently take a default.</summary>
    UnknownSlot,

    /// <summary>A referenced slot has neither a supplied value nor a default.</summary>
    MissingRequiredSlot,

    /// <summary>The template addresses a slot the blueprint does not declare.</summary>
    UnknownPlaceholder
}

/// <summary>One error shape for both consumers: <see cref="SlotName"/> is the field an editor would mark,
/// <see cref="Message"/> is what a tool result tells the model.</summary>
public sealed record RoutineFillError(RoutineFillErrorKind Kind, string SlotName)
{
    /// <summary>English on purpose — a tool result is read by the model, not shown to the user.</summary>
    public string Message => Kind switch
    {
        RoutineFillErrorKind.UnknownSlot =>
            $"'{SlotName}' is not a slot on this blueprint. Call list_routine_blueprints for the slot names; do not invent one.",
        RoutineFillErrorKind.MissingRequiredSlot =>
            $"Slot '{SlotName}' is required and has no value. Ask the user for it, then call again.",
        _ => $"This blueprint's template references '{SlotName}', which it does not declare.",
    };
}

public sealed record RoutineFillResult(string? Query, RoutineFillError? Error)
{
    public bool IsSuccess => Error is null;
}
