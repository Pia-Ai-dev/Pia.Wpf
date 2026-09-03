using System.Text.RegularExpressions;
using Pia.Models;

namespace Pia.Services;

/// <summary>Renders a blueprint's query template against slot values, enforcing the rules that stop a
/// hallucinated or mistyped slot name from quietly producing a job that runs the default every morning.</summary>
public static class RoutineBlueprintFill
{
    private static readonly Regex Placeholder = new(@"\{([^{}]*)\}", RegexOptions.Compiled);

    /// <param name="text">The blueprint's template and defaults in the locale the job is being created in.</param>
    /// <param name="values">Slot name to value. A blank value counts as unsupplied, so an empty string cannot
    /// blank a slot that has a default.</param>
    public static RoutineFillResult ToCreateArgs(
        RoutineBlueprint blueprint,
        RoutineBlueprintText text,
        IReadOnlyDictionary<string, string>? values = null)
    {
        var slots = blueprint.Slots.ToDictionary(s => s.Name, StringComparer.Ordinal);

        if (values is not null)
        {
            foreach (var name in values.Keys)
                if (!slots.ContainsKey(name))
                    return new RoutineFillResult(null, new RoutineFillError(RoutineFillErrorKind.UnknownSlot, name));
        }

        RoutineFillError? error = null;
        var rendered = Placeholder.Replace(text.Template, match =>
        {
            var name = match.Groups[1].Value;
            if (!slots.ContainsKey(name))
            {
                error ??= new RoutineFillError(RoutineFillErrorKind.UnknownPlaceholder, name);
                return match.Value;
            }

            if (values is not null && values.TryGetValue(name, out var supplied) && !string.IsNullOrWhiteSpace(supplied))
                return supplied.Trim();

            if (text.SlotDefaults.TryGetValue(name, out var fallback) && fallback is not null)
                return fallback;

            error ??= new RoutineFillError(RoutineFillErrorKind.MissingRequiredSlot, name);
            return match.Value;
        });

        return error is null ? new RoutineFillResult(rendered, null) : new RoutineFillResult(null, error);
    }

    /// <summary>Every <c>{</c> and <c>}</c> in the template belongs to a placeholder this renderer would
    /// substitute — an unbalanced brace would otherwise reach the model verbatim.</summary>
    public static bool BracesAreAllPlaceholders(string template)
    {
        var matches = Placeholder.Matches(template).Count;
        return template.Count(c => c == '{') == matches && template.Count(c => c == '}') == matches;
    }
}
