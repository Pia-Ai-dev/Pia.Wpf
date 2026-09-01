using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Pia.Models;

public enum PendingFileKind
{
    Text,
    Document,
    Email,
}

public sealed partial class PendingFileAttachment : ObservableObject
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required PendingFileKind Kind { get; init; }
    public required string Text { get; init; }
    public required bool Truncated { get; init; }
    public required int OriginalCharCount { get; init; }

    /// <summary>Where the file was copied to inside the assistant-files sandbox (relative, forward
    /// slashes), or null while it is staged in memory only. Set by the composer's save action.</summary>
    [ObservableProperty]
    private string? _savedRelativePath;

    public bool IsSaved => SavedRelativePath is not null;

    partial void OnSavedRelativePathChanged(string? value) => OnPropertyChanged(nameof(IsSaved));

    public SymbolRegular Icon => Kind switch
    {
        PendingFileKind.Email => SymbolRegular.Mail24,
        PendingFileKind.Document => SymbolRegular.Document24,
        _ => SymbolRegular.DocumentText24,
    };
}
