using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

public sealed partial class RunProgressViewModel
{
    /// <summary>Set by the host when plans may be edited while they wait for approval, not only when paused.</summary>
    [ObservableProperty]
    private bool _planEditableAtApproval;

    /// <summary>The weak-provider warning shown while the plan waits for approval; null shows nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWeakProviderNote))]
    private string? _weakProviderNote;

    public bool ShowWeakProviderNote => WeakProviderNote is not null && IsPlanApprovalPause;

    private bool IsEditableApprovalPark => PlanEditableAtApproval && IsPlanApprovalPause;

    partial void OnPlanEditableAtApprovalChanged(bool value) => NotifyPlanMutability();

    partial void OnIsPlanApprovalPauseChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWeakProviderNote));
        NotifyPlanMutability();
    }

    private void NotifyPlanMutability()
    {
        OnPropertyChanged(nameof(CanMutatePlan));
        EditStepCommand.NotifyCanExecuteChanged();
        SaveStepEditCommand.NotifyCanExecuteChanged();
        InsertStepBelowCommand.NotifyCanExecuteChanged();
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
        SkipStepCommand.NotifyCanExecuteChanged();
    }
}
