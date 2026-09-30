namespace Pia.Services.Consent;

/// <summary>One place a session's transcript went, as logged in the session's evidence folder.</summary>
/// <param name="Kind"><see cref="ExportKind"/>, <see cref="VaultKind"/>, <see cref="ChatKind"/> or <see cref="SummaryRequestedKind"/>.</param>
/// <param name="Path">The exported file; set for <see cref="ExportKind"/> only.</param>
/// <param name="Ref">The vault-relative note; set for <see cref="VaultKind"/> only.</param>
/// <param name="ChatId">The summary chat; set for <see cref="ChatKind"/> only.</param>
public sealed record ConsentCopy(
    string Kind,
    DateTimeOffset At,
    string? Path = null,
    string? Ref = null,
    Guid? ChatId = null)
{
    public const string ExportKind = "export";
    public const string VaultKind = "vault";
    public const string ChatKind = "chat";

    // Logged before the chat has an id, so a summary still in flight counts as a copy.
    public const string SummaryRequestedKind = "summary-requested";

    public static ConsentCopy Export(string path, DateTimeOffset at) => new(ExportKind, at, Path: path);

    public static ConsentCopy Vault(string reference, DateTimeOffset at) => new(VaultKind, at, Ref: reference);

    public static ConsentCopy Chat(Guid chatId, DateTimeOffset at) => new(ChatKind, at, ChatId: chatId);

    public static ConsentCopy SummaryRequested(DateTimeOffset at) => new(SummaryRequestedKind, at);
}
