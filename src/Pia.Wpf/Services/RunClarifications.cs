using System.Text.Json;

namespace Pia.Services;

/// <summary>
/// <b>18 D2 / owner Q3.</b> Reader+writer for the answers a user has given to a run's clarification questions,
/// stored as a JSON array of strings in <c>AgentRuns.ClarificationsJson</c>:
/// <c>["the CI job on main","use the 2025 export"]</c>. Ordered oldest-first, and they ACCUMULATE — 18 D4 puts
/// no cap on how many times a run may park and ask, so the second park must not lose the first answer.
/// <para>
/// <b>Why a dedicated column and not the pause envelope's <c>ExtraJson</c>.</b> Not a preference — forced.
/// <c>TryBeginResumeAsync</c> (<c>AgentRunService.cs:387</c>) and <c>TryResumeFromPauseAsync</c> (<c>:487</c>)
/// both <c>SET ExtraJson=NULL</c> inside the resume claim, deliberately, "so the claim does not retain stale
/// pause state" — so anything kept there is destroyed by the very resume that carries the answer. A dedicated
/// column also keeps USER CONTENT out of <see cref="RunPauseEnvelope"/>, whose doc licenses a consumer to log
/// every member it carries.
/// </para>
/// <para>
/// <b>Why not the run's <c>Goal</c> column.</b> The goal stays exactly what the user typed (Q3): the run panel
/// and <c>ChildRunRowViewModel</c> (<c>RunProgressViewModel.cs:1203</c>) render <c>Goal</c> directly, and folding
/// answers into it would rewrite, in front of the user, the sentence they are looking at to identify their own
/// run. The answers therefore live BESIDE the goal and are joined to it only in the plan prompt
/// (<c>RunContext.AppendClarifications</c>).
/// </para>
/// <para>
/// <b>SENSITIVE.</b> Every element is user-typed content. Log the COUNT freely (app-owned); log the text only
/// through <c>Pia.Logging.LoggingExtensions.SensitiveDebug</c> or a <c>Sensitive*</c> sibling — never as an
/// argument to <c>LogInformation</c>/<c>LogWarning</c>/<c>LogDebug</c>/<c>LogError</c>/<c>LogTrace</c>.
/// </para>
/// <para>
/// Same swallowing discipline as <see cref="RunPauseEnvelope"/> beside it: a malformed, absent or foreign
/// document reads as EMPTY ("this run has no recorded answers"), never a guess and never a throw — a
/// clarification list is context for a prompt, and no read of it is worth failing a resume for.
/// </para>
/// </summary>
internal static class RunClarifications
{
    /// <summary>
    /// Cap on ONE answer, matching <c>RunContext.MaxNudgeChars</c>'s head-kept shape. The same text arrives as
    /// the resume's nudge, which is already capped there, so this is the DURABLE side of the same bound rather
    /// than a second opinion about it: without it a paste of a log file would be persisted whole and then
    /// re-sent on every later plan turn of the run.
    /// </summary>
    internal const int MaxAnswerChars = 1000;

    /// <summary>
    /// Cap on how many answers a run keeps. NOT a cap on how many times a run may ask — 18 D4 forbids that, and
    /// the owner was shown the stall risk and chose no cap. This bounds the PROMPT: past this many answers the
    /// oldest are dropped, because a plan turn that ships forty accumulated replies has stopped grounding the
    /// model and started burying the goal (the reliability argument <c>AgentPlanner.MaxGroundingEntries</c>
    /// makes, applied to a listing). A run may still park and ask an unbounded number of times.
    /// </summary>
    internal const int MaxAnswers = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The recorded answers oldest-first, or EMPTY when the run carries no readable document.</summary>
    internal static IReadOnlyList<string> Read(string? clarificationsJson)
    {
        if (string.IsNullOrWhiteSpace(clarificationsJson))
            return [];

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(clarificationsJson, Json);
            if (parsed is null)
                return [];

            // A blank element is dropped rather than rendered: an empty bullet under "The user has since
            // clarified" reads as a clarification the user did not give.
            return parsed.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Returns the document that <paramref name="clarificationsJson"/> becomes once
    /// <paramref name="answer"/> is appended, or <c>null</c> when there is nothing to append (a blank answer —
    /// the Flow Continue card carries no input at all (§4.3), so a resume with no typed text is the normal
    /// case, not an error). Pure: the caller owns the write, which is what lets the store do the
    /// read-modify-write inside one gate hold.
    /// </summary>
    internal static string? Append(string? clarificationsJson, string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;

        // Flatten CR/LF/TAB → space before storing, exactly as RunContext.SetNudge does and for the same
        // reason: this text is fenced into a prompt later, and a newline in the user's answer must not be able
        // to forge extra prompt lines downstream.
        var flat = answer.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        if (flat.Length > MaxAnswerChars)
            flat = flat[..MaxAnswerChars] + "…";

        var kept = new List<string>(Read(clarificationsJson)) { flat };
        if (kept.Count > MaxAnswers)
            kept.RemoveRange(0, kept.Count - MaxAnswers); // drop the OLDEST — the newest answer is the one just given

        return JsonSerializer.Serialize(kept, Json);
    }
}
