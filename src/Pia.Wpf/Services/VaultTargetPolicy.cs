using System.IO;
using Microsoft.Extensions.AI;
using Pia.Infrastructure;

namespace Pia.Services;

/// <summary>
/// Names the memory tools a run has to use instead of writing under the vault folder, so the refusal and the
/// step hint cannot come to name different ones.
/// </summary>
internal static class VaultTargetPolicy
{
    internal const string CreateSourceToolName = "create_source";
    internal const string UpdateSourceToolName = "update_source";

    internal const string SourcesPrefix = "sources/";
    private const string GenericReference = SourcesPrefix + "<name>.md";

    /// <summary>What the per-step instruction tells the model. Model-facing, so deliberately unlocalized.</summary>
    internal const string StepHint =
        "The working folder is NOT the user's memory vault and the vault is not part of it: a file you write "
        + "here never reaches the vault, and a '" + AssistantWorkspace.VaultSubfolderName + "/' path in the "
        + "working folder is refused. Put a vault document there with " + CreateSourceToolName + " and an "
        + "explicit vault-relative path under '" + SourcesPrefix + "' — e.g. "
        + CreateSourceToolName + "('" + SourcesPrefix + "<subfolder>/<name>.md', content) — never a subfolder "
        + "you leave implicit, and report that same reference as " + AgentStepTools.EmitStepResultToolName
        + "'s artifact_ref.";

    /// <summary>Offered, not merely described: a run without the memory plugin must not be told to call a tool
    /// it does not have.</summary>
    internal static bool StepHintApplies(string? workspaceRoot, IEnumerable<AITool>? tools) =>
        !string.IsNullOrEmpty(workspaceRoot)
        && tools is not null
        && tools.Any(t => string.Equals(t.Name, CreateSourceToolName, StringComparison.Ordinal));

    /// <summary>A vault reference, not a path: forward slashes and rooted at <c>sources/</c>, which is what resolves.</summary>
    internal static string SuggestedReference(string anchorRoot, string resolvedPath)
    {
        string remainder;
        try
        {
            remainder = Path.GetRelativePath(AssistantWorkspace.VaultRootFor(anchorRoot), resolvedPath)
                .Replace('\\', '/')
                .Trim('/');
        }
        catch
        {
            return GenericReference;
        }

        if (remainder.Length == 0 || remainder is "." or ".." || remainder.StartsWith("../", StringComparison.Ordinal))
            return GenericReference;

        if (remainder.StartsWith(SourcesPrefix, StringComparison.OrdinalIgnoreCase))
            return remainder;

        var leaf = Path.GetFileName(remainder);
        return leaf.Length == 0 ? GenericReference : SourcesPrefix + leaf;
    }

    internal static string WriteRefusal(string anchorRoot, string resolvedPath)
    {
        var folder = AssistantWorkspace.VaultSubfolderName;
        return "Error: this run works in an isolated workspace that does not contain the memory vault, so a file "
            + $"written under '{folder}/' here reaches no vault and is dropped when the run finishes. "
            + $"Call {CreateSourceToolName}('{SuggestedReference(anchorRoot, resolvedPath)}', content) to add a new "
            + $"vault source, or {UpdateSourceToolName}(reference, content) to correct one that already exists. "
            + $"To keep this as a working file instead, write it outside '{folder}/'.";
    }
}
