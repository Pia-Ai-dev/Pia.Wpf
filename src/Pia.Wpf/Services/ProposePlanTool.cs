using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public static class ProposePlanTool
{
    public const string Name = "propose_plan";

    internal const int MinSteps = 2;

    internal const string Description =
        "Propose a multi-step plan to the user. The plan is shown under your message and starts only when the user "
        + "clicks Start; you cannot start it yourself. Use it when the request has several distinct parts, changes "
        + "several files, or needs long research. Do NOT use it when you can answer directly or finish the work with a "
        + "few tool calls in this turn. Write one short lead-in sentence before calling it and do not list the steps "
        + "in your text. A plan has at least two steps and only the steps genuinely needed. Group by logical change, "
        + "not by file: one reason to edit several files is ONE step listing every file in expectedArtifact. A step "
        + "that writes from data it must first read reads and writes in that one step, because a step boundary does "
        + "not carry raw tool results. Name in expectedArtifact exactly the files a step writes, relative to the "
        + "working folder, never rooted and never as alternatives; omit it when the step writes nothing checkable.";

    internal const string PromptGuidance =
        """

        ## Plans
        Answer directly, or do small tool work in this turn, when that finishes the request. When the work has several distinct parts, changes several files or needs long research, call propose_plan instead of doing it all now: write one short lead-in sentence, then call the tool once. The user approves the plan with a click; a message saying "go ahead" does not start it, so point them at the Start button.
        """;

    [Description(Description)]
    private static string Schema(
        [Description("One or two self-contained sentences stating what the plan achieves.")] string goal,
        [Description("The ordered steps, at least two.")] AgentPlanner.PlanStepArg[] steps) => "";

    public static AITool Build() => AIFunctionFactory.Create(Schema, Name, Description);

    internal static string BuildPrompt(IReadOnlyList<Persona> roster)
    {
        var sb = new StringBuilder(PromptGuidance);
        if (roster.Count > 0)
        {
            sb.AppendLine();
            AgentPlanner.AppendRoster(sb, roster);
        }
        return sb.ToString();
    }

    /// <summary>The plan, or a model-facing reason it could not be read or is not valid.</summary>
    public static (ProposedPlan? Plan, string? Error) Read(IDictionary<string, object?>? arguments, int maxSteps)
    {
        Args? args;
        try
        {
            var json = JsonSerializer.Serialize(arguments ?? new Dictionary<string, object?>());
            args = JsonSerializer.Deserialize<Args>(json, AgentPlanner.PlanJson);
        }
        catch (JsonException)
        {
            return (null, "The arguments could not be read. Send goal as a string and steps as an array of objects with title and intent.");
        }

        if (string.IsNullOrWhiteSpace(args?.Goal))
            return (null, "The plan needs a goal.");
        var steps = args.Steps ?? [];
        if (steps.Length < MinSteps)
            return (null, "A plan needs at least two steps. If one step is enough, do the work in this turn instead.");
        if (AgentPlanner.DescribePlanDefect(steps, maxSteps) is { } defect)
            return (null, defect);
        return (new ProposedPlan(args.Goal.Trim(), steps), null);
    }

    private sealed record Args(string? Goal, AgentPlanner.PlanStepArg[]? Steps);
}
