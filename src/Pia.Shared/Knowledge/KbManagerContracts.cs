namespace Pia.Shared.Knowledge;

/// <param name="Shared">The KB is granted to more than the caller's group; the other groups are never named.</param>
public sealed record KbManagerKnowledgeBase(Guid Id, string Name, int DocumentCount, bool Shared, bool HasPrompt);

public sealed record KbManagerDocument(
    Guid Id,
    string Title,
    string? SourceUri,
    string ContentType,
    string Status,
    long SizeBytes,
    string? Error,
    int ChunkCount,
    long Tokens,
    long RetrievalCount,
    DateTime? LastRetrievedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <param name="EmbeddingTokensThisMonth">Spent on this KB alone; the owner group's budget is not exposed because on a shared KB it may be another team's.</param>
public sealed record KbManagerStats(
    int DocumentCount,
    int PendingCount,
    int ProcessingCount,
    int ReadyCount,
    int FailedCount,
    long SizeBytes,
    long ByteLimit,
    int DocumentLimit,
    int ChunkCount,
    long IndexedTokens,
    long RetrievalCount,
    DateTime? LastRetrievedAt,
    long EmbeddingTokensThisMonth);

public sealed record KbManagerUploadRequest(string? Title, string? SourceUri, string? ContentType, string? Content);

/// <param name="ContentType">Null keeps the document's current type.</param>
/// <param name="Title">Null keeps the document's current title.</param>
public sealed record KbManagerUpdateContentRequest(string? Content, string? ContentType, string? Title);

public sealed record KbManagerWriteResult(Guid DocumentId, string Status);

public sealed record KbManagerPrompt(string? Prompt);
