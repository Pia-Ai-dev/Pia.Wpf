namespace Pia.Models;

/// <summary>
/// A blueprint's user-facing text resolved for one locale. Resolved once when a card opens rather than
/// held on the record, so the goal a routine stores is frozen in the language it was created in.
/// </summary>
public sealed record RoutineBlueprintText(string Template, IReadOnlyDictionary<string, string?> SlotDefaults)
{
    /// <param name="lookup">Resx lookup — <c>ILocalizationService</c> indexer in the app, a culture-bound
    /// <c>ResourceManager</c> in the tests that check every locale.</param>
    public static RoutineBlueprintText Resolve(RoutineBlueprint blueprint, Func<string, string> lookup)
    {
        // The separator lives here, not at the front of the guard value: a leading space in resx survives only
        // by xml:space and would be silently eaten by any tool that rewrites the file.
        var template = lookup(blueprint.QueryKey);
        if (blueprint.GuardKey is { } guardKey)
            template = $"{template} {lookup(guardKey)}";

        var defaults = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var slot in blueprint.Slots)
            defaults[slot.Name] = slot.DefaultKey is null ? null : lookup(slot.DefaultKey);

        return new RoutineBlueprintText(template, defaults);
    }
}
