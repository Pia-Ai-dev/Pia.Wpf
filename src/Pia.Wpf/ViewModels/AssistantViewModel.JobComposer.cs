using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;

namespace Pia.ViewModels;

public partial class AssistantViewModel
{
    /// <summary>The run writing this chat is a chat job and the unified flow is on, so Send stays open beside it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRunActiveHint))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _chatJobComposerOpen;

    public bool ShowRunActiveHint => ForeignRunActive && !ChatJobComposerOpen;

    private int _chatJobComposerGeneration;

    partial void OnForeignRunActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRunActiveHint));
        RefreshChatJobComposerAsync().SafeFireAndForget(_logger);
    }

    internal async Task RefreshChatJobComposerAsync()
    {
        var generation = ++_chatJobComposerGeneration;
        var runId = _subscribedSession?.ActiveRunId;
        if (!ForeignRunActive || runId is not { } id)
        {
            ChatJobComposerOpen = false;
            return;
        }

        var open = await IsExecutingChatJobAsync(id);
        await _uiDispatcher.PostAsync(() =>
        {
            if (generation == _chatJobComposerGeneration)
                ChatJobComposerOpen = open && ForeignRunActive && _subscribedSession?.ActiveRunId == id;
        });
    }

    private async Task<bool> IsExecutingChatJobAsync(Guid runId)
    {
        try
        {
            if (!(await _settingsService.GetSettingsAsync()).UnifiedAssistantFlowEnabled)
                return false;
            var run = await Task.Run(() => _agentRunService.GetAsync(runId));
            return run is { ParentRunId: null } && AgentRunStates.IsExecuting(run.State) && run.IsChatJob();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading run {RunId} for the composer lock failed", runId);
            return false;
        }
    }
}
