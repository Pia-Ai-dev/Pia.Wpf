using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// Drives the Advanced Creation overlay: one question card at a time, then a draft. The panel is the
/// only surface — <see cref="IDialogService"/> would replace it in the single-slot overlay host and
/// orphan the task the caller is awaiting, so confirmations and errors live on this view model instead.
/// </summary>
public partial class AdvancedCreationViewModel : ObservableObject
{
    private readonly IAdvancedCreationService _service;
    private readonly ILocalizationService _localization;
    private readonly ILogger<AdvancedCreationViewModel> _logger;
    private readonly AdvancedCreationSession _session;

    public AdvancedCreationViewModel(
        IAdvancedCreationService service,
        ILocalizationService localization,
        ILogger<AdvancedCreationViewModel> logger,
        AdvancedCreationMode mode,
        Guid? providerId,
        string? seed)
    {
        _service = service;
        _localization = localization;
        _logger = logger;
        _session = new AdvancedCreationSession(mode, providerId);
        _opening = seed ?? string.Empty;
        Subject = mode.Subject;
    }

    public AdvancedCreationSubject Subject { get; }

    /// <summary>The finished draft object, for the caller to hand to its own apply path. Null until done.</summary>
    public string? DraftJson { get; private set; }

    // ---- state --------------------------------------------------------------------------------------

    /// <summary>The single sentence the interview starts from.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _opening;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOpening), nameof(ShowsQuestions), nameof(ShowsDone))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOpening), nameof(ShowsQuestions), nameof(ShowsDone))]
    private bool _hasStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOpening), nameof(ShowsQuestions), nameof(ShowsDone))]
    private bool _isComplete;

    /// <summary>One line of what the model understands so far; the header above the current question.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summary;

    /// <summary>Rendered inline. A snackbar would vanish behind the backdrop and a dialog would evict
    /// this panel from the host.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isConfirmingDiscard;

    public ObservableCollection<AdvancedCreationQuestionRow> Questions { get; } = [];

    /// <summary>One entry per turn taken, the last of them current. Grows rather than drawing a total:
    /// the model never says how many turns remain, so a fixed scale would be a guess shown as fact.</summary>
    public ObservableCollection<bool> Dots { get; } = [];

    public bool ShowsOpening => !HasStarted && !IsBusy;

    public bool ShowsQuestions => HasStarted && !IsComplete && !IsBusy;

    public bool ShowsDone => IsComplete && !IsBusy;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>What Escape has to protect: below this the interview is worth nothing and closing is free.</summary>
    public bool HasProgress => HasStarted && !IsComplete;

    public string Title => _localization[$"AdvancedCreation_Title_{Subject}"];

    public string OpeningLabel => _localization[$"AdvancedCreation_Opening_{Subject}"];

    public string DoneMessage => _localization["AdvancedCreation_Done"];

    // ---- commands -----------------------------------------------------------------------------------

    private bool CanStart() => !string.IsNullOrWhiteSpace(Opening);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync(CancellationToken ct) =>
        RunAsync(() => _service.StartAsync(_session, Opening, ct));

    [RelayCommand]
    private Task SendAsync(CancellationToken ct)
    {
        var answers = Questions
            .Where(q => q.HasAnswer)
            .ToDictionary(q => q.Question.Id, q => q.Answer);
        var skipped = Questions
            .Where(q => !q.HasAnswer)
            .Select(q => q.Question.Id)
            .ToList();

        return RunAsync(() => _service.AnswerAsync(_session, answers, skipped, ct));
    }

    /// <summary>Answers nothing and lets the model move on — the same call as Send, with every id skipped.</summary>
    [RelayCommand]
    private Task SkipAsync(CancellationToken ct) =>
        RunAsync(() => _service.AnswerAsync(
            _session,
            new Dictionary<string, string>(),
            [.. Questions.Select(q => q.Question.Id)],
            ct));

    /// <summary>The transcript survived the failed turn, so retrying re-sends it rather than answering
    /// something again. Before the first successful turn there is nothing to re-send, so it restarts.</summary>
    [RelayCommand]
    private Task RetryAsync(CancellationToken ct) =>
        HasStarted
            ? RunAsync(() => _service.RetryTurnAsync(_session, ct))
            : RunAsync(() => _service.StartAsync(_session, Opening, ct));

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void KeepEditing() => IsConfirmingDiscard = false;

    /// <summary>Asks before discarding once there is something to lose; below that Escape just closes.</summary>
    public bool RequestClose()
    {
        if (!HasProgress)
            return true;

        IsConfirmingDiscard = true;
        return false;
    }

    private async Task RunAsync(Func<Task<AdvancedCreationTurn>> call)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var turn = await call();
            Apply(turn);
        }
        catch (OperationCanceledException)
        {
            // The panel is closing; nothing to report to a surface that is going away.
        }
        catch (AdvancedCreationReplyException ex)
        {
            _logger.LogWarning(ex, "Advanced creation reply was unusable for {Subject}", Subject);
            ErrorMessage = _localization["AdvancedCreation_Error_BadReply"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Advanced creation turn failed for {Subject}", Subject);
            ErrorMessage = _localization["AdvancedCreation_Error_Failed"];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(AdvancedCreationTurn turn)
    {
        HasStarted = true;
        Summary = turn.Summary ?? Summary;

        if (turn.IsComplete)
        {
            DraftJson = turn.DraftJson;
            IsComplete = true;
            Questions.Clear();
            MarkDotsSettled();
            return;
        }

        Questions.Clear();
        foreach (var question in turn.Questions)
            Questions.Add(new AdvancedCreationQuestionRow(question));

        MarkDotsSettled();
        Dots.Add(true);
    }

    // Only the last dot is current, so every earlier one is redrawn as settled before a new one lands.
    private void MarkDotsSettled()
    {
        for (var i = 0; i < Dots.Count; i++)
            Dots[i] = false;
    }
}
