using System.Linq;

namespace Pia.Services;

/// <summary>
/// 18 D1 layer 1: the cheap, LOCAL pre-flight that refuses a blatant-junk goal before any run is created —
/// no run row, no workspace, no model turn spent. It sits at the composer/launcher boundary, reached from
/// both <c>AssistantViewModel.CanExecuteRunInBackground</c> and <c>ChatSessionManager.StartBackgroundRunAsync</c>,
/// which is why the predicate lives here once rather than being re-derived at each call site.
/// <para>
/// Per 18 spec §10.1, this is deliberately NARROWER than layer 2 (the planner's own emit_plan decline): the
/// test is a CONJUNCTION, not a length threshold alone — refuse only when the trimmed goal has NO WHITESPACE
/// *and* is 8 characters or fewer. Any multi-word goal therefore passes this layer unconditionally, no matter
/// how short ("Fix CI", "Ship it"), because a layer 1 that refuses a real goal is worse than no layer 1 at
/// all: the button goes dead and the user has no recourse, whereas a goal that reaches layer 2 can still be
/// declined WITH a question the model asks and the user can answer. Layer 1 only needs to catch the case
/// nobody could read as an attempt at a goal — a stray "ggg" — and leave everything else to layer 2.
/// </para>
/// </summary>
public static class GoalPreflight
{
    /// <summary>
    /// True when <paramref name="goal"/> is blatant junk layer 1 should refuse. An empty/whitespace-only
    /// goal is NOT this layer's concern — that is a distinct, already-handled case (the composer's own
    /// "requires real text" gate) — so this returns false for it rather than double-refusing.
    /// </summary>
    public static bool IsRefused(string? goal)
    {
        var trimmed = goal?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return false;

        return trimmed.Length <= 8 && !trimmed.Any(char.IsWhiteSpace);
    }
}
