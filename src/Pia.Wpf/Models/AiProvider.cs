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
    VLlm,
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
    public int TimeoutSeconds { get; set; } = 300;
    public ReasoningEffort? ReasoningEffort { get; set; }
    public bool EnableWebSearch { get; set; } = false;
    public string? MistralAgentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Shallow copy — used to apply a per-turn override (e.g. a persona's reasoning effort) without
    /// mutating the stored provider. All fields are value types or immutable strings, so a shallow
    /// copy is safe.
    /// </summary>
    public AiProvider Clone() => (AiProvider)MemberwiseClone();
}
