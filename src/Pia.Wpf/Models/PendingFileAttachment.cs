using Wpf.Ui.Controls;

namespace Pia.Models;

public enum PendingFileKind
{
    Text,
    Document,
    Email,
}

public sealed class PendingFileAttachment
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required PendingFileKind Kind { get; init; }
    public required string Text { get; init; }
    public required bool Truncated { get; init; }
    public required int OriginalCharCount { get; init; }

    public SymbolRegular Icon => Kind switch
    {
        PendingFileKind.Email => SymbolRegular.Mail24,
        PendingFileKind.Document => SymbolRegular.Document24,
        _ => SymbolRegular.DocumentText24,
    };
}
