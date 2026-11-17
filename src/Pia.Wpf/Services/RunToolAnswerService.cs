using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class RunToolAnswerService : IRunToolAnswerService
{
    private readonly IAgentRunService _runService;
    private readonly IAgentRunResumeService _resume;
    private readonly IToolPermissionService _permissions;
    private readonly IAgentToolExchangeStore? _exchanges;
    private readonly ILogger<RunToolAnswerService> _logger;

    public RunToolAnswerService(
        IAgentRunService runService, IAgentRunResumeService resume, IToolPermissionService permissions,
        ILogger<RunToolAnswerService> logger, IAgentToolExchangeStore? exchanges = null)
    {
        _runService = runService;
        _resume = resume;
        _permissions = permissions;
        _logger = logger;
        _exchanges = exchanges;
    }

    public async Task<bool> AnswerParkedToolAsync(Guid runId, ToolDecision decision, CancellationToken ct = default)
    {
        var run = await _runService.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.ParentRunId is not null || !run.IsChatJob()
            || run.State != AgentRunState.WaitingForInput
            || RunPauseEnvelope.ReadReason(run) != AgentRunOrchestrator.ToolApprovalReason
            || RunPauseEnvelope.ReadApprovalTool(run) is not { } tool)
        {
            return false;
        }

        if (decision is ToolDecision.AllowForSession or ToolDecision.AlwaysAllow)
        {
            var parked = _exchanges is null
                ? null
                : await _exchanges.GetParkedCallAsync(runId, tool, ct).ConfigureAwait(false);
            if (parked?.PluginId is { } pluginId)
                await ToolDecisionGrants.ApplyAsync(_permissions, decision, pluginId, tool).ConfigureAwait(false);
            else
                _logger.LogWarning("Run {RunId}: the parked call of {ToolName} names no plugin; allowing it once",
                    runId, tool);
        }

        _logger.LogInformation("Run {RunId}: parked {ToolName} answered on the block ({Decision})", runId, tool, decision);
        return decision == ToolDecision.Decline
            ? await _resume.DeclineAsync(runId, ct).ConfigureAwait(false)
            : await _resume.ResumeAsync(runId, ct: ct).ConfigureAwait(false);
    }
}
