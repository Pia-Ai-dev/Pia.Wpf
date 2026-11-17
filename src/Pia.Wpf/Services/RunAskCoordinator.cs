using System.Collections.Concurrent;
using System.Windows;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class RunAskCoordinator : IRunAskBroker
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private sealed record Pending(RunToolQuestion Question, TaskCompletionSource<ToolDecision?> Answer);

    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly ConcurrentDictionary<Guid, string> _userInput = new();
    private readonly IChatAttendance _attendance;
    private readonly IAgentRunService _runService;
    private readonly IFlowService? _flow;
    private readonly ILocalizationService? _localization;
    private readonly ILogger<RunAskCoordinator> _logger;
    private readonly TimeSpan _timeout;

    public RunAskCoordinator(
        IChatAttendance attendance, IAgentRunService runService, ILogger<RunAskCoordinator> logger,
        IFlowService? flow = null, ILocalizationService? localization = null, TimeSpan? timeout = null)
    {
        _attendance = attendance;
        _runService = runService;
        _logger = logger;
        _flow = flow;
        _localization = localization;
        _timeout = timeout ?? DefaultTimeout;
    }

    public event EventHandler<Guid>? Changed;

    public RunToolQuestion? GetPending(Guid runId) =>
        _pending.TryGetValue(runId, out var pending) ? pending.Question : null;

    public async Task<ToolDecision?> AskAsync(RunToolQuestion question, Action<bool>? setClockRunning, CancellationToken ct)
    {
        if (!IsAttended(question.ChatId))
        {
            _logger.LogInformation("Run {RunId}: {ToolName} needs a decision and the chat is not on screen; parking",
                question.RunId, question.ToolName);
            return null;
        }

        var pending = new Pending(question, new TaskCompletionSource<ToolDecision?>(
            TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_pending.TryAdd(question.RunId, pending))
            return null;

        _logger.LogInformation("Run {RunId}: waiting in place for a decision on {ToolName}",
            question.RunId, question.ToolName);
        _runService.SetWorkClockRunning(question.RunId, false);
        setClockRunning?.Invoke(false);
        Changed?.Invoke(this, question.RunId);
        PublishFlowItem(question.RunId);

        try
        {
            using var timeout = new CancellationTokenSource(_timeout);
            using var _ = timeout.Token.Register(() => pending.Answer.TrySetResult(null));
            using var __ = ct.Register(() => pending.Answer.TrySetCanceled(ct));
            var decision = await pending.Answer.Task.ConfigureAwait(false);
            if (decision is null)
                _logger.LogInformation("Run {RunId}: nobody answered in time; parking", question.RunId);
            return decision;
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<Guid, Pending>(question.RunId, pending));
            _runService.SetWorkClockRunning(question.RunId, true);
            setClockRunning?.Invoke(true);
            RetractFlowItem(question.RunId);
            Changed?.Invoke(this, question.RunId);
        }
    }

    public bool Answer(Guid runId, Guid requestId, ToolDecision decision) =>
        _pending.TryGetValue(runId, out var pending)
        && pending.Question.RequestId == requestId
        && pending.Answer.TrySetResult(decision);

    public void SetUserInputQuestion(Guid runId, string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
            _userInput.TryRemove(runId, out _);
        else
            _userInput[runId] = question;
        Changed?.Invoke(this, runId);
    }

    public string? GetUserInputQuestion(Guid runId) => _userInput.TryGetValue(runId, out var q) ? q : null;

    private bool IsAttended(Guid chatId)
    {
        try
        {
            return _attendance.IsAttended(chatId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell whether chat {ChatId} is on screen; treating it as unattended", chatId);
            return false;
        }
    }

    private static string FlowKey(Guid runId) => $"{runId}:ask";

    private void PublishFlowItem(Guid runId)
    {
        if (_flow is null || _localization is null)
            return;
        OnUi(() => _flow.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.ActionRequired,
            Source = FlowSource.AgentRun,
            Title = _localization["Flow_Run_Title"],
            Body = _localization["RunAsk_Flow_Body"],
            DedupKey = FlowKey(runId),
            Lifetime = FlowLifetime.Persistent,
            Action = new OpenParkedRunAction(runId, _localization["Flow_Action_OpenRun"]),
        }));
    }

    private void RetractFlowItem(Guid runId)
    {
        if (_flow is null)
            return;
        OnUi(() => _flow.Retract(FlowKey(runId)));
    }

    private void OnUi(Action action)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.InvokeAsync(action);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow item for a waiting run could not be updated");
        }
    }
}

/// <summary>A chat is attended when it is the one the assistant window shows and that window is up.</summary>
public sealed class WindowChatAttendanceService : IChatAttendance
{
    private readonly IServiceProvider _services;

    public WindowChatAttendanceService(IServiceProvider services) => _services = services;

    public bool IsAttended(Guid chatId)
    {
        var windows = (IWindowManagerService?)_services.GetService(typeof(IWindowManagerService));
        if (windows is null || windows.ActiveAssistantChatId != chatId)
            return false;

        var dispatcher = Application.Current?.Dispatcher;
        bool Shown() => windows.IsVisible(WindowMode.Assistant) && !windows.IsMinimized(WindowMode.Assistant);
        return dispatcher is null || dispatcher.CheckAccess() ? Shown() : dispatcher.Invoke(Shown);
    }
}
