using Pia.Helpers;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class AssistantViewModel
{
    private bool UnifiedFlowOn => !IsChatAgentLeverVisible;

    // With the unified flow a typed message at a waiting plan is an ordinary turn.
    private bool LocksSendAtPlanApproval(bool parkActive) => parkActive && !UnifiedFlowOn;

    private void ConfigureForUnifiedFlow(RunProgressViewModel block)
    {
        block.PlanEditableAtApproval = UnifiedFlowOn;
        if (UnifiedFlowOn)
            ApplyWeakProviderNoteAsync(block).SafeFireAndForget(_logger);
        else
            block.WeakProviderNote = null;
    }

    private async Task ApplyWeakProviderNoteAsync(RunProgressViewModel block)
    {
        var provider = await ResolveActiveProviderAsync();
        var weak = provider is not null
            && await _providerCapabilityService.GetPlanningCapabilityAsync(provider) != PlanningCapability.Capable;
        _uiDispatcher.PostOrRun(() =>
            block.WeakProviderNote = weak ? _localizationService["Agent_WeakProvider_Body"] : null);
    }

    partial void OnIsChatAgentLeverVisibleChanged(bool value)
    {
        PlanApprovalParkActive = LocksSendAtPlanApproval(_subscribedSession?.PlanApprovalParkActive == true);
        if (UnifiedFlowOn && AgentModeEnabled)
        {
            // Under the seed guard so the chat's stored lever is left as it was.
            _isLoadingAgentMode = true;
            try { AgentModeEnabled = false; }
            finally { _isLoadingAgentMode = false; }
        }
        foreach (var block in _earlierBlocks.Values.Append(_runProgress).OfType<RunProgressViewModel>())
            ConfigureForUnifiedFlow(block);
    }
}
