namespace Pia.Models;

public class OptimizationSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OriginalText { get; set; }
    public required string OptimizedText { get; set; }
    public Guid TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public Guid ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public bool WasTranscribed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int TokensUsed { get; set; }
    public long ProcessingTimeMs { get; set; }
}
