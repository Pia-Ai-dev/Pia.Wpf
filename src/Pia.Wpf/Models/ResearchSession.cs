using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.Models;

public partial class ResearchSession : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    public string Query { get; }

    public DateTime CreatedAt { get; } = DateTime.Now;

    public DateTime? CompletedAt { get; set; }

    public ObservableCollection<ResearchStep> Steps { get; } = new();

    [ObservableProperty]
    private string _synthesizedResult = string.Empty;

    [ObservableProperty]
    private ResearchStatus _status = ResearchStatus.Pending;

    public ResearchSession(string query)
    {
        Query = query;
    }
}
