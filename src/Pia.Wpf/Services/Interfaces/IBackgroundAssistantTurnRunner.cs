using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// One headless assistant turn to run with no window / no UI thread. The prompt is
/// treated as the user turn; the produced answer is saved as a new assistant chat.
/// </summary>
public sealed record BackgroundTurnRequest
{
    /// <summary>The assistant-crafted user turn text to execute.</summary>
    public required string Prompt { get; init; }

    /// <summary>The provider to run against (resolved by the caller).</summary>
    public required AiProvider Provider { get; init; }

    /// <summary>
    /// Write-tool names this turn is allowed to execute. Reads are always allowed;
    /// any write tool not in this set is denied (reads default-allow / writes default-deny).
    /// </summary>
    public IReadOnlyCollection<string> GrantedWriteTools { get; init; } = [];

    /// <summary>Optional initial chat title; when null the runner auto-titles / derives one.</summary>
    public string? Title { get; init; }

    /// <summary>Run provenance (defaults to <see cref="AgentRunTrigger.User"/>); NOT the persist discriminator (§16 R14).</summary>
    public AgentRunTrigger Trigger { get; init; } = AgentRunTrigger.User;

    /// <summary>Correlating entity for the trigger, e.g. the <c>ScheduledJob.Id</c> when scheduled.</summary>
    public Guid? TriggerRef { get; init; }

    /// <summary>Owner device for this run (mirrors <c>ScheduledJob.OwnerDeviceId</c>).</summary>
    public Guid? OwnerDeviceId { get; init; }
}

/// <summary>Outcome of a background turn. <see cref="ChatId"/> is allocated even on failure.</summary>
public sealed record BackgroundTurnResult(Guid ChatId, bool Succeeded, string? Error);

/// <summary>
/// Runs a single full assistant turn off-thread (no window, no action-card UI) under a
/// per-run tool-permission policy, and persists the result as a normal assistant chat.
/// Reusable infrastructure for any background-assistant feature (scheduled jobs today).
/// </summary>
public interface IBackgroundAssistantTurnRunner
{
    Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct);
}
