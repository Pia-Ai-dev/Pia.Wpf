using System.IO;

namespace Pia.Services.Interfaces;

public interface IAccountDataService
{
    /// <summary>Streams the server's ZIP export of the signed-in account into <paramref name="destination"/>.</summary>
    Task ExportAsync(Stream destination, CancellationToken ct = default);

    /// <summary>Writes the export to <paramref name="path"/>; a failed export leaves no partial file behind.</summary>
    Task ExportToFileAsync(string path, CancellationToken ct = default);

    /// <summary>Asks the server to delete the signed-in account. Only local accounts need a password.</summary>
    Task<AccountDeletionOutcome> DeleteAsync(string? password, CancellationToken ct = default);
}

public enum AccountDeletionOutcome
{
    Deleted,
    InvalidPassword,
    Failed,
}
