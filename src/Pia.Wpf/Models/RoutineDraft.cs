namespace Pia.Models;

/// <summary>
/// AI-generated draft of a routine's fields from a short description. Any member may be null when the model
/// didn't produce it or produced something unparseable; if the reply is not JSON at all, only
/// <see cref="Goal"/> is set, from the raw text.
/// </summary>
/// <param name="NeedsWebSearch">The goal cannot be answered from the model's own knowledge, so the caller has
/// to append the web-search guard — otherwise a provider that cannot search answers from memory.</param>
/// <param name="Tools">Write tools the goal cannot be carried out without. Proposed, not granted: the caller
/// still drops anything the local catalog does not offer, so an invented name cannot reach a stored grant.</param>
public record RoutineDraft(
    string? Name,
    string? Goal,
    RecurrenceType? Recurrence,
    DayOfWeek? DayOfWeek,
    TimeOnly? TimeOfDay,
    ReasoningEffort? Effort,
    bool NeedsWebSearch,
    IReadOnlyList<string>? Tools = null);

/// <summary>One tool offered to the drafting model, so it picks from what this device actually has rather than
/// from what it remembers a tool being called.</summary>
public sealed record RoutineDraftTool(string Name, string? Description);
