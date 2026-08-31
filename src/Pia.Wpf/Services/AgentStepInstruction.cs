using Microsoft.Extensions.AI;

namespace Pia.Services;

/// <summary>The one composer both step paths use, so the headless and the live instruction cannot drift.</summary>
internal static class AgentStepInstruction
{
    internal static string Compose(int ordinal, string intent, string? expectedArtifact,
        string? workspaceRoot, IEnumerable<AITool>? tools)
    {
        var instruction = $"Execute step {ordinal + 1}: {intent}.";
        if (!string.IsNullOrEmpty(expectedArtifact))
            instruction += $" Expected: {expectedArtifact}";
        instruction += " " + AgentToolCarryover.ReReadHint + " " + RunScratchFolder.StepHint;
        if (VaultTargetPolicy.StepHintApplies(workspaceRoot, tools))
            instruction += " " + VaultTargetPolicy.StepHint;
        return instruction;
    }
}
