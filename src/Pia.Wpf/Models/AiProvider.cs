namespace Pia.Models;

public enum AiProviderType
{
    PiaCloud,
    OpenAI,
    AzureOpenAI,
    Ollama,
    OpenRouter,
    OpenAICompatible,
    Mistral
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
    public int TimeoutSeconds { get; set; } = 30;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
