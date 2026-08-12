using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;

namespace Pia.ViewModels.Models;

public sealed class AssignmentScopeItemViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Func<AssignmentScopeItemViewModel, bool>? _tryAdmit;
    private bool _isSelected;

    public AssignmentScopeItemViewModel(
        AssignmentScopeItem item,
        ILocalizationService localization,
        Func<AssignmentScopeItemViewModel, bool>? tryAdmit = null)
    {
        Item = item;
        _localization = localization;
        _tryAdmit = tryAdmit;
    }

    public AssignmentScopeItem Item { get; }

    public string EntityType => Item.EntityType;

    public string TypeLabel => _localization[EntityTypeKey(Item.EntityType)];

    public string Title => Item.Title;

    public int CharCount => Item.CharCount;

    public DateTime? UpdatedAt => Item.UpdatedAt;

    public string SizeLabel => _localization.Format("AssignmentConsent_Record_Chars", Item.CharCount);

    public bool CanSelect => !Item.ExceedsItemCap;

    public bool IsUnsendable => Item.ExceedsItemCap;

    public string UnsendableReason => Item.ExceedsItemCap
        ? _localization.Format("AssignmentConsent_Record_TooLarge", AssignmentInput.MaxItemChars)
        : string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value == _isSelected) return;

            if (value && (!CanSelect || _tryAdmit?.Invoke(this) == false))
            {
                // Notify anyway, or the checkbox keeps the state this setter just refused.
                OnPropertyChanged();
                return;
            }

            SetProperty(ref _isSelected, value);
        }
    }

    internal static string EntityTypeKey(string entityType) => entityType switch
    {
        AssignmentInputEntityTypes.AssistantChat => "AssignmentConsent_EntityType_AssistantChat",
        AssignmentInputEntityTypes.Session => "AssignmentConsent_EntityType_Session",
        AssignmentInputEntityTypes.Memory => "AssignmentConsent_EntityType_Memory",
        AssignmentInputEntityTypes.Todo => "AssignmentConsent_EntityType_Todo",
        AssignmentInputEntityTypes.Template => "AssignmentConsent_EntityType_Template",
        _ => "AssignmentConsent_EntityType_Unknown",
    };
}
