using Pia.Models;

namespace Pia.Services;

/// <summary>
/// The three subjects an Advanced Creation interview can design. Each preamble and key block is the one
/// the matching one-shot generator in <see cref="TextOptimizationService"/> already uses, so the two
/// doors end in the same draft.
/// </summary>
public static class AdvancedCreationModes
{
    public static AdvancedCreationMode Template() => new(
        AdvancedCreationSubject.Template,
        SubjectPreamble:
            "a text-optimization template: one instruction an assistant will be given together with a "
            + "piece of the user's text, to rewrite that text in a particular style. Ask about the tone, "
            + "the sentence structure, the vocabulary level, and any formatting the style needs. Ask for "
            + "a sample of the kind of text it will be applied to, and for a sample of the result they "
            + "want, whenever seeing one would change the instruction.",
        DraftKeysBlock: """
            - "name": a short display name for the template (max 40 characters)
            - "description": a one-line summary of what this template does (max 120 characters)
            - "prompt": the instruction itself, 2-4 sentences, written in the second person as a command. It must capture the tone, the sentence structure and complexity, the vocabulary level, and any formatting or structural pattern. Write it so it applies to ANY input text, not to one example.
            """,
        ExtraContext: null,
        ProviderMode: WindowMode.Optimize);

    public static AdvancedCreationMode Persona() => new(
        AdvancedCreationSubject.Persona,
        SubjectPreamble:
            "an AI assistant persona: an identity, a voice and a set of constraints the assistant adopts. "
            + "Ask about who it is for, the register it should speak in, what it must never do, and how "
            + "its answers should be shaped. Ask for a sample exchange whenever the voice is hard to pin "
            + "down in the abstract.",
        DraftKeysBlock: """
            - "name": a short display name (max 40 characters)
            - "tagline": a one-line summary (max 120 characters)
            - "systemPrompt": a 2-5 sentence identity/voice instruction written in the second person ("You are…") that fully defines how the assistant should speak and behave
            - "guardrails": one or two sentences of constraints the assistant must respect; use an empty string if none apply
            - "outputFormat": 3-6 short bullet points (each on its own line, starting with "- ") describing how this persona should format its replies; use an empty string if nothing special applies
            - "archetype": exactly one of "assistant", "analyst", "creative", "visionary", "explainer", "custom"
            - "emoji": a single emoji that represents the persona
            - "accentColor": a hex colour like "#7C4DFF"
            - "expertise": an array of up to 6 short domain tags
            """,
        ExtraContext: null,
        ProviderMode: WindowMode.Assistant);

    /// <param name="availableTools">What this device offers. Empty ⇒ the draft is asked for no tools at
    /// all rather than invited to guess.</param>
    public static AdvancedCreationMode Routine(IReadOnlyList<RoutineDraftTool> availableTools)
    {
        var toolSection = availableTools.Count == 0
            ? "- \"tools\": an empty array. This device offers no tools a routine may be granted."
            : "- \"tools\": the tools this routine cannot be carried out without, as an array of names copied "
              + "EXACTLY from the list below. Most routines need none, because reading and reporting needs no "
              + "grant at all — return an empty array unless the goal has to CHANGE something. Never invent a "
              + "name that is not on the list.";

        var toolList = availableTools.Count == 0
            ? null
            : "Tools available on this device that need a grant. Reading and searching — files, notes, chats, "
              + "todos, git history — is always possible and is deliberately not listed:\n"
              + string.Join("\n", availableTools.Select(t => string.IsNullOrWhiteSpace(t.Description)
                  ? $"- {t.Name}"
                  : $"- {t.Name}: {t.Description}"));

        return new AdvancedCreationMode(
            AdvancedCreationSubject.Routine,
            SubjectPreamble:
                "a scheduled routine: one instruction an assistant will carry out on its own, on a schedule, "
                + "with nobody there to answer a follow-up question. Ask about what it should look at, what "
                + "shape the answer takes, how often it runs and when, and — when the task has several parts "
                + "— the order they should happen in. Because nobody is there to correct it, ask for anything "
                + "the instruction would otherwise have to guess.",
            DraftKeysBlock: $"""
                - "name": a short display name for the routine (max 40 characters)
                - "goal": the instruction the assistant will be given every time it runs, written in the second person as a command. Say what to look at, what shape the answer takes, and how long it may be. When the task has more than one part, set the parts out as a numbered list in the order they should happen, one short line each, so the run follows that order instead of working it out again. Be specific rather than brief: nobody is there to fill a gap. At most 1000 characters. Do not tell it to remember anything from a previous run — each run is a fresh conversation with no memory of the last one.
                - "recurrence": exactly one of "once", "daily", "weekly", "monthly", "yearly", "manual"
                - "dayOfWeek": the English weekday name when the recurrence is weekly, otherwise an empty string
                - "timeOfDay": the time of day to run, as "HH:mm" on a 24-hour clock
                - "effort": how much reasoning it needs — exactly one of "minimal", "low", "medium", "high"
                - "needsWebSearch": true when the goal cannot be answered without searching the web, false otherwise
                {toolSection}
                """,
            ExtraContext: toolList,
            ProviderMode: WindowMode.Assistant);
    }
}
