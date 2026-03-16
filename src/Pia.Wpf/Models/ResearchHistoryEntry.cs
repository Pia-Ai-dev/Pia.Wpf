namespace Pia.Models;

public class ResearchHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Query { get; set; } = string.Empty;
    public string SynthesizedResult { get; set; } = string.Empty;
    public string StepsJson { get; set; } = "[]";
    public Guid ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string Status { get; set; } = "Completed";
    public int StepCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime CompletedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Truncated query for display in list views.
    /// </summary>
    public string QueryPreview => Query.Length > 120 ? Query[..120] + "..." : Query;

    /// <summary>
    /// Truncated result for display in list views.
    /// </summary>
    public string ResultPreview =>
        SynthesizedResult.Length > 200 ? SynthesizedResult[..200] + "..." : SynthesizedResult;
}

public class ResearchStepDto
{
    public int StepNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = "Completed";
}
