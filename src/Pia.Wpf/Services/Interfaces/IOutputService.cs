namespace Pia.Services.Interfaces;

public interface IOutputService
{
    Task CopyToClipboardAsync(string text);
    Task AutoTypeAsync(string text, CancellationToken cancellationToken = default);
    Task PasteToPreviousWindowAsync(string text, CancellationToken cancellationToken = default);
}
