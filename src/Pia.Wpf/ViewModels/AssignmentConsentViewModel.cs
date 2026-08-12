using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>Reads record metadata only: the content is resolved by the orchestrator, after a receipt exists.</summary>
public sealed partial class AssignmentConsentViewModel : ObservableObject
{
    private readonly IAssignmentScopeResolver _scope;
    private readonly IAssignmentConsentStore _consent;
    private readonly IAssignmentRunOrchestrator _orchestrator;
    private readonly ILocalizationService _localization;
    private readonly ILogger<AssignmentConsentViewModel> _logger;

    private int _recordLoadGeneration;
    private bool _recordLoadFailed;

    public AssignmentConsentViewModel(
        IAssignmentScopeResolver scope,
        IAssignmentConsentStore consent,
        IAssignmentRunOrchestrator orchestrator,
        ILocalizationService localization,
        ILogger<AssignmentConsentViewModel> logger)
    {
        _scope = scope;
        _consent = consent;
        _orchestrator = orchestrator;
        _localization = localization;
        _logger = logger;
    }

    public ObservableCollection<AssignmentSkill> Skills { get; } = [];

    public ObservableCollection<AssignmentScopeItemViewModel> Records { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(OffersRecords))]
    [NotifyPropertyChangedFor(nameof(IsPromptOnly))]
    private AssignmentSkill? _selectedSkill;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private string _prompt = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private bool _isAffirmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(HasNoOfferableRecords))]
    [NotifyPropertyChangedFor(nameof(RecordsUnavailable))]
    private bool _isLoadingRecords;

    /// <summary>Set once the send path is entered, so a second click cannot start a second run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private bool _hasSent;

    [ObservableProperty]
    private string _capNotice = string.Empty;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    /// <summary>The record listing runs off a skill change, so a test can await what the picker started.</summary>
    internal Task PendingRecordLoad { get; private set; } = Task.CompletedTask;

    public bool ShowSkillPicker => Skills.Count > 1;

    public bool OffersRecords => SelectedSkill is { DeclaredInputTypes.Count: > 0 };

    public bool IsPromptOnly => SelectedSkill is { DeclaredInputTypes.Count: 0 };

    /// <summary>"You have no records" is a claim about the user's data, so a read that failed makes it instead
    /// of answering it.</summary>
    public bool HasNoOfferableRecords =>
        OffersRecords && !IsLoadingRecords && !_recordLoadFailed && Records.Count == 0;

    public bool RecordsUnavailable => OffersRecords && !IsLoadingRecords && _recordLoadFailed;

    public int PromptMaxLength => AssignmentInput.MaxPromptChars;

    public int SelectedCount => Records.Count(r => r.IsSelected);

    public int SelectedChars => Records.Where(r => r.IsSelected).Sum(r => r.CharCount);

    public string SelectionSummary => _localization.Format(
        "AssignmentConsent_Selection_Summary",
        SelectedCount, AssignmentInput.MaxItems, SelectedChars, AssignmentInput.MaxTotalItemChars);

    public bool CanSend =>
        !HasSent
        && !IsLoadingRecords
        && SelectedSkill is not null
        && IsAffirmed
        && !string.IsNullOrWhiteSpace(Prompt)
        && Prompt.Length <= AssignmentInput.MaxPromptChars
        && SelectedCount <= AssignmentInput.MaxItems
        && SelectedChars <= AssignmentInput.MaxTotalItemChars;

    public async Task InitializeAsync(
        AssignmentSurface surface, string? prefillPrompt = null, CancellationToken ct = default)
    {
        Skills.Clear();
        foreach (var skill in surface.Skills) Skills.Add(skill);
        OnPropertyChanged(nameof(ShowSkillPicker));

        if (!string.IsNullOrWhiteSpace(prefillPrompt))
        {
            Prompt = prefillPrompt.Length <= AssignmentInput.MaxPromptChars
                ? prefillPrompt
                : prefillPrompt[..AssignmentInput.MaxPromptChars];
        }

        SelectedSkill = Skills.FirstOrDefault();
        await PendingRecordLoad.WaitAsync(ct);
    }

    /// <summary>Re-checks the gate the primary button binds to, so a caller that skipped the dialog cannot
    /// reach the send.</summary>
    public async Task<AssignmentStartStatus> SendAsync(CancellationToken ct = default)
    {
        if (!CanSend)
        {
            ResultMessage = _localization[StartResultKey(AssignmentStartStatus.ConsentMissing)];
            return AssignmentStartStatus.ConsentMissing;
        }

        var skill = SelectedSkill!;
        var items = Records.Where(r => r.IsSelected).Select(r => r.Item).ToList();
        HasSent = true;

        try
        {
            var receipt = await _consent.RecordAsync(skill.Name, skill.Mode, items, ct);
            var outcome = await _orchestrator.StartAsync(
                new AssignmentRequest(skill.Name, Prompt.Trim(), items), receipt, ct);

            ResultMessage = _localization[StartResultKey(outcome.Status)];
            _logger.LogInformation(
                "An assignment on '{Skill}' with {ItemCount} record(s) ended as {Status}.",
                skill.Name, items.Count, outcome.Status);
            return outcome.Status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start an assignment on '{Skill}'.", skill.Name);
            ResultMessage = _localization["AssignmentConsent_Result_Error"];
            return AssignmentStartStatus.Refused;
        }
    }

    internal static string StartResultKey(AssignmentStartStatus status) => status switch
    {
        AssignmentStartStatus.Started => "AssignmentConsent_Result_Started",
        AssignmentStartStatus.ConsentMissing => "AssignmentConsent_Result_ConsentMissing",
        AssignmentStartStatus.TooLarge => "AssignmentConsent_Result_TooLarge",
        AssignmentStartStatus.Refused => "AssignmentConsent_Result_Refused",
        _ => "AssignmentConsent_Result_Error",
    };

    partial void OnSelectedSkillChanged(AssignmentSkill? value) =>
        PendingRecordLoad = LoadRecordsAsync(value, ++_recordLoadGeneration);

    /// <summary>Keyed on the load, not on the skill: re-picking the same skill mid-load would otherwise let both
    /// loads land and list every record twice.</summary>
    private async Task LoadRecordsAsync(AssignmentSkill? skill, int generation)
    {
        ClearRecords();
        CapNotice = string.Empty;
        _recordLoadFailed = false;

        if (skill is null || skill.DeclaredInputTypes.Count == 0)
        {
            IsLoadingRecords = false;
            RefreshSelection();
            return;
        }

        IsLoadingRecords = true;
        try
        {
            var items = await _scope.ListAsync(skill.DeclaredInputTypes);

            // A newer load owns the list by now, and mixing two vocabularies would offer a record type this
            // skill never declared.
            if (generation != _recordLoadGeneration) return;

            foreach (var item in items)
            {
                var row = new AssignmentScopeItemViewModel(item, _localization, TryAdmit);
                row.PropertyChanged += OnRecordPropertyChanged;
                Records.Add(row);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the records the '{Skill}' skill can be given.", skill.Name);
            if (generation == _recordLoadGeneration) _recordLoadFailed = true;
        }
        finally
        {
            if (generation == _recordLoadGeneration)
            {
                IsLoadingRecords = false;
                RefreshSelection();
            }
        }
    }

    /// <summary>A refusal here is stated, never a silent trim: the user is told which cap stopped the tick.</summary>
    private bool TryAdmit(AssignmentScopeItemViewModel row)
    {
        if (SelectedCount >= AssignmentInput.MaxItems)
        {
            CapNotice = _localization.Format("AssignmentConsent_Cap_TooManyItems", AssignmentInput.MaxItems);
            return false;
        }

        if (SelectedChars + row.CharCount > AssignmentInput.MaxTotalItemChars)
        {
            CapNotice = _localization.Format("AssignmentConsent_Cap_TotalChars", AssignmentInput.MaxTotalItemChars);
            return false;
        }

        CapNotice = string.Empty;
        return true;
    }

    private void ClearRecords()
    {
        foreach (var row in Records) row.PropertyChanged -= OnRecordPropertyChanged;
        Records.Clear();
    }

    private void OnRecordPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssignmentScopeItemViewModel.IsSelected)) RefreshSelection();
    }

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedChars));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasNoOfferableRecords));
        OnPropertyChanged(nameof(RecordsUnavailable));
        OnPropertyChanged(nameof(CanSend));
    }
}
