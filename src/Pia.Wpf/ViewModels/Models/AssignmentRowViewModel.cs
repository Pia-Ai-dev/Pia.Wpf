using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;

namespace Pia.ViewModels.Models;

/// <summary>The server's status vocabulary, plus an arm for a value this build does not know.</summary>
public enum AssignmentRowStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    Unknown,
}

/// <summary>One run as the list shows it. Another device's run has no journal entry, so it renders without a
/// prompt and without a chat to open.</summary>
public sealed partial class AssignmentRowViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Action<AssignmentRowViewModel> _openChat;
    private readonly Func<AssignmentRowViewModel, Task> _cancel;

    public AssignmentRowViewModel(
        AssignmentDto row,
        PendingAssignment? journal,
        string skillDisplayName,
        TimeSpan elapsed,
        ILocalizationService localization,
        Action<AssignmentRowViewModel> openChat,
        Func<AssignmentRowViewModel, Task> cancel)
    {
        _localization = localization;
        _openChat = openChat;
        _cancel = cancel;
        Id = row.Id;
        Apply(row, journal, skillDisplayName, elapsed);
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string _skillDisplayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private AssignmentRowStatus _status = AssignmentRowStatus.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasSteps))]
    private int _stepCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedLabel))]
    private TimeSpan _elapsed;

    /// <summary>What the user asked. Sensitive — bound, never logged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrompt))]
    private string _prompt = string.Empty;

    [ObservableProperty]
    private bool _isFromThisDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenChatCommand))]
    private bool _canOpenChat;

    public Guid? ChatId { get; private set; }

    public string StatusLabel => _localization[StatusLabelKey(Status)];

    public bool IsLive => Status is AssignmentRowStatus.Queued or AssignmentRowStatus.Running;

    public bool CanCancel => IsLive;

    public bool HasSteps => StepCount > 0;

    public string StepCountLabel => _localization.Format("Assignments_Steps", StepCount);

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);

    public string ElapsedLabel
    {
        get
        {
            if (Elapsed < TimeSpan.FromMinutes(1)) return _localization["Assignments_Elapsed_UnderAMinute"];
            if (Elapsed < TimeSpan.FromHours(1))
                return _localization.Format("Assignments_Elapsed_Minutes", (int)Elapsed.TotalMinutes);
            return _localization.Format("Assignments_Elapsed_Hours", (int)Elapsed.TotalHours, Elapsed.Minutes);
        }
    }

    internal void Apply(AssignmentDto row, PendingAssignment? journal, string skillDisplayName, TimeSpan elapsed)
    {
        SkillDisplayName = skillDisplayName;
        Status = ParseStatus(row.Status);
        StepCount = row.StepCount;
        Elapsed = elapsed;
        IsFromThisDevice = journal is not null;
        Prompt = journal?.Prompt ?? string.Empty;
        ChatId = journal?.ChatId;
        CanOpenChat = journal is { CollectedAtUtc: not null };
    }

    /// <summary>The server owns this vocabulary and may extend it, so an unrecognised value renders neutrally.</summary>
    internal static AssignmentRowStatus ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "queued" => AssignmentRowStatus.Queued,
        "running" => AssignmentRowStatus.Running,
        "completed" => AssignmentRowStatus.Completed,
        "failed" => AssignmentRowStatus.Failed,
        "cancelled" => AssignmentRowStatus.Cancelled,
        _ => AssignmentRowStatus.Unknown,
    };

    internal static string StatusLabelKey(AssignmentRowStatus status) => $"Assignments_Status_{status}";

    [RelayCommand(CanExecute = nameof(CanOpenChat))]
    private void OpenChat() => _openChat(this);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private Task CancelAsync() => _cancel(this);
}
