using Microsoft.Extensions.AI;

namespace Pia.Services;

/// <summary>The one composer both step paths use, so the headless and the live instruction cannot drift.</summary>
internal static class AgentStepInstruction
{
    internal const int MaxSeededPerBlock = 6;

    internal const int MaxSeededArtifactChars = 120;

    internal const string ProducedHeader =
        "Deliverables this run has ALREADY produced (do not create any of these again under a different name "
        + "— if one needs changing, write to the same path):";

    internal const string ReservedHeader =
        "Deliverables RESERVED for later steps of this plan (another step produces them; do not produce them "
        + "here):";

    internal const string OwnDeliverableRule =
        "Create no new deliverable other than the one named after 'Expected:' above.";

    internal static string Compose(int ordinal, string intent, string? expectedArtifact,
        string? workspaceRoot, IEnumerable<AITool>? tools, RunContext ctx)
    {
        var instruction = $"Execute step {ordinal + 1}: {intent}.";
        if (!string.IsNullOrEmpty(expectedArtifact))
            instruction += $" Expected: {expectedArtifact}";
        instruction += Block(ProducedHeader, Produced(ctx));
        instruction += Block(ReservedHeader, Reserved(ctx, ordinal));
        if (!string.IsNullOrEmpty(expectedArtifact))
            instruction += " " + OwnDeliverableRule;
        instruction += " " + AgentToolCarryover.ReReadHint + " " + RunScratchFolder.StepHint;
        if (VaultTargetPolicy.StepHintApplies(workspaceRoot, tools))
            instruction += " " + VaultTargetPolicy.StepHint;
        return instruction;
    }

    private static string Block(string header, List<string> entries) =>
        entries.Count == 0 ? string.Empty : $" {header} {string.Join("; ", entries)}.";

    // A failed step's declaration names a file that does not exist, so seeding it would forbid the work the
    // next step still has to do.
    private static List<string> Produced(RunContext ctx) =>
        [.. ctx.CompletedSteps
            .Where(c => c.Succeeded && c.Outcome?.Succeeded != false)
            .Select(c => StepOutcomeStore.Clamp(c.Outcome?.ArtifactRef ?? c.ExpectedArtifact, MaxSeededArtifactChars))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(MaxSeededPerBlock)];

    private static List<string> Reserved(RunContext ctx, int ordinal) =>
        [.. ctx.PlannedArtifacts
            .Where(p => p.Ordinal != ordinal)
            .OrderBy(p => p.Ordinal)
            .Select(p => StepOutcomeStore.Clamp(p.Artifact, MaxSeededArtifactChars))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSeededPerBlock)];
}
