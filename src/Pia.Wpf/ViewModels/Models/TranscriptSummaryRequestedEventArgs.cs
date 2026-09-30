namespace Pia.ViewModels.Models;

/// <summary>A transcript overlay handing its summary prompt to the assistant, which reports back the chat it went into.</summary>
public sealed class TranscriptSummaryRequestedEventArgs : EventArgs
{
    private readonly Action<Guid>? _onChatId;

    public TranscriptSummaryRequestedEventArgs(string prompt, Action<Guid>? onChatId = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Prompt = prompt;
        _onChatId = onChatId;
    }

    public string Prompt { get; }

    /// <summary>Called by the host once the summary chat has its id.</summary>
    public void ReportChatId(Guid chatId) => _onChatId?.Invoke(chatId);
}
