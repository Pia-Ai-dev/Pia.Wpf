namespace Pia.Helpers.Email;

public sealed record EmailMessage(
    string? Subject,
    string? From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    DateTimeOffset? Date,
    string Body,
    IReadOnlyList<string> AttachmentNames,
    bool BodyIsFromHtmlFallback);
