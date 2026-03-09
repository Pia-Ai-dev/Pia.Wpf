using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.Models;

public partial class ResearchStep : ObservableObject
{
    public int StepNumber { get; }

    public string Title { get; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private ResearchStatus _status = ResearchStatus.Pending;

    [ObservableProperty]
    private bool _isStreaming;

    public ResearchStep(int stepNumber, string title)
    {
        StepNumber = stepNumber;
        Title = title;
    }
}
