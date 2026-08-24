namespace Pia.Models;

/// <summary>
/// What the OpenRouter default route actually serves, per model — a dated snapshot, used only when the live
/// lookup cannot run. <c>ProviderService</c> re-reads the value from the API on every save of an OpenRouter
/// provider, so this table is the offline and first-run answer rather than the authority.
/// </summary>
public static class OpenRouterContextWindows
{
    /// <summary>When the table below was taken, from <c>GET https://openrouter.ai/api/v1/models</c>.</summary>
    public const string SnapshotDate = "2026-08-24";

    /// <summary>
    /// <c>top_provider.context_length</c> — what the default route serves — and NOT the advertised
    /// <c>context_length</c>. The two differ for 42 of these by up to 31x: <c>thedrummer/unslopnemo-12b</c>
    /// advertises 1024000 and serves 32768, so the advertised number would size a request nothing accepts.
    /// <para>
    /// Keyed by the id lowercased with a leading <c>~</c> removed, and <b>with any <c>:variant</c> suffix
    /// kept</b>. Stripping the suffix would be wrong: 8 variants serve a different window from their base,
    /// mostly smaller — <c>poolside/laguna-s-2.1:free</c> serves 262144 against the base's 1048576 — and
    /// three (<c>cohere/north-mini-code:free</c> among them) have no base row at all.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, int> Windows = new(StringComparer.Ordinal)
    {
        ["aion-labs/aion-2.0"] = 131_072,
        ["aion-labs/aion-3.0"] = 131_072,
        ["aion-labs/aion-3.0-mini"] = 131_072,
        ["aion-labs/aion-rp-llama-3.1-8b"] = 32_768,
        ["allenai/olmo-3-32b-think"] = 65_536,
        ["amazon/nova-2-lite-v1"] = 1_000_000,
        ["amazon/nova-lite-v1"] = 300_000,
        ["amazon/nova-micro-v1"] = 128_000,
        ["amazon/nova-premier-v1"] = 1_000_000,
        ["amazon/nova-pro-v1"] = 300_000,
        ["anthracite-org/magnum-v4-72b"] = 32_768,
        ["anthropic/claude-3-haiku"] = 200_000,
        ["anthropic/claude-fable-5"] = 1_000_000,
        ["anthropic/claude-fable-5:batch"] = 1_000_000,
        ["anthropic/claude-fable-latest"] = 1_000_000,
        ["anthropic/claude-haiku-4.5"] = 200_000,
        ["anthropic/claude-haiku-4.5:batch"] = 200_000,
        ["anthropic/claude-haiku-latest"] = 200_000,
        ["anthropic/claude-opus-4"] = 200_000,
        ["anthropic/claude-opus-4.1"] = 200_000,
        ["anthropic/claude-opus-4.1:batch"] = 200_000,
        ["anthropic/claude-opus-4.5"] = 200_000,
        ["anthropic/claude-opus-4.5:batch"] = 200_000,
        ["anthropic/claude-opus-4.6"] = 1_000_000,
        ["anthropic/claude-opus-4.6:batch"] = 1_000_000,
        ["anthropic/claude-opus-4.7"] = 1_000_000,
        ["anthropic/claude-opus-4.7-fast"] = 1_000_000,
        ["anthropic/claude-opus-4.7:batch"] = 1_000_000,
        ["anthropic/claude-opus-4.8"] = 1_000_000,
        ["anthropic/claude-opus-4.8-fast"] = 1_000_000,
        ["anthropic/claude-opus-4.8:batch"] = 1_000_000,
        ["anthropic/claude-opus-5"] = 1_000_000,
        ["anthropic/claude-opus-5-fast"] = 1_000_000,
        ["anthropic/claude-opus-5:batch"] = 1_000_000,
        ["anthropic/claude-opus-latest"] = 1_000_000,
        ["anthropic/claude-sonnet-4"] = 200_000,
        ["anthropic/claude-sonnet-4.5"] = 1_000_000,
        ["anthropic/claude-sonnet-4.5:batch"] = 1_000_000,
        ["anthropic/claude-sonnet-4.6"] = 1_000_000,
        ["anthropic/claude-sonnet-4.6:batch"] = 1_000_000,
        ["anthropic/claude-sonnet-5"] = 1_000_000,
        ["anthropic/claude-sonnet-5:batch"] = 1_000_000,
        ["anthropic/claude-sonnet-latest"] = 1_000_000,
        ["arcee-ai/trinity-large-thinking"] = 262_144,
        ["arcee-ai/virtuoso-large"] = 131_072,
        ["baidu/ernie-4.5-vl-424b-a47b"] = 123_000,
        ["bytedance-seed/seed-1.6"] = 262_144,
        ["bytedance-seed/seed-1.6-flash"] = 262_144,
        ["bytedance-seed/seed-2-1-turbo"] = 262_144,
        ["bytedance-seed/seed-2.0-code"] = 262_144,
        ["bytedance-seed/seed-2.0-lite"] = 262_144,
        ["bytedance-seed/seed-2.0-mini"] = 262_144,
        ["bytedance/ui-tars-1.5-7b"] = 128_000,
        ["cognitivecomputations/dolphin-mistral-24b-venice-edition"] = 128_000,
        ["cohere/command-a"] = 256_000,
        ["cohere/command-r-08-2024"] = 128_000,
        ["cohere/command-r-plus-08-2024"] = 128_000,
        ["cohere/command-r7b-12-2024"] = 128_000,
        ["cohere/north-mini-code:free"] = 256_000,
        ["deepseek/deepseek-chat"] = 128_000,
        ["deepseek/deepseek-chat-v3-0324"] = 163_840,
        ["deepseek/deepseek-chat-v3.1"] = 161_000,
        ["deepseek/deepseek-r1"] = 64_000,
        ["deepseek/deepseek-r1-0528"] = 163_840,
        ["deepseek/deepseek-r1-distill-llama-70b"] = 8_192,
        ["deepseek/deepseek-v3.1-terminus"] = 131_072,
        ["deepseek/deepseek-v3.2"] = 163_840,
        ["deepseek/deepseek-v3.2-exp"] = 163_840,
        ["deepseek/deepseek-v4-flash"] = 1_024_000,
        ["deepseek/deepseek-v4-flash-0731"] = 1_048_576,
        ["deepseek/deepseek-v4-flash-latest"] = 1_048_576,
        ["deepseek/deepseek-v4-flash-vision-exp"] = 1_048_576,
        ["deepseek/deepseek-v4-pro"] = 1_024_000,
        ["deepseek/deepseek-v4-pro-0813"] = 1_048_575,
        ["dots-studio/dots-3-note-preview:free"] = 512_000,
        ["google/gemini-2.5-flash"] = 1_048_576,
        ["google/gemini-2.5-flash-image"] = 32_768,
        ["google/gemini-2.5-flash-lite"] = 1_048_576,
        ["google/gemini-2.5-flash-lite:batch"] = 1_048_576,
        ["google/gemini-2.5-flash:batch"] = 1_048_576,
        ["google/gemini-2.5-pro"] = 1_048_576,
        ["google/gemini-2.5-pro-preview"] = 1_048_576,
        ["google/gemini-2.5-pro-preview-05-06"] = 1_048_576,
        ["google/gemini-2.5-pro:batch"] = 1_048_576,
        ["google/gemini-3-flash-preview"] = 1_048_576,
        ["google/gemini-3-flash-preview:batch"] = 1_048_576,
        ["google/gemini-3-pro-image"] = 65_536,
        ["google/gemini-3-pro-image-preview"] = 65_536,
        ["google/gemini-3.1-flash-image"] = 131_072,
        ["google/gemini-3.1-flash-image-preview"] = 65_536,
        ["google/gemini-3.1-flash-lite"] = 1_048_576,
        ["google/gemini-3.1-flash-lite-image"] = 65_536,
        ["google/gemini-3.1-flash-lite-preview"] = 1_048_576,
        ["google/gemini-3.1-flash-lite:batch"] = 1_048_576,
        ["google/gemini-3.1-pro-preview"] = 1_048_576,
        ["google/gemini-3.1-pro-preview-customtools"] = 1_048_576,
        ["google/gemini-3.1-pro-preview:batch"] = 1_048_576,
        ["google/gemini-3.5-flash"] = 1_048_576,
        ["google/gemini-3.5-flash-lite"] = 1_048_576,
        ["google/gemini-3.5-flash-lite:batch"] = 1_048_576,
        ["google/gemini-3.5-flash:batch"] = 1_048_576,
        ["google/gemini-3.6-flash"] = 1_048_576,
        ["google/gemini-3.6-flash:batch"] = 1_048_576,
        ["google/gemini-3.7-flash"] = 1_048_576,
        ["google/gemini-3.7-flash:batch"] = 1_048_576,
        ["google/gemini-flash-latest"] = 1_048_576,
        ["google/gemini-pro-latest"] = 1_048_576,
        ["google/gemma-2-27b-it"] = 8_192,
        ["google/gemma-3-12b-it"] = 131_072,
        ["google/gemma-3-27b-it"] = 131_072,
        ["google/gemma-3-4b-it"] = 131_072,
        ["google/gemma-3n-e4b-it"] = 32_768,
        ["google/gemma-4-26b-a4b-it"] = 262_144,
        ["google/gemma-4-26b-a4b-it:free"] = 262_144,
        ["google/gemma-4-31b-it"] = 262_144,
        ["google/gemma-4-31b-it:free"] = 262_144,
        ["google/lyria-3-clip-preview"] = 1_048_576,
        ["google/lyria-3-pro-preview"] = 1_048_576,
        ["gryphe/mythomax-l2-13b"] = 4_096,
        ["ibm-granite/granite-4.0-h-micro"] = 131_000,
        ["ibm-granite/granite-4.1-8b"] = 131_072,
        ["inception/mercury-2"] = 128_000,
        ["inclusionai/ling-2.6-1t"] = 262_144,
        ["inclusionai/ling-2.6-flash"] = 262_144,
        ["inclusionai/ling-3.0-flash"] = 262_144,
        ["inclusionai/ring-2.6-1t"] = 262_144,
        ["kwaipilot/kat-coder-air-v2.5"] = 256_000,
        ["kwaipilot/kat-coder-pro-v2"] = 256_000,
        ["kwaipilot/kat-coder-pro-v2.5"] = 256_000,
        ["liquid/lfm-2.5-2.6b:free"] = 65_536,
        ["mancer/weaver"] = 8_000,
        ["meituan/longcat-2.0"] = 1_048_756,
        ["meta-llama/llama-3.1-70b-instruct"] = 131_072,
        ["meta-llama/llama-3.1-8b-instruct"] = 131_072,
        ["meta-llama/llama-3.2-1b-instruct"] = 60_000,
        ["meta-llama/llama-3.2-3b-instruct"] = 131_072,
        ["meta-llama/llama-3.3-70b-instruct"] = 131_072,
        ["meta-llama/llama-4-maverick"] = 1_048_576,
        ["meta-llama/llama-4-scout"] = 327_680,
        ["meta-llama/llama-guard-4-12b"] = 163_840,
        ["meta/muse-glimmer-30b"] = 131_072,
        ["meta/muse-spark-1.1"] = 1_048_576,
        ["meta/muse-spark-1.2"] = 1_048_576,
        ["meta/muse-spark-1.2-contributor"] = 1_048_576,
        ["microsoft/phi-4"] = 16_384,
        ["microsoft/wizardlm-2-8x22b"] = 65_535,
        ["minimax/minimax-01"] = 1_000_192,
        ["minimax/minimax-m1"] = 1_000_000,
        ["minimax/minimax-m2"] = 204_800,
        ["minimax/minimax-m2-her"] = 65_536,
        ["minimax/minimax-m2.1"] = 204_800,
        ["minimax/minimax-m2.5"] = 200_000,
        ["minimax/minimax-m2.7"] = 196_608,
        ["minimax/minimax-m3"] = 524_288,
        ["minimax/minimax-m3:batch"] = 524_288,
        ["mistralai/codestral-2508"] = 256_000,
        ["mistralai/ministral-14b-2512"] = 262_144,
        ["mistralai/ministral-3b-2512"] = 131_072,
        ["mistralai/ministral-8b"] = 128_000,
        ["mistralai/ministral-8b-2512"] = 262_144,
        ["mistralai/mistral-large"] = 128_000,
        ["mistralai/mistral-large-2407"] = 131_072,
        ["mistralai/mistral-large-2512"] = 262_144,
        ["mistralai/mistral-medium-3"] = 131_072,
        ["mistralai/mistral-medium-3-5"] = 262_144,
        ["mistralai/mistral-medium-3.1"] = 131_072,
        ["mistralai/mistral-nemo"] = 131_072,
        ["mistralai/mistral-saba"] = 32_768,
        ["mistralai/mistral-small-24b-instruct-2501"] = 32_768,
        ["mistralai/mistral-small-2603"] = 262_144,
        ["mistralai/mistral-small-3.1-24b-instruct"] = 128_000,
        ["mistralai/mistral-small-3.2-24b-instruct"] = 128_000,
        ["mistralai/mixtral-8x22b-instruct"] = 65_536,
        ["mistralai/voxtral-small-24b-2507"] = 32_000,
        ["moonshotai/kimi-k2"] = 131_072,
        ["moonshotai/kimi-k2-0905"] = 262_144,
        ["moonshotai/kimi-k2-thinking"] = 262_144,
        ["moonshotai/kimi-k2.5"] = 262_144,
        ["moonshotai/kimi-k2.6"] = 262_144,
        ["moonshotai/kimi-k2.7-code"] = 262_144,
        ["moonshotai/kimi-k2.7-code:batch"] = 262_144,
        ["moonshotai/kimi-k3"] = 1_048_576,
        ["moonshotai/kimi-latest"] = 974_842,
        ["morph/morph-v3-fast"] = 81_920,
        ["morph/morph-v3-large"] = 262_144,
        ["nex-agi/nex-n2-mini"] = 262_144,
        ["nex-agi/nex-n2-pro"] = 262_144,
        ["nousresearch/hermes-3-llama-3.1-405b"] = 131_072,
        ["nousresearch/hermes-3-llama-3.1-70b"] = 131_072,
        ["nousresearch/hermes-4-405b"] = 131_072,
        ["nousresearch/hermes-4-70b"] = 131_072,
        ["nvidia/nemotron-3-nano-30b-a3b"] = 262_144,
        ["nvidia/nemotron-3-nano-30b-a3b:free"] = 256_000,
        ["nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free"] = 256_000,
        ["nvidia/nemotron-3-super-120b-a12b"] = 262_144,
        ["nvidia/nemotron-3-super-120b-a12b:free"] = 262_144,
        ["nvidia/nemotron-3-ultra-550b-a55b"] = 512_288,
        ["nvidia/nemotron-3-ultra-550b-a55b:batch"] = 512_288,
        ["nvidia/nemotron-3-ultra-550b-a55b:free"] = 1_000_000,
        ["nvidia/nemotron-3.5-content-safety:free"] = 128_000,
        ["nvidia/nemotron-3.5-lightning"] = 262_144,
        ["nvidia/nemotron-3.5-lightning:free"] = 1_000_000,
        ["nvidia/nemotron-nano-12b-v2-vl:free"] = 128_000,
        ["nvidia/nemotron-nano-9b-v2:free"] = 128_000,
        ["openai/gpt-3.5-turbo"] = 16_385,
        ["openai/gpt-3.5-turbo-0613"] = 4_095,
        ["openai/gpt-3.5-turbo-16k"] = 16_385,
        ["openai/gpt-3.5-turbo-instruct"] = 4_095,
        ["openai/gpt-3.5-turbo:batch"] = 16_385,
        ["openai/gpt-4"] = 8_191,
        ["openai/gpt-4-turbo"] = 128_000,
        ["openai/gpt-4-turbo-preview"] = 128_000,
        ["openai/gpt-4-turbo:batch"] = 128_000,
        ["openai/gpt-4.1"] = 1_047_576,
        ["openai/gpt-4.1-mini"] = 1_047_576,
        ["openai/gpt-4.1-mini:batch"] = 1_047_576,
        ["openai/gpt-4.1-nano"] = 1_047_576,
        ["openai/gpt-4.1-nano:batch"] = 1_047_576,
        ["openai/gpt-4.1:batch"] = 1_047_576,
        ["openai/gpt-4o"] = 128_000,
        ["openai/gpt-4o-2024-05-13"] = 128_000,
        ["openai/gpt-4o-2024-08-06"] = 128_000,
        ["openai/gpt-4o-2024-11-20"] = 128_000,
        ["openai/gpt-4o-mini"] = 128_000,
        ["openai/gpt-4o-mini-2024-07-18"] = 128_000,
        ["openai/gpt-4o-mini:batch"] = 128_000,
        ["openai/gpt-4o:batch"] = 128_000,
        ["openai/gpt-5"] = 400_000,
        ["openai/gpt-5-codex:batch"] = 400_000,
        ["openai/gpt-5-image"] = 400_000,
        ["openai/gpt-5-image-mini"] = 400_000,
        ["openai/gpt-5-mini"] = 400_000,
        ["openai/gpt-5-mini:batch"] = 400_000,
        ["openai/gpt-5-nano"] = 400_000,
        ["openai/gpt-5-nano:batch"] = 400_000,
        ["openai/gpt-5-pro"] = 400_000,
        ["openai/gpt-5-pro:batch"] = 400_000,
        ["openai/gpt-5:batch"] = 400_000,
        ["openai/gpt-5.1"] = 400_000,
        ["openai/gpt-5.1-codex"] = 400_000,
        ["openai/gpt-5.1-codex-max"] = 400_000,
        ["openai/gpt-5.1-codex-mini"] = 400_000,
        ["openai/gpt-5.1:batch"] = 400_000,
        ["openai/gpt-5.2"] = 400_000,
        ["openai/gpt-5.2-chat"] = 128_000,
        ["openai/gpt-5.2-codex"] = 400_000,
        ["openai/gpt-5.2-pro"] = 400_000,
        ["openai/gpt-5.2-pro:batch"] = 400_000,
        ["openai/gpt-5.2:batch"] = 400_000,
        ["openai/gpt-5.3-codex"] = 400_000,
        ["openai/gpt-5.4"] = 1_050_000,
        ["openai/gpt-5.4-image-2"] = 272_000,
        ["openai/gpt-5.4-mini"] = 400_000,
        ["openai/gpt-5.4-mini:batch"] = 400_000,
        ["openai/gpt-5.4-nano"] = 400_000,
        ["openai/gpt-5.4-nano:batch"] = 400_000,
        ["openai/gpt-5.4-pro"] = 1_050_000,
        ["openai/gpt-5.4-pro:batch"] = 1_050_000,
        ["openai/gpt-5.4:batch"] = 1_050_000,
        ["openai/gpt-5.5"] = 1_050_000,
        ["openai/gpt-5.5-pro"] = 1_050_000,
        ["openai/gpt-5.5-pro:batch"] = 1_050_000,
        ["openai/gpt-5.5:batch"] = 1_050_000,
        ["openai/gpt-5.6-luna"] = 1_050_000,
        ["openai/gpt-5.6-luna-pro"] = 1_050_000,
        ["openai/gpt-5.6-luna-pro:batch"] = 1_050_000,
        ["openai/gpt-5.6-luna:batch"] = 1_050_000,
        ["openai/gpt-5.6-sol"] = 1_050_000,
        ["openai/gpt-5.6-sol-pro"] = 1_050_000,
        ["openai/gpt-5.6-sol-pro:batch"] = 1_050_000,
        ["openai/gpt-5.6-sol:batch"] = 1_050_000,
        ["openai/gpt-5.6-terra"] = 1_050_000,
        ["openai/gpt-5.6-terra-pro"] = 1_050_000,
        ["openai/gpt-5.6-terra-pro:batch"] = 1_050_000,
        ["openai/gpt-5.6-terra:batch"] = 1_050_000,
        ["openai/gpt-audio"] = 128_000,
        ["openai/gpt-audio-mini"] = 128_000,
        ["openai/gpt-chat-latest"] = 400_000,
        ["openai/gpt-latest"] = 1_050_000,
        ["openai/gpt-mini-latest"] = 400_000,
        ["openai/gpt-oss-120b"] = 131_072,
        ["openai/gpt-oss-20b"] = 131_072,
        ["openai/gpt-oss-safeguard-20b"] = 131_072,
        ["openai/o1"] = 200_000,
        ["openai/o1-pro"] = 200_000,
        ["openai/o1-pro:batch"] = 200_000,
        ["openai/o1:batch"] = 200_000,
        ["openai/o3"] = 200_000,
        ["openai/o3-mini"] = 200_000,
        ["openai/o3-mini-high"] = 200_000,
        ["openai/o3-mini-high:batch"] = 200_000,
        ["openai/o3-mini:batch"] = 200_000,
        ["openai/o3-pro"] = 200_000,
        ["openai/o3-pro:batch"] = 200_000,
        ["openai/o3:batch"] = 200_000,
        ["openai/o4-mini"] = 200_000,
        ["openai/o4-mini-high"] = 200_000,
        ["openai/o4-mini-high:batch"] = 200_000,
        ["openai/o4-mini:batch"] = 200_000,
        ["openrouter/auto"] = 2_000_000,
        ["openrouter/auto-beta"] = 2_000_000,
        ["openrouter/bodybuilder"] = 128_000,
        ["openrouter/free"] = 200_000,
        ["openrouter/fusion"] = 1_000_000,
        ["openrouter/pareto-code"] = 2_000_000,
        ["perceptron/perceptron-mk1"] = 32_768,
        ["perplexity/sonar"] = 127_072,
        ["perplexity/sonar-deep-research"] = 128_000,
        ["perplexity/sonar-pro"] = 200_000,
        ["perplexity/sonar-pro-search"] = 200_000,
        ["perplexity/sonar-reasoning-pro"] = 128_000,
        ["poolside/laguna-s-2.1"] = 1_048_576,
        ["poolside/laguna-s-2.1:free"] = 262_144,
        ["poolside/laguna-xs-2.1"] = 262_144,
        ["poolside/laguna-xs-2.1:free"] = 262_144,
        ["qwen/qwen-2.5-72b-instruct"] = 32_768,
        ["qwen/qwen-2.5-7b-instruct"] = 32_768,
        ["qwen/qwen-2.5-coder-32b-instruct"] = 32_768,
        ["qwen/qwen-plus"] = 1_000_000,
        ["qwen/qwen-plus-2025-07-28"] = 1_000_000,
        ["qwen/qwen-plus-2025-07-28:thinking"] = 1_000_000,
        ["qwen/qwen2.5-vl-72b-instruct"] = 128_000,
        ["qwen/qwen3-14b"] = 40_960,
        ["qwen/qwen3-235b-a22b"] = 131_072,
        ["qwen/qwen3-235b-a22b-2507"] = 262_144,
        ["qwen/qwen3-235b-a22b-thinking-2507"] = 131_072,
        ["qwen/qwen3-30b-a3b"] = 40_960,
        ["qwen/qwen3-30b-a3b-instruct-2507"] = 128_000,
        ["qwen/qwen3-30b-a3b-thinking-2507"] = 81_920,
        ["qwen/qwen3-32b"] = 40_960,
        ["qwen/qwen3-8b"] = 131_072,
        ["qwen/qwen3-coder"] = 262_144,
        ["qwen/qwen3-coder-30b-a3b-instruct"] = 262_144,
        ["qwen/qwen3-coder-flash"] = 1_000_000,
        ["qwen/qwen3-coder-next"] = 262_144,
        ["qwen/qwen3-coder-plus"] = 1_000_000,
        ["qwen/qwen3-max"] = 262_144,
        ["qwen/qwen3-max-thinking"] = 262_144,
        ["qwen/qwen3-next-80b-a3b-instruct"] = 262_144,
        ["qwen/qwen3-next-80b-a3b-thinking"] = 131_072,
        ["qwen/qwen3-vl-235b-a22b-instruct"] = 131_072,
        ["qwen/qwen3-vl-235b-a22b-thinking"] = 131_072,
        ["qwen/qwen3-vl-30b-a3b-instruct"] = 131_072,
        ["qwen/qwen3-vl-30b-a3b-thinking"] = 131_072,
        ["qwen/qwen3-vl-32b-instruct"] = 131_072,
        ["qwen/qwen3-vl-8b-instruct"] = 131_072,
        ["qwen/qwen3-vl-8b-thinking"] = 131_072,
        ["qwen/qwen3.5-122b-a10b"] = 262_144,
        ["qwen/qwen3.5-27b"] = 262_144,
        ["qwen/qwen3.5-35b-a3b"] = 262_144,
        ["qwen/qwen3.5-397b-a17b"] = 262_144,
        ["qwen/qwen3.5-9b"] = 262_144,
        ["qwen/qwen3.5-flash-02-23"] = 1_000_000,
        ["qwen/qwen3.5-plus-02-15"] = 1_000_000,
        ["qwen/qwen3.5-plus-20260420"] = 1_000_000,
        ["qwen/qwen3.6-27b"] = 262_144,
        ["qwen/qwen3.6-35b-a3b"] = 262_144,
        ["qwen/qwen3.6-flash"] = 1_000_000,
        ["qwen/qwen3.6-max-preview"] = 262_144,
        ["qwen/qwen3.6-plus"] = 1_000_000,
        ["qwen/qwen3.7-flash"] = 1_000_000,
        ["qwen/qwen3.7-max"] = 1_000_000,
        ["qwen/qwen3.7-plus"] = 1_000_000,
        ["qwen/qwen3.8-2.4t-a95b"] = 1_048_576,
        ["qwen/qwen3.8-27b"] = 262_144,
        ["qwen/qwen3.8-max"] = 1_000_000,
        ["rekaai/reka-edge"] = 16_384,
        ["rekaai/reka-flash-3"] = 65_536,
        ["relace/relace-apply-3"] = 256_000,
        ["relace/relace-search"] = 256_000,
        ["sakana/fugu-ultra"] = 1_000_000,
        ["sakana/sakana-namazu"] = 262_144,
        ["sao10k/l3-lunaris-8b"] = 8_192,
        ["sao10k/l3.1-euryale-70b"] = 131_072,
        ["sao10k/l3.3-euryale-70b"] = 131_072,
        ["stealth/ox-alpha"] = 1_048_576,
        ["stepfun/step-3.5-flash"] = 262_144,
        ["stepfun/step-3.7-flash"] = 256_000,
        ["tencent/hunyuan-a13b-instruct"] = 131_072,
        ["tencent/hy-mt2-1.8b"] = 8_192,
        ["tencent/hy-mt2-30b-a3b"] = 8_192,
        ["tencent/hy-mt2-7b"] = 8_192,
        ["tencent/hy3"] = 262_144,
        ["tencent/hy3-preview"] = 262_144,
        ["thedrummer/cydonia-24b-v4.1"] = 131_072,
        ["thedrummer/rocinante-12b"] = 65_536,
        ["thedrummer/skyfall-36b-v2"] = 32_768,
        ["thedrummer/unslopnemo-12b"] = 32_768,
        ["thinkingmachines/inkling"] = 524_288,
        ["thinkingmachines/inkling-small"] = 524_288,
        ["thinkingmachines/inkling-small:free"] = 262_144,
        ["thinkingmachines/inkling:batch"] = 524_288,
        ["thinkingmachines/inkling:free"] = 262_144,
        ["undi95/remm-slerp-l2-13b"] = 6_144,
        ["upstage/solar-pro-3"] = 131_072,
        ["upstage/solar-pro4"] = 524_288,
        ["writer/palmyra-x5"] = 1_040_000,
        ["x-ai/grok-4.20"] = 2_000_000,
        ["x-ai/grok-4.20-multi-agent"] = 2_000_000,
        ["x-ai/grok-4.3"] = 1_000_000,
        ["x-ai/grok-4.5"] = 500_000,
        ["x-ai/grok-4.6"] = 500_000,
        ["x-ai/grok-build-0.1"] = 256_000,
        ["x-ai/grok-latest"] = 500_000,
        ["xiaomi/mimo-v2.5"] = 1_048_576,
        ["xiaomi/mimo-v2.5-pro"] = 1_048_576,
        ["z-ai/glm-4.5"] = 131_072,
        ["z-ai/glm-4.5-air"] = 131_072,
        ["z-ai/glm-4.5v"] = 65_536,
        ["z-ai/glm-4.6"] = 202_752,
        ["z-ai/glm-4.6v"] = 131_072,
        ["z-ai/glm-4.7"] = 202_752,
        ["z-ai/glm-4.7-flash"] = 202_752,
        ["z-ai/glm-5"] = 198_000,
        ["z-ai/glm-5-turbo"] = 202_752,
        ["z-ai/glm-5.1"] = 200_000,
        ["z-ai/glm-5.2"] = 1_048_576,
        ["z-ai/glm-5.2:batch"] = 1_048_575,
        ["z-ai/glm-5.2:free"] = 256_000,
        ["z-ai/glm-5.3"] = 1_048_576,
        ["z-ai/glm-5v-turbo"] = 202_752,
        ["z-ai/glm-latest"] = 1_048_576,    };

    /// <summary>
    /// The window this model's default route serves, or false when the snapshot does not know it.
    /// <para>
    /// OpenRouter ids do not follow the origin providers' conventions — they are namespaced
    /// (<c>anthropic/claude-opus-5</c>), alias rows carry a leading <c>~</c>, and a routing suffix selects a
    /// variant. An exact hit therefore wins, and only an id the snapshot has never seen falls back to its
    /// base, so a known variant keeps its own window while an unknown <c>:nitro</c> still resolves.
    /// </para>
    /// </summary>
    public static bool TryGet(string? modelName, out int window)
    {
        window = 0;
        if (string.IsNullOrWhiteSpace(modelName))
            return false;

        foreach (var key in LookupKeys(modelName))
            if (Canonical.TryGetValue(key, out window))
                return true;

        return TryResolveByBasename(modelName, out window);
    }

    /// <summary>
    /// The same catalogue read for a NON-OpenRouter provider, where the model has no <c>author/</c> prefix:
    /// <c>gpt-4o</c>, <c>claude-haiku-4-5-20251001</c>, <c>mistral-large-latest</c>. Falls back to the longest
    /// registered basename the id begins with, so a dated snapshot or a <c>-latest</c> alias still lands.
    /// <para>
    /// <b>These are the windows OpenRouter's route serves, which can sit below what the vendor serves
    /// directly</b> — <c>anthropic/claude-sonnet-4</c> is 200000 here against 1000000 advertised. That biases
    /// a direct provider low, which compacts early rather than sending a request the provider refuses. Seven
    /// basenames carry conflicting windows and deliberately resolve to nothing, so the caller's default
    /// applies instead of a coin toss.
    /// </para>
    /// </summary>
    private static bool TryResolveByBasename(string modelName, out int window)
    {
        window = 0;
        var id = Normalize(modelName);
        var stripped = StripVariant(id);
        var basename = stripped.Contains('/') ? stripped[(stripped.IndexOf('/') + 1)..] : stripped;

        if (basename.Length == 0)
            return false;

        if (ByBasename.TryGetValue(basename, out window))
            return true;

        // A name we KNOW but cannot decide stops here. Falling through would hand "glm-5.2" the window of
        // "glm-5" — a different model — where the caller's default is the honest answer.
        if (ConflictedBasenames.Contains(basename))
        {
            window = 0;
            return false;
        }

        // Longest first, and only on a separator boundary — otherwise "gpt-5x" would inherit "gpt-5".
        foreach (var candidate in BasenamesByLength)
            if (basename.Length > candidate.Length
                && basename.StartsWith(candidate, StringComparison.Ordinal)
                && basename[candidate.Length] is '-' or '/'
                && ByBasename.TryGetValue(candidate, out window))
            {
                return true;
            }

        window = 0;
        return false;
    }

    /// <summary>
    /// The ids to try, most specific first: the model as written, then — only if that is unknown — the same
    /// id without its <c>:variant</c> suffix. Shared with the live lookup so both apply one rule; a variant
    /// that is listed must keep its own window rather than inherit its base's.
    /// </summary>
    public static IEnumerable<string> LookupKeys(string modelName)
    {
        var id = Normalize(modelName);
        yield return id;

        var colon = id.LastIndexOf(':');
        if (colon > 0)
            yield return id[..colon];
    }

    /// <summary>
    /// Lowercased, trimmed, without the leading <c>~</c> that marks a floating alias row, and with <c>.</c>
    /// folded to <c>-</c> — the two conventions disagree on the separator, OpenRouter publishing
    /// <c>claude-haiku-4.5</c> where Anthropic's own id is <c>claude-haiku-4-5</c>. Measured to add no
    /// ambiguity: the seven conflicting basenames are the same set with or without the fold.
    /// <para>
    /// One function, used to build the index AND to look up, including by the live reader — they cannot
    /// disagree about what an id is. They did once: the index folded the separator and the lookup did not,
    /// so every id containing a dot missed.
    /// </para>
    /// </summary>
    public static string Normalize(string modelName) =>
        modelName.Trim().ToLowerInvariant().TrimStart('~').Replace('.', '-');

    private static string StripVariant(string id)
    {
        var colon = id.LastIndexOf(':');
        return colon > 0 ? id[..colon] : id;
    }

    /// <summary>The published ids, canonicalised, so a lookup meets either separator convention.</summary>
    private static readonly Dictionary<string, int> Canonical = BuildCanonical();

    /// <summary>Basename to window, with every basename that carries more than one window left OUT.</summary>
    private static readonly Dictionary<string, int> ByBasename = BuildByBasename();

    /// <summary>The basenames left out of <see cref="ByBasename"/>, kept so an ambiguous name can be refused
    /// outright rather than falling through to a shorter, unrelated prefix.</summary>
    private static readonly HashSet<string> ConflictedBasenames = BuildConflicted();

    private static readonly string[] BasenamesByLength =
        [.. ByBasename.Keys.OrderByDescending(k => k.Length)];

    private static Dictionary<string, int> BuildCanonical()
    {
        var canonical = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, window) in Windows)
            canonical[Normalize(id)] = window;
        return canonical;
    }

    private static Dictionary<string, int> BuildByBasename()
    {
        var windows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (basename, window) in EnumerateBasenames())
            windows[basename] = window;

        foreach (var basename in BuildConflicted())
            windows.Remove(basename);

        return windows;
    }

    private static HashSet<string> BuildConflicted()
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var conflicted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (basename, window) in EnumerateBasenames())
        {
            if (seen.TryGetValue(basename, out var existing) && existing != window)
                conflicted.Add(basename);
            else
                seen[basename] = window;
        }

        return conflicted;
    }

    private static IEnumerable<(string Basename, int Window)> EnumerateBasenames()
    {
        foreach (var (id, window) in Windows)
        {
            var stripped = StripVariant(Normalize(id));
            yield return (stripped.Contains('/') ? stripped[(stripped.IndexOf('/') + 1)..] : stripped, window);
        }
    }
}
