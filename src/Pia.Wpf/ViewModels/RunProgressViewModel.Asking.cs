using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public sealed partial class RunProgressViewModel
{
    private readonly IRunAskBroker? _askBroker;
    private readonly IRunToolAnswerService? _toolAnswers;
    private RunToolQuestion? _pendingAsk;

    /// <summary>A step of this chat job is waiting in place for a decision on a tool call.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolAsk))]
    [NotifyCanExecuteChangedFor(nameof(AllowOnceAskCommand))]
    [NotifyCanExecuteChangedFor(nameof(AllowForSessionAskCommand))]
    [NotifyCanExecuteChangedFor(nameof(AlwaysAllowAskCommand))]
    [NotifyCanExecuteChangedFor(nameof(DenyAskCommand))]
    [NotifyPropertyChangedFor(nameof(HasToolActivity))]
    private bool _isAskingInPlace;

    /// <summary>This chat job is parked on a tool call, answered with the card's four choices.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolAsk))]
    [NotifyPropertyChangedFor(nameof(ShowDenyButton))]
    [NotifyPropertyChangedFor(nameof(ShowContinueButton))]
    [NotifyPropertyChangedFor(nameof(ShowNudgeBox))]
    [NotifyCanExecuteChangedFor(nameof(AllowOnceAskCommand))]
    [NotifyCanExecuteChangedFor(nameof(AllowForSessionAskCommand))]
    [NotifyCanExecuteChangedFor(nameof(AlwaysAllowAskCommand))]
    [NotifyCanExecuteChangedFor(nameof(DenyAskCommand))]
    private bool _isParkedToolAsk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AskToolHeading))]
    private string? _askToolName;

    public string? AskToolHeading =>
        AskToolName is null ? null : _localization.Format("RunAsk_Tool_Question", AskToolName);

    /// <summary>User content: rendered on the block, never logged.</summary>
    [ObservableProperty]
    private string? _askToolArguments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNudgeBox))]
    [NotifyPropertyChangedFor(nameof(ShowContinueButton))]
    [NotifyCanExecuteChangedFor(nameof(AnswerUserInputCommand))]
    private bool _showUserInputAsk;

    /// <summary>User content: rendered on the block, never logged.</summary>
    [ObservableProperty]
    private string? _userInputQuestion;

    /// <summary>User content: bound to the answer field, never logged.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnswerUserInputCommand))]
    private string? _userInputAnswer;

    public bool ShowToolAsk => IsAskingInPlace || IsParkedToolAsk;

    partial void OnIsResumingChanged(bool value)
    {
        AllowOnceAskCommand.NotifyCanExecuteChanged();
        AllowForSessionAskCommand.NotifyCanExecuteChanged();
        AlwaysAllowAskCommand.NotifyCanExecuteChanged();
        DenyAskCommand.NotifyCanExecuteChanged();
        AnswerUserInputCommand.NotifyCanExecuteChanged();
    }

    private void AttachAsking()
    {
        if (_askBroker is not null)
            _askBroker.Changed += OnAskChanged;
    }

    private void DetachAsking()
    {
        if (_askBroker is not null)
            _askBroker.Changed -= OnAskChanged;
    }

    private void OnAskChanged(object? sender, Guid runId)
    {
        if (runId == _runId)
            _uiContext.Post(_ => SyncAsk(), null);
    }

    internal void SyncAsk()
    {
        if (_disposed)
            return;
        var wasAsking = IsAskingInPlace;
        _pendingAsk = _askBroker?.GetPending(_runId);
        IsAskingInPlace = _pendingAsk is not null;
        if (wasAsking && !IsAskingInPlace)
            RefreshAsync().SafeFireAndForget(_logger);
        if (_pendingAsk is { } ask)
        {
            AskToolName = ask.ToolName;
            AskToolArguments = ask.DisplayArgs;
            CurrentActivity = _localization.Format("RunAsk_Activity_Waiting", ask.ToolName);
        }
        else if (!IsParkedToolAsk)
        {
            AskToolName = null;
            AskToolArguments = null;
        }

        SyncElapsedTimer();
        SubLine = ComposeSubLine();
    }

    private void ProjectAsking(AgentRun run)
    {
        var chatJob = run.ParentRunId is null && run.IsChatJob();
        var reason = run.State == AgentRunState.WaitingForInput ? RunPauseEnvelope.ReadReason(run) : null;

        IsParkedToolAsk = chatJob && _toolAnswers is not null && ApprovalParkTool(run) is not null;
        if (IsParkedToolAsk)
        {
            AskToolName = ApprovalToolName;
            AskToolArguments = ApprovalToolArguments;
        }

        ShowUserInputAsk = chatJob && reason == AgentRunOrchestrator.NeedsInputReason;
        UserInputQuestion = ShowUserInputAsk ? _askBroker?.GetUserInputQuestion(_runId) : null;

        if (_askBroker is not null)
            SyncAsk();
    }

    private bool CanAnswerToolAsk() => ShowToolAsk && !IsResuming;

    [RelayCommand(CanExecute = nameof(CanAnswerToolAsk))]
    private Task AllowOnceAsk() => AnswerToolAskAsync(ToolDecision.AllowOnce);

    [RelayCommand(CanExecute = nameof(CanAnswerToolAsk))]
    private Task AllowForSessionAsk() => AnswerToolAskAsync(ToolDecision.AllowForSession);

    [RelayCommand(CanExecute = nameof(CanAnswerToolAsk))]
    private Task AlwaysAllowAsk() => AnswerToolAskAsync(ToolDecision.AlwaysAllow);

    [RelayCommand(CanExecute = nameof(CanAnswerToolAsk))]
    private Task DenyAsk() => AnswerToolAskAsync(ToolDecision.Decline);

    internal async Task AnswerToolAskAsync(ToolDecision decision)
    {
        if (_pendingAsk is { } ask && _askBroker is not null)
        {
            if (!_askBroker.Answer(_runId, ask.RequestId, decision))
                _logger.LogInformation("Run {RunId}: the block's answer arrived after the question closed", _runId);
            return;
        }

        if (!IsParkedToolAsk || _toolAnswers is null)
            return;

        IsResuming = true;
        try
        {
            await _toolAnswers.AnswerParkedToolAsync(_runId, decision);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} tool answer failed from the block", _runId);
        }
        finally
        {
            IsResuming = false;
        }
    }

    private bool CanAnswerUserInput() =>
        ShowUserInputAsk && !IsResuming && !string.IsNullOrWhiteSpace(UserInputAnswer);

    [RelayCommand(CanExecute = nameof(CanAnswerUserInput))]
    private async Task AnswerUserInput()
    {
        IsResuming = true;
        try
        {
            if (await _resumeService.ResumeAsync(_runId, UserInputAnswer))
            {
                UserInputAnswer = null;
                _askBroker?.SetUserInputQuestion(_runId, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} answer failed from the block", _runId);
        }
        finally
        {
            IsResuming = false;
        }
    }
}
