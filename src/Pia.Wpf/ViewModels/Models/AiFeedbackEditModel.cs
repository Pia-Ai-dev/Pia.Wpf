using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels.Models;

/// <summary>What the "report this answer" dialog collects before a complaint goes to Pia Cloud.</summary>
public partial class AiFeedbackEditModel : ObservableObject
{
    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    private bool _includeAnswer = true;

    /// <summary>Decides which privacy note the dialog shows: placeholders replace personal data, or the text goes as shown.</summary>
    public bool PiiTokenizationActive { get; }

    public AiFeedbackEditModel(bool piiTokenizationActive)
    {
        PiiTokenizationActive = piiTokenizationActive;
    }
}
