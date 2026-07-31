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

    /// <summary>
    /// The roster persona this step runs as (Batch 07 G6), or null for the run persona — which is the common
    /// case and the only case until a roster is configured. Written by <c>AgentPlanner</c> from the plan's
    /// <c>personaKey</c> and consumed by both executors through <c>StepPersonaResolver</c>. An id here is a
    /// REQUEST, not a guarantee: it is honoured only while it is still on the roster and still resolves, and it
    /// degrades to the run persona rather than failing the step.
    /// </summary>
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

    /// <summary>
    /// Free-form per-step JSON. Since Batch 07 G6 the planner writes <c>{"parallelGroup":N}</c> here when the
    /// plan declares steps independent of one another, and since G10 the loop ACTS on it: the document's ONE
    /// consumer is <c>AgentRunOrchestrator.ParallelGroupOf</c>, and a group of two or more still-pending steps is
    /// dispatched as sibling CHILD RUNS on a separate slot pool rather than executed in-process (07 D11). Absence
    /// — and any shape that reader cannot parse — means sequential, which is what makes an unreadable value safe
    /// rather than silent. A second member added here must therefore preserve <c>parallelGroup</c>'s spelling and
    /// semantics, or every fan-out plan quietly becomes sequential again. Deliberately not
    /// <see cref="DependsOnJson"/>: that stays reserved for a real dependency graph, and a group marker is not
    /// one.
    /// </summary>
    public string? ExtraJson { get; set; }
}
