namespace Pia.Services.Interfaces;

/// <summary>
/// Reads and writes the vault charter — the statement of what this knowledge base is about, which
/// grounds ingest's judgement of which topics deserve a page. Exists so the Vault view can edit the
/// charter without taking a concrete service dependency; ingest keeps using the class directly.
/// </summary>
public interface IVaultCharterService
{
    /// <summary>The charter body, or <c>""</c> when none is set.</summary>
    Task<string> GetCharterAsync();

    /// <summary>Persist the charter; an empty or whitespace body deletes the page.</summary>
    Task SaveCharterAsync(string body);
}
