using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels.Models;

/// <summary>What the export dialog collects before an answer is written anywhere.</summary>
public partial class AnswerExportEditModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _fileName;

    /// <summary>Opt-out: the export is nearly always something the user wants to look at straight away.</summary>
    [ObservableProperty]
    private bool _openAfterStorage = true;

    public AnswerExportEditModel(string fileName) => _fileName = fileName;

    public bool CanSave => !string.IsNullOrWhiteSpace(FileName);
}

/// <summary>Which button closed the export dialog.</summary>
public enum AnswerExportDestination
{
    Cancel,
    Vault,
    External,
}
