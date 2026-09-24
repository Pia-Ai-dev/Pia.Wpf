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
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _isDownloaded;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private int _downloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _isSelected;

    // The saved voice key outlives its files — the Piper→sherpa move deleted the old tree — and
    // TtsService refuses to load a voice that is not on disk, so a picked-but-absent voice speaks
    // nothing and must not claim to be the active one.
    public bool IsActive => IsSelected && IsDownloaded;
}
