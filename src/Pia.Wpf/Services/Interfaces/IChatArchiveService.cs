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
public sealed record OpenWebUiConversion(
    List<SyncAssistantChat> Chats,
    int SkippedEmpty,
    int DroppedAttachments);

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

    /// <summary>Imports a Pia archive or an Open WebUI export, detected by shape.</summary>
    Task<ChatImportResult> ImportAsync(string filePath, CancellationToken ct = default);
}
