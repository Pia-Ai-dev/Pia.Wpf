using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.Models;

public partial class PiiKeywordEntry : ObservableObject
{
    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private string _category = "Custom";
}
