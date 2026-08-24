using Pia.Models;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// OpenRouter ids do not follow the origin providers' conventions — namespaced, sometimes alias-prefixed,
/// and carrying a routing suffix that can change the window. Getting the lookup rule wrong oversizes
/// requests the route then refuses.
/// </summary>
public class OpenRouterContextWindowTests
{
    [Theory]
    [InlineData("anthropic/claude-opus-5", 1_000_000)]
    [InlineData("qwen/qwen3-14b", 40_960)]
    [InlineData("thedrummer/unslopnemo-12b", 32_768)]
    [InlineData("meta-llama/llama-4-scout", 327_680)]
    public void TheRoutedWindowWins_NotTheAdvertisedOne(string id, int expected)
    {
        Assert.True(OpenRouterContextWindows.TryGet(id, out var window));
        Assert.Equal(expected, window);
    }

    /// <summary>An alias row floats to whatever the author ships as current; the id resolves with or without
    /// the marker.</summary>
    [Theory]
    [InlineData("anthropic/claude-opus-latest")]
    [InlineData("~anthropic/claude-opus-latest")]
    [InlineData("  ANTHROPIC/Claude-Opus-Latest  ")]
    public void AnAliasResolvesWithOrWithoutItsMarker(string id)
    {
        Assert.True(OpenRouterContextWindows.TryGet(id, out var window));
        Assert.Equal(1_000_000, window);
    }

    /// <summary>The load-bearing one. Eight listed variants serve a different window from their base, mostly
    /// smaller — collapsing them onto the base would oversize the request by up to 4x.</summary>
    [Theory]
    [InlineData("poolside/laguna-s-2.1:free", 262_144, "poolside/laguna-s-2.1", 1_048_576)]
    [InlineData("z-ai/glm-5.2:free", 256_000, "z-ai/glm-5.2", 1_048_576)]
    [InlineData("thinkingmachines/inkling:free", 262_144, "thinkingmachines/inkling", 524_288)]
    [InlineData("nvidia/nemotron-3.5-lightning:free", 1_000_000, "nvidia/nemotron-3.5-lightning", 262_144)]
    public void AListedVariantKeepsItsOwnWindow(string variantId, int variantWindow, string baseId, int baseWindow)
    {
        Assert.True(OpenRouterContextWindows.TryGet(variantId, out var actualVariant));
        Assert.Equal(variantWindow, actualVariant);

        Assert.True(OpenRouterContextWindows.TryGet(baseId, out var actualBase));
        Assert.Equal(baseWindow, actualBase);

        Assert.NotEqual(actualBase, actualVariant);
    }

    /// <summary>An UNLISTED suffix still has to resolve — OpenRouter routes with `:nitro` and `:floor` too,
    /// and the base is the right answer when the snapshot has never seen the variant.</summary>
    [Fact]
    public void AnUnlistedVariantFallsBackToItsBase()
    {
        Assert.True(OpenRouterContextWindows.TryGet("anthropic/claude-opus-5:nitro", out var window));
        Assert.Equal(1_000_000, window);
    }

    [Theory]
    [InlineData("cohere/north-mini-code:free")]
    public void AVariantWithNoBaseRow_StillResolves(string id)
    {
        Assert.True(OpenRouterContextWindows.TryGet(id, out var window));
        Assert.True(window > 0);
    }

    [Theory]
    [InlineData("no-such/model-that-does-not-exist")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownModelIsNotGuessedAt(string? id)
    {
        Assert.False(OpenRouterContextWindows.TryGet(id, out var window));
        Assert.Equal(0, window);
    }

    /// <summary>The routing only applies to OpenRouter: the same id on another provider type must not pick up
    /// a window that describes OpenRouter's route rather than that provider's.</summary>
    [Fact]
    public void TheSnapshotIsConsultedOnlyForOpenRouter()
    {
        Assert.Equal(40_960, ContextWindowDefaults.For(AiProviderType.OpenRouter, "qwen/qwen3-14b"));
        Assert.Equal(ContextWindowDefaults.Fallback, ContextWindowDefaults.For(AiProviderType.OpenAI, "qwen/qwen3-14b"));
    }

    [Fact]
    public void AnOpenRouterModelTheSnapshotDoesNotKnow_StillTakesTheFallback()
    {
        Assert.Equal(ContextWindowDefaults.Fallback,
            ContextWindowDefaults.For(AiProviderType.OpenRouter, "no-such/model-that-does-not-exist"));
    }

    // ---- the live payload ---------------------------------------------------------------------------

    private const string Payload = """
        { "data": [
            { "id": "anthropic/claude-opus-5", "context_length": 1000000,
              "top_provider": { "context_length": 900000 } },
            { "id": "thedrummer/unslopnemo-12b", "context_length": 1024000,
              "top_provider": { "context_length": 32768 } },
            { "id": "vendor/no-top-provider", "context_length": 65536 },
            { "id": "vendor/null-routed", "context_length": 65536,
              "top_provider": { "context_length": null } }
        ] }
        """;

    /// <summary>The whole reason the live read exists: advertised and routed differ, and routed is what the
    /// request is measured against.</summary>
    [Fact]
    public void TheLiveReadPrefersTopProviderOverAdvertised()
    {
        Assert.Equal(900_000, OpenRouterModelCatalog.TryReadContextLength(Payload, "anthropic/claude-opus-5"));
        Assert.Equal(32_768, OpenRouterModelCatalog.TryReadContextLength(Payload, "thedrummer/unslopnemo-12b"));
    }

    [Theory]
    [InlineData("vendor/no-top-provider")]
    [InlineData("vendor/null-routed")]
    public void AMissingRoutedValueFallsBackToAdvertised(string id)
    {
        Assert.Equal(65_536, OpenRouterModelCatalog.TryReadContextLength(Payload, id));
    }

    [Fact]
    public void TheLiveReadAppliesTheSameNormalisation()
    {
        Assert.Equal(900_000, OpenRouterModelCatalog.TryReadContextLength(Payload, "~ANTHROPIC/Claude-Opus-5"));
        Assert.Equal(900_000, OpenRouterModelCatalog.TryReadContextLength(Payload, "anthropic/claude-opus-5:nitro"));
    }

    /// <summary>A save must never fail on a bad payload — the caller keeps the snapshot's value instead.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{ "data": "not an array" }""")]
    [InlineData("")]
    public void AnUnusablePayloadYieldsNothingRatherThanThrowing(string json)
    {
        Assert.Null(OpenRouterModelCatalog.TryReadContextLength(json, "anthropic/claude-opus-5"));
    }

    [Fact]
    public void AModelAbsentFromThePayloadYieldsNothing()
    {
        Assert.Null(OpenRouterModelCatalog.TryReadContextLength(Payload, "vendor/never-heard-of-it"));
    }
}
