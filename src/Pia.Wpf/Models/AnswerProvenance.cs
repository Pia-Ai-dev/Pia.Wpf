namespace Pia.Models;

/// <summary>What the answer footer and the exports name as the origin of a model reply.</summary>
public static class AnswerProvenance
{
    public const string PiaCloudLabel = "Pia Cloud";

    /// <summary>
    /// Pia Cloud chooses (and may re-route) the upstream model, so it is named as the service only. Every
    /// other provider is named as "provider" + the model the response reported, falling back to the configured one.
    /// </summary>
    public static (string Model, string? Provider) Describe(AiProvider provider, string? responseModelId)
    {
        if (provider.ProviderType == AiProviderType.PiaCloud)
            return (PiaCloudLabel, null);

        var model = FirstNonBlank(responseModelId, provider.ModelName, provider.Name);
        return (model, ProviderLabel(provider));
    }

    public static string ProviderLabel(AiProvider provider) => provider.ProviderType switch
    {
        AiProviderType.PiaCloud => PiaCloudLabel,
        AiProviderType.OpenAI => "OpenAI",
        AiProviderType.AzureOpenAI => "Azure OpenAI",
        AiProviderType.Ollama => "Ollama",
        AiProviderType.OpenRouter => "OpenRouter",
        AiProviderType.Mistral => "Mistral",
        AiProviderType.VLlm => "vLLM",
        // The type says nothing about who runs the endpoint; the user's own name for it does.
        AiProviderType.OpenAICompatible => string.IsNullOrWhiteSpace(provider.Name) ? "OpenAI-compatible" : provider.Name,
        _ => provider.ProviderType.ToString(),
    };

    private static string FirstNonBlank(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c)) return c.Trim();
        }
        return string.Empty;
    }
}
