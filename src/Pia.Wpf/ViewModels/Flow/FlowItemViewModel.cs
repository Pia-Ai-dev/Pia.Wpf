using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Controls.Cards;
using Pia.Helpers;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Navigation;
using Pia.Services.Flow;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Flow;

/// <summary>
/// Per-item presenter that wraps a single <see cref="FlowItem"/> (design §5). Passes through the
/// display fields and adds the multi-action <see cref="Decisions"/> bar (re-derived from the item's
/// <see cref="FlowSource"/>, never stored). Owns the per-item action logic that previously lived in
/// <c>FlowViewModel.ExecuteItemAction</c>: navigation links, the local-clear ✕, and the async
/// reminder Snooze/Done commands (which gate re-entrancy via <see cref="IsBusy"/> and keep the card
/// on failure). The wrapped item is supplied via <see cref="Bind"/> so the ctor takes only injected
/// interface dependencies (DI/architecture rule) and the same wrapper instance can be rebound during
/// the reconcile without dropping an in-flight decision's <see cref="IsBusy"/>.
/// </summary>
public partial class FlowItemViewModel : ObservableObject
{
    private readonly IFlowService _flow;
    private readonly IReminderService _reminderService;
    private readonly IWindowManagerService _windowManager;
    private readonly INavigationService _navigationService;
    private readonly ILocalizationService _localizationService;
    private readonly IAgentRunResumeService _resumeService;
    private readonly ILogger<FlowItemViewModel> _logger;

    private FlowItem _item = null!;
    private DecisionButton[]? _decisions;

    public FlowItemViewModel(
        IFlowService flow,
        IReminderService reminderService,
        IWindowManagerService windowManager,
        INavigationService navigationService,
        ILocalizationService localizationService,
        IAgentRunResumeService resumeService,
        ILogger<FlowItemViewModel> logger)
    {
        _flow = flow;
        _reminderService = reminderService;
        _windowManager = windowManager;
        _navigationService = navigationService;
        _localizationService = localizationService;
        _resumeService = resumeService;
        _logger = logger;
    }

    /// <summary>The wrapped item (exposed read-only for the reconcile to match by <see cref="FlowItem.Id"/>).</summary>
    public FlowItem Item => _item;

    public string Title => _item.Title;
    public string Body => _item.Body;
    public FlowSource Source => _item.Source;
    public FlowSeverity Severity => _item.Severity;
    public DateTimeOffset CreatedAt => _item.CreatedAt;
    public bool IsRead => _item.IsRead;

    /// <summary>The navigation deep-link (rendered as the accent text-link); null for decision-only cards.</summary>
    public FlowAction? Action => _item.Action;

    /// <summary>
    /// The originating chat state for background-chat cards — feeds the shared <c>PiaChatStateBadge</c>
    /// pill. Re-derived (never stored) from the persisted <see cref="FlowSeverity"/> via
    /// <see cref="FlowSeverityMapper.ToChatState"/>; null for every other source so the badge
    /// self-collapses. Survives reload because severity is persisted.
    /// </summary>
    public ChatState? State => _item.Source == FlowSource.BackgroundChat
        ? FlowSeverityMapper.ToChatState(_item.Severity)
        : null;

    /// <summary>True when the card shows a chat-state chip; the chip then replaces the prose body.</summary>
    public bool HasChatState => State is not null;

    /// <summary>Decision buttons derived from the item's source (design §5); empty for non-reminder sources.</summary>
    public IReadOnlyList<DecisionButton> Decisions => _decisions ??= BuildDecisions();

    /// <summary>True when the card carries decisions — drives hiding the hover-✕ (design §5).</summary>
    public bool HasDecisions => Decisions.Count > 0;

    /// <summary>True while an async decision command runs; gates re-entrancy and disables the buttons.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnoozeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DoneCommand))]
    private bool _isBusy;

    /// <summary>(Re)binds the wrapped item, raising PropertyChanged for every passthrough plus the decision props.</summary>
    public void Bind(FlowItem item)
    {
        _item = item;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(IsRead));
        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(HasChatState));
        OnPropertyChanged(nameof(Decisions));
        OnPropertyChanged(nameof(HasDecisions));
    }

    private DecisionButton[] BuildDecisions()
    {
        if (_item.Source != FlowSource.Reminder)
            return Array.Empty<DecisionButton>();

        return new[]
        {
            new DecisionButton
            {
                Label = _localizationService["Flow_Action_Snooze"],
                Emphasis = DecisionEmphasis.Default,
                Command = SnoozeCommand,
            },
            new DecisionButton
            {
                Label = _localizationService["Flow_Action_Done"],
                Emphasis = DecisionEmphasis.Primary,
                Command = DoneCommand,
            },
        };
    }

    private bool CanDecide => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private Task Snooze()
        => RunReminderDecision("snooze", id => _reminderService.SnoozeAsync(id, TimeSpan.FromMinutes(10)));

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private Task Done()
        => RunReminderDecision("done", id => _reminderService.DismissAsync(id));

    /// <summary>
    /// Runs a reminder decision: gate re-entrancy via <see cref="IsBusy"/>, dismiss the card on
    /// success, and keep it (logging the failure) on error. <paramref name="operation"/> names the
    /// decision for the log only (a constant, not user content).
    /// </summary>
    private async Task RunReminderDecision(string operation, Func<Guid, Task> decide)
    {
        if (!TryGetReminderId(out var reminderId))
            return;

        IsBusy = true;
        try
        {
            await decide(reminderId);
            _flow.Dismiss(_item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow {Operation} failed for item {Id}", operation, _item.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => _flow.Dismiss(_item.Id);

    [RelayCommand]
    private void ExecuteAction()
    {
        if (_item.Action is null)
            return;

        try
        {
            switch (_item.Action)
            {
                case OpenChatAction chat:
                    _windowManager.ShowAssistantChat(chat.ChatId);
                    RetractByKey();
                    break;
                case OpenRunAction run:
                    _windowManager.ShowAgentRun(run.RunId);
                    RetractByKey();
                    break;
                case OpenParkedRunAction parked:
                    // Navigate without retracting: retracting here would delete the only durable trace of a
                    // still-parked run the moment it is merely viewed. Let the state change retract it instead.
                    _windowManager.ShowAgentRun(parked.RunId);
                    _flow.MarkRead(_item.Id);
                    break;
                case ContinueRunAction cont:
                    // Resume out-of-band (no foreground chat to navigate to). CAS in the resume service
                    // makes a double invoke a no-op; drop the WaitingForInput card immediately (the surface
                    // also retracts on the resulting Running/terminal RunChanged — a gone key is a no-op).
                    _resumeService.ResumeAsync(cont.RunId).SafeFireAndForget(_logger);
                    RetractByKey();
                    break;
                case OpenTodoAction:
                    NavigateToTodoBoard();
                    _flow.MarkRead(_item.Id); // the deadline auto-retracts when the todo is completed/out of window
                    break;
                case InvokeAction invoke:
                    invoke.Callback();
                    _flow.Dismiss(_item.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow action failed for item {Id}", _item.Id);
        }
    }

    private bool TryGetReminderId(out Guid reminderId)
        => Guid.TryParse(_item.DedupKey, out reminderId);

    private void RetractByKey()
    {
        if (_item.DedupKey is { } key)
            _flow.Retract(key);
        else
            _flow.Dismiss(_item.Id);
    }

    private void NavigateToTodoBoard()
    {
        try
        {
            _navigationService.NavigateTo<TodoViewModel>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow could not navigate to the todo board");
        }
    }
}
