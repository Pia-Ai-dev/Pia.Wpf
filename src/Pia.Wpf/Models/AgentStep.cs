namespace Pia.Models;

/// <summary>
/// A single step of a <see cref="RunShape.Planned"/> run. Distinct from <c>TodoItem</c>
/// (which is binary-status + positional, no deps/substeps/tool-state). No rows are written
/// by 1.1 wiring — the steps API is present but only exercised in 1.2.
/// </summary>
public sealed class AgentStep
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public int Ordinal { get; set; }

    /// <summary>SENSITIVE (user content) — log only via <c>SensitiveDebug</c>.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>SENSITIVE (user content) — what this step should accomplish.</summary>
    public string? Intent { get; set; }

    public AgentStepStatus Status { get; set; }

    public string? ExpectedArtifact { get; set; }

    /// <summary>Reserved for Phase 3 sub-agents; null in Phase 1 (single persona).</summary>
    public Guid? AssignedPersonaId { get; set; }

    /// <summary>Reserved for a DAG; Phase 1 is linear.</summary>
    public string? DependsOnJson { get; set; }

    /// <summary>Idempotency hint for Phase 2 resume.</summary>
    public bool ReRunnable { get; set; } = true;

    /// <summary>Transcript slice by STABLE message Id (never a positional ordinal — §16 R3).</summary>
    public Guid? FirstMessageId { get; set; }

    public Guid? LastMessageId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? ExtraJson { get; set; }
}
