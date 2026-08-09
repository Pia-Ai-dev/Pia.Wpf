namespace Pia.Tests.Integration.Providers;

/// <summary>
/// Per-provider env-var lookups for real-API integration tests. Tests should
/// call <see cref="SkipIfMissing"/> at the top so they're skipped (not failed)
/// in environments where the relevant credentials aren't configured.
/// </summary>
internal static class ProviderTestEnvironment
{
    public static (string Endpoint, string ApiKey, string Model) OpenAi() => (
        Endpoint: "https://api.openai.com/v1",
        ApiKey: Env("PIA_TEST_OPENAI_KEY"),
        Model: EnvOrDefault("PIA_TEST_OPENAI_MODEL", "gpt-5-mini"));

    public static (string Endpoint, string ApiKey, string Deployment) AzureOpenAi() => (
        Endpoint: Env("PIA_TEST_AZURE_ENDPOINT"),
        ApiKey: Env("PIA_TEST_AZURE_KEY"),
        Deployment: Env("PIA_TEST_AZURE_DEPLOYMENT"));

    public static (string Endpoint, string ApiKey, string Model) Mistral() => (
        Endpoint: "https://api.mistral.ai/v1",
        ApiKey: Env("PIA_TEST_MISTRAL_KEY"),
        Model: EnvOrDefault("PIA_TEST_MISTRAL_MODEL", "mistral-small-latest"));

    public static string MistralAgentId() => Env("PIA_TEST_MISTRAL_AGENT_ID");

    public static (string Endpoint, string ApiKey, string Model) OpenRouter() => (
        Endpoint: "https://openrouter.ai/api/v1",
        ApiKey: Env("PIA_TEST_OPENROUTER_KEY"),
        Model: EnvOrDefault("PIA_TEST_OPENROUTER_MODEL", "openai/gpt-5-mini"));

    public static (string Endpoint, string Model) Ollama() => (
        Endpoint: EnvOrDefault("PIA_TEST_OLLAMA_ENDPOINT", "http://localhost:11434/v1"),
        Model: EnvOrDefault("PIA_TEST_OLLAMA_MODEL", "qwen3:8b"));

    public static (string Endpoint, string Model) VLlm() => (
        Endpoint: Env("PIA_TEST_VLLM_ENDPOINT"),
        Model: EnvOrDefault("PIA_TEST_VLLM_MODEL", "Qwen/Qwen3-8B"));

    public static string? GetEnv(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Returns the env var value, or empty string if unset. Tests should check
    /// for empty via <see cref="SkipIfMissing"/> rather than throwing on read.
    /// </summary>
    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    private static string EnvOrDefault(string name, string fallback)
        => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))
            ? fallback
            : Environment.GetEnvironmentVariable(name)!;
}
