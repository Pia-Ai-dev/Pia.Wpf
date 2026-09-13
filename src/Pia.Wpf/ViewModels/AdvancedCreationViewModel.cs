using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
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

    private readonly IPersonaService _personas;
    private readonly ISettingsService _settings;

    public AdvancedCreationViewModel(
        IAdvancedCreationService service,
        ILocalizationService localization,
        ILogger<AdvancedCreationViewModel> logger,
        IPersonaService personas,
        ISettingsService settings,
        AdvancedCreationMode mode,
        Guid? providerId,
        string? seed)
    {
        _service = service;
        _localization = localization;
        _logger = logger;
        _personas = personas;
        _settings = settings;
        _session = new AdvancedCreationSession(mode, providerId);
        _opening = seed ?? string.Empty;
        Subject = mode.Subject;

        LoadPersonasAsync().SafeFireAndForget(_logger);
    }

    /// <summary>Which persona the interview runs on — it picks the model, so a persona routed to a private
    /// one keeps the interview there. Offered on the opening card only: once the transcript exists it was
    /// built on one model and cannot be moved to another.</summary>
    public ObservableCollection<Persona> AvailablePersonas { get; } = [];

    [ObservableProperty]
    private Persona? _selectedPersona;

    /// <summary>Opens on the persona this mode would have used anyway, so the picker states what is about to
    /// happen rather than offering a separate "default" row that names no model.</summary>
    private async Task LoadPersonasAsync()
    {
        var settings = await _settings.GetSettingsAsync();
        var personas = await _personas.GetPersonasAsync();
        var active = await _personas.ResolveActiveAsync(
            _session.Mode.ProviderMode, settings.UserOperatingMode ?? UserOperatingMode.Personal);

        foreach (var persona in personas)
            AvailablePersonas.Add(persona);

        SelectedPersona = AvailablePersonas.FirstOrDefault(p => p.Id == active.Id) ?? active;
    }

    public AdvancedCreationSubject Subject { get; }

    /// <summary>The finished draft object, for the caller to hand to its own apply path. Null until done.</summary>
    public string? DraftJson { get; private set; }

    // ---- state --------------------------------------------------------------------------------------

    /// <summary>The single sentence the interview starts from.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(CanPrimary))]
    private string _opening;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOpening), nameof(ShowsQuestions), nameof(ShowsDone))]
    [NotifyPropertyChangedFor(nameof(PrimaryLabel), nameof(SecondaryLabel), nameof(CanPrimary), nameof(CanSecondary))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOpening), nameof(ShowsQuestions), nameof(ShowsDone))]
    [NotifyPropertyChangedFor(nameof(PrimaryLabel), nameof(SecondaryLabel), nameof(CanPrimary), nameof(CanSecondary))]
    private bool _hasStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOpening), nameof(ShowsQuestions), nameof(ShowsDone))]
    [NotifyPropertyChangedFor(nameof(PrimaryLabel), nameof(SecondaryLabel), nameof(CanPrimary), nameof(CanSecondary))]
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

    /// <summary>Kept apart from <see cref="ErrorMessage"/>: that banner offers Retry, which spends a model
    /// turn, and an unreadable dropped file is no reason to re-run one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDropNotice))]
    private string? _dropNotice;

    public ObservableCollection<AdvancedCreationQuestionRow> Questions { get; } = [];

    /// <summary>One entry per turn taken, the last of them current. Grows rather than drawing a total:
    /// the model never says how many turns remain, so a fixed scale would be a guess shown as fact.</summary>
    public ObservableCollection<bool> Dots { get; } = [];

    public bool ShowsOpening => !HasStarted && !IsBusy;

    public bool ShowsQuestions => HasStarted && !IsComplete && !IsBusy;

    public bool ShowsDone => IsComplete && !IsBusy;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasDropNotice => !string.IsNullOrWhiteSpace(DropNotice);

    /// <summary>What Escape has to protect: below this the interview is worth nothing and closing is free.</summary>
    public bool HasProgress => HasStarted && !IsComplete;

    public string Title => _localization[$"AdvancedCreation_Title_{Subject}"];

    public string OpeningLabel => _localization[$"AdvancedCreation_Opening_{Subject}"];

    public string DoneMessage => _localization["AdvancedCreation_Done"];

    /// <summary>The interview has one action row — the panel footer — so its primary carries whichever step
    /// is current rather than sitting disabled behind a second row of its own.</summary>
    public string PrimaryLabel => _localization[
        IsComplete ? "AdvancedCreation_Use" : HasStarted ? "AdvancedCreation_Send" : "AdvancedCreation_Start"];

    /// <summary>Null collapses the footer's secondary button: skipping exists only while questions are open.
    /// Driven by the phase rather than by <see cref="ShowsQuestions"/>, so a turn in flight greys it out
    /// instead of making the row jump.</summary>
    public string? SecondaryLabel => IsAsking ? _localization["AdvancedCreation_Skip"] : null;

    public bool CanPrimary => !IsBusy && (HasStarted || IsComplete || CanStart());

    public bool CanSecondary => IsAsking && !IsBusy;

    private bool IsAsking => HasStarted && !IsComplete;

    // ---- commands -----------------------------------------------------------------------------------

    private bool CanStart() => !string.IsNullOrWhiteSpace(Opening);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync(CancellationToken ct)
    {
        _session.Persona = SelectedPersona;
        return RunAsync(() => _service.StartAsync(_session, Opening, ct));
    }

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
    private void DismissDropNotice() => DropNotice = null;

    /// <summary>A mail or document dropped on a box arrives as its extracted text, the way Optimize takes one.</summary>
    [RelayCommand]
    private Task OpeningFilesDropped(IReadOnlyList<string>? paths) =>
        paths is null ? Task.CompletedTask : ImportIntoAsync(text =>
            Opening = string.IsNullOrWhiteSpace(Opening)
                ? text
                : $"{Opening}{Environment.NewLine}{Environment.NewLine}{text}", paths);

    private async Task ImportIntoAsync(Action<string> apply, IReadOnlyList<string> paths)
    {
        DropNotice = null;
        var text = await DroppedFileImporter.TryImportAsync(
            paths, _logger, snackbarService: null, _localization, onProblem: message => DropNotice = message);

        if (text is not null)
            apply(text);
    }

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
        {
            var row = new AdvancedCreationQuestionRow(question);
            row.FilesDropped = paths => ImportIntoAsync(row.AppendDroppedText, paths);
            Questions.Add(row);
        }

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
