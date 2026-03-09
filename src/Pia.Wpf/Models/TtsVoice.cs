using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.Models;

public partial class TtsVoice : ObservableObject
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Language { get; init; }
    public required string Quality { get; init; }
    public required string Gender { get; init; }
    public required long SizeBytes { get; init; }

    [ObservableProperty]
    private bool _isDownloaded;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private int _downloadProgress;

    [ObservableProperty]
    private bool _isSelected;
}
