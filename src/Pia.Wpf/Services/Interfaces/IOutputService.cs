namespace Pia.Services.Interfaces;

public interface IOutputService
{
    Task CopyToClipboardAsync(string text);

    /// <summary>Copies text that must stay out of Windows clipboard history and cloud clipboard sync.</summary>
    Task CopySecretToClipboardAsync(string text);
    Task AutoTypeAsync(string text, CancellationToken cancellationToken = default);
    Task PasteToPreviousWindowAsync(string text, CancellationToken cancellationToken = default);
}
