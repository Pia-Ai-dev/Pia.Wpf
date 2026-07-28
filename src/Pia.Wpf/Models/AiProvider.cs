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

    /// <summary>
    /// The model's total context window, in tokens. <see langword="null"/> — together with
    /// <see cref="MaxOutputTokens"/> — means agent context compaction is OFF for this provider,
    /// which is the state every provider persisted before this field existed upgrades into
    /// (providers are stored as JSON, so a missing property simply deserializes as null).
    /// <para>
    /// Deliberately absent from <c>SyncProvider</c>: the budget is a device-local tuning knob,
    /// exactly like SupportsStreaming, ReasoningEffort, EnableWebSearch and MistralAgentId, which
    /// the sync DTO already omits by design.
    /// </para>
    /// <para>
    /// Deliberately absent from <c>ProviderFingerprint.Compute</c>: that fingerprint keys the
    /// capability cache, whose entries record tool-calling and streaming probe outcomes. A window
    /// size cannot change what those probes measure, so including it would force a needless live
    /// re-probe on every budget edit.
    /// </para>
    /// </summary>
    public int? MaxContextWindowTokens { get; set; }

    /// <summary>
    /// The most output tokens the model can generate in one response. Subtracted from
    /// <see cref="MaxContextWindowTokens"/> to get the input budget compaction works against.
    /// <see langword="null"/> (or a value at or above the window) means compaction is OFF.
    /// Same two deliberate omissions as <see cref="MaxContextWindowTokens"/> — SyncProvider and
    /// ProviderFingerprint.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

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
