namespace Pia.Models;

public enum AiProviderType
{
    PiaCloud,
    OpenAI,
    AzureOpenAI,
    Ollama,
    OpenRouter,
    OpenAICompatible,
    Mistral,
    Anthropic
}

public enum ReasoningEffort
{
    None,
    Low,
    Medium,
    High
}

public class AiProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public AiProviderType ProviderType { get; set; }
    public required string Endpoint { get; set; }
    public string? ModelName { get; set; }
    public string? EncryptedApiKey { get; set; }
    public string? AzureDeploymentName { get; set; }
    public bool SupportsToolCalling { get; set; } = true;
    public bool SupportsStreaming { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Provider-specific options
    public ReasoningEffort ReasoningEffort { get; set; } = ReasoningEffort.None;
    public bool WebSearchEnabled { get; set; }
    public bool ExtendedThinkingEnabled { get; set; }
    public int? ThinkingBudgetTokens { get; set; }
    public bool PromptCachingEnabled { get; set; }
}
