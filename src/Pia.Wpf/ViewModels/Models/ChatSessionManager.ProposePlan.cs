using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

public sealed partial class ChatSessionManager
{
    internal const string ProposePlanCreatedResult =
        "The plan is shown to the user under your message, waiting for their approval. Your turn ends here.";

    internal const string ProposePlanJobExistsResult =
        "This chat already has a job that is not finished. Do not propose another plan: tell the user to change "
        + "or finish that job first, or wait until it is done.";

    internal const string ProposePlanSaveFailedResult =
        "The plan could not be saved. Answer the user in text instead.";

    private async Task<bool> IsUnifiedFlowOnAsync()
    {
        try { return (await _settingsService.GetSettingsAsync()).UnifiedAssistantFlowEnabled; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the unified-flow setting failed; using the lever flow");
            return false;
        }
    }

    private async Task<AssistantTurnSetup> WithProposePlanAsync(
        AssistantTurnSetup setup, bool unifiedFlow, CancellationToken ct)
    {
        if (!unifiedFlow || setup.Tools is null)
            return setup;

        var tools = setup.Tools.Where(t => t.Name != "suggest_agent_mode").ToList();
        tools.Add(ProposePlanTool.Build());
        var roster = await TryGetRosterAsync(ct);
        return setup with { Tools = tools, SystemPrompt = setup.SystemPrompt + ProposePlanTool.BuildPrompt(roster) };
    }

    private Func<AssistantMessage, IDictionary<string, object?>?, Task<ProposePlanOutcome>>? ProposePlanHandlerFor(
        ChatSession session, Persona persona, AssistantTurnSetup setup) =>
        setup.Tools?.Any(t => t.Name == ProposePlanTool.Name) == true
            ? (message, arguments) => ProposePlanAsync(session, persona, message, arguments)
            : null;

    internal async Task<ProposePlanOutcome> ProposePlanAsync(
        ChatSession session, Persona persona, AssistantMessage proposing, IDictionary<string, object?>? arguments)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var maxSteps = RunProfile.FromBudget(
            settings.AgentMaxSteps, settings.AgentMaxReplans, settings.AgentWallClockMinutes).MaxSteps;
        var (plan, error) = ProposePlanTool.Read(arguments, maxSteps);
        if (plan is null)
        {
            _logger.LogInformation("Chat {ChatId}: propose_plan rejected (invalid plan)", session.Id);
            _logger.SensitiveDebug("propose_plan rejection: {Reason}", error);
            return new ProposePlanOutcome(error!, EndsTurn: false);
        }

        if (session.Id is not { } chatId || await HasUnfinishedJobAsync(chatId))
        {
            _logger.LogInformation("Chat {ChatId}: propose_plan refused, the chat already has an unfinished job", session.Id);
            return new ProposePlanOutcome(ProposePlanJobExistsResult, EndsTurn: false);
        }

        var steps = AgentPlanner.BuildSteps(plan.Steps, await TryGetRosterAsync(CancellationToken.None), _logger);

        AgentRun? run = null;
        try
        {
            // The run row references the chat row.
            await PersistAsync(session);

            var policy = RunAutonomyPolicy.FromSettings(settings);
            string policyJson;
            try { policyJson = HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.User, policy, chatJob: true); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize the proposed plan's grant envelope");
                policyJson = HeadlessRunLauncher.ChatJobEmptyEnvelopeJson;
            }

            run = await Task.Run(() => _agentRunService.CreateAsync(new AgentRunCreateRequest(
                chatId, RunShape.Planned, AgentRunTrigger.User, Goal: plan.Goal, PolicyJson: policyJson,
                PersonaId: persona.Id)));
            var runId = run.Id;
            await Task.Run(async () =>
            {
                await _agentRunService.ReplaceStepsAsync(runId, steps);
                await _agentRunService.SetRunMessageRangeAsync(runId, proposing.Id, proposing.Id);
            });
            // Attached before the park so the park event finds the session holding the run.
            session.SetActiveRun(runId);
            await Task.Run(() => _agentRunService.PauseAsync(runId, AgentRunOrchestrator.PlanApprovalReason));
            session.SetPlanApprovalParkActive(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat {ChatId}: creating the proposed job failed", chatId);
            if (run is { } created)
            {
                if (session.ActiveRunId == created.Id)
                    session.SetActiveRun(null);
                // An orphan left unfinished would refuse every later plan in this chat.
                try { await Task.Run(() => _agentRunService.FailAsync(created.Id, FailureReasonText.ProposePlanCreateFailure)); }
                catch (Exception failEx) { _logger.LogWarning(failEx, "Run {RunId}: settling the failed proposal failed", created.Id); }
            }
            return new ProposePlanOutcome(ProposePlanSaveFailedResult, EndsTurn: false);
        }

        _logger.LogInformation("Chat {ChatId}: proposed job {RunId} with {StepCount} step(s) waits for approval",
            chatId, run.Id, steps.Count);
        _logger.SensitiveDebug("Proposed job {RunId} goal: {Goal}", run.Id, plan.Goal);
        return new ProposePlanOutcome(ProposePlanCreatedResult, EndsTurn: true);
    }

    private async Task<bool> HasUnfinishedJobAsync(Guid chatId)
    {
        var runs = await Task.Run(() => _agentRunService.GetByChatAsync(chatId));
        return runs.Any(r => r.ParentRunId is null && r.RunShape == RunShape.Planned
            && r.State is not (AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled));
    }

    private async Task<IReadOnlyList<Persona>> TryGetRosterAsync(CancellationToken ct)
    {
        if (_stepPersonas is null)
            return [];
        try { return await _stepPersonas().GetRosterAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Plan persona roster could not be resolved ({Error})", ex.GetType().Name);
            return [];
        }
    }
}
