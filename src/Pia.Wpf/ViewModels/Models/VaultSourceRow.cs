namespace Pia.ViewModels.Models;

/// <summary>
/// One display-ready row of the Vault Overview's source-documents list: the raw file's name, its
/// vault-relative path (row tooltip), formatted size, and the localized ingest-status line the
/// view-model builds (compiled into N topic pages / not yet ingested / not a text file).
/// <see cref="IsIngested"/> drives the status swatch color in XAML.
/// </summary>
public record VaultSourceRow(
    string Name, string RelativePath, bool IsIngested, string StatusText, string SizeText);
