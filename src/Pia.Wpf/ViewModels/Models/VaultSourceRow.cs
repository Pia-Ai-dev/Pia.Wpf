using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels.Models;

/// <summary>
/// One display-ready row of the Vault Overview's source-documents list: the raw file's name, its
/// vault-relative path (row tooltip), formatted size, and the localized ingest-status line the
/// view-model builds (compiled into N topic pages / not yet ingested / not a text file).
/// <see cref="IsIngested"/> drives the status swatch color in XAML; <see cref="IsIngesting"/> is the
/// live "this document is being compiled right now" flag the view-model flips from the ingest
/// scheduler's activity so the row can swap its swatch for a spinner.
/// </summary>
public partial class VaultSourceRow : ObservableObject
{
    public string Name { get; }
    public string RelativePath { get; }
    public bool IsIngested { get; }
    public string StatusText { get; }
    public string SizeText { get; }

    [ObservableProperty]
    private bool _isIngesting;

    public VaultSourceRow(
        string name, string relativePath, bool isIngested, string statusText, string sizeText)
    {
        Name = name;
        RelativePath = relativePath;
        IsIngested = isIngested;
        StatusText = statusText;
        SizeText = sizeText;
    }
}
