using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

/// <summary>Which producer an import file came from, as decided by shape sniffing.</summary>
public enum ChatArchiveFormat
{
    Unknown,
    Pia,
    OpenWebUi,
}

/// <summary>What a converted Open WebUI file yielded, plus the fidelity Pia could not keep.</summary>
/// <param name="RecoveredFromTree">
/// Messages the message tree held that the flat <c>chat.messages</c> cache had lost. Non-zero means the
/// export would have imported truncated had the converter trusted that cache.
/// </param>
public sealed record OpenWebUiConversion(
    List<SyncAssistantChat> Chats,
    int SkippedEmpty,
    int DroppedAttachments,
    int RecoveredFromTree);

/// <summary>Which stage of an import the progress report describes.</summary>
public enum ChatImportPhase
{
    /// <summary>Reading and parsing the file. The chat count is not known yet.</summary>
    Reading,

    /// <summary>Mapping parsed records onto Pia chats.</summary>
    Converting,

    /// <summary>Writing chats to the store, one at a time.</summary>
    Storing,
}

/// <summary>
/// One progress tick. <see cref="Total"/> is 0 until the file is parsed, so only
/// <see cref="ChatImportPhase.Storing"/> can drive a determinate bar.
/// </summary>
public readonly record struct ChatImportProgress(ChatImportPhase Phase, int Processed, int Total);

/// <summary>Outcome of one import run. Every count is surfaced to the user, so none of them are debug-only.</summary>
public sealed record ChatImportResult
{
    public ChatArchiveFormat Format { get; init; }

    public int Imported { get; init; }

    /// <summary>Already stored at the same or a newer <c>UpdatedAt</c> — re-importing a file is a no-op.</summary>
    public int SkippedUpToDate { get; init; }

    /// <summary>No usable messages. History hides message-less chats, so importing them would be invisible.</summary>
    public int SkippedEmpty { get; init; }

    /// <summary>Chats whose write threw; the rest of the file still imported.</summary>
    public int Failed { get; init; }

    /// <summary>Attachments left behind — chat history is a text-only store.</summary>
    public int DroppedAttachments { get; init; }

    /// <summary>Oldest imported <c>UpdatedAt</c>, so the caller can widen a date filter that would hide the result.</summary>
    public DateTime? OldestUpdatedAt { get; init; }

    public int Total => Imported + SkippedUpToDate + SkippedEmpty + Failed;
}

public interface IChatArchiveService
{
    /// <summary>Writes the given chats as a Pia chat archive. Returns how many were written.</summary>
    Task<int> ExportAsync(IReadOnlyList<Guid> chatIds, string filePath, CancellationToken ct = default);

    Task<int> ExportAllAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Imports a Pia archive or an Open WebUI export, detected by shape. Parsing, conversion and the
    /// per-chat writes all run off the caller's context, so a UI caller stays responsive.
    /// </summary>
    Task<ChatImportResult> ImportAsync(
        string filePath,
        IProgress<ChatImportProgress>? progress = null,
        CancellationToken ct = default);
}
