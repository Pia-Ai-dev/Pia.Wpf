namespace Pia.ViewModels.Models;

public sealed record ConsentExportRow(string Path, DateTime SavedAt);

/// <param name="Key">Keys the row's AutomationId, which must not carry the user-named path.</param>
public sealed record ConsentNoteRow(int Key, string Reference);

public sealed record ConsentChatRow(Guid ChatId, DateTime SummarizedAt);
