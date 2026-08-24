using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// The window assumed for a provider nobody configured. Getting a row wrong is worse than having no row:
/// too high sends an oversized request the provider rejects, too low silently evicts context.
/// </summary>
public class ContextWindowDefaultsTests
{
    /// <summary>Matched as a substring, because that is the form these ids actually arrive in — namespaced
    /// through OpenRouter, or carrying a dated snapshot suffix.</summary>
    [Theory]
    [InlineData("claude-opus-5", 1_000_000)]
    [InlineData("anthropic/claude-opus-5", 1_000_000)]
    [InlineData("claude-sonnet-5", 1_000_000)]
    [InlineData("claude-sonnet-4-6", 1_000_000)]
    [InlineData("claude-opus-4-8", 1_000_000)]
    [InlineData("claude-opus-4-7", 1_000_000)]
    [InlineData("claude-opus-4-6", 1_000_000)]
    [InlineData("claude-fable-5", 1_000_000)]
    [InlineData("claude-haiku-4-5", 200_000)]
    [InlineData("claude-haiku-4-5-20251001", 200_000)]
    public void AKnownModelResolvesItsOwnWindow(string modelName, int expected)
    {
        Assert.Equal(expected, ContextWindowDefaults.For(modelName));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.Equal(1_000_000, ContextWindowDefaults.For("ANTHROPIC/Claude-Opus-5"));
    }

    [Theory]
    [InlineData("some-model-nobody-listed")]
    [InlineData("gpt-4o")]
    [InlineData("llama3:latest")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnythingUnsourcedTakesTheFallback(string? modelName)
    {
        Assert.Equal(ContextWindowDefaults.Fallback, ContextWindowDefaults.For(modelName));
    }

    /// <summary>The fallback is a floor for current models, not a guess about any one of them. Lowering it
    /// would start evicting context on chats that fit today.</summary>
    [Fact]
    public void TheFallbackIsTheRecordedValue()
    {
        Assert.Equal(128_000, ContextWindowDefaults.Fallback);
    }

    /// <summary>
    /// Every override row must still be doing work. A row the catalogue already answers identically is a
    /// duplicate that will drift, and a long table here reads as though the catalogue were not serving the
    /// other providers at all — which is exactly the impression eight redundant Claude rows gave.
    /// </summary>
    [Fact]
    public void EveryOverrideRowEarnsItsPlace()
    {
        var redundant = new List<string>();

        foreach (var (fragment, window) in OverrideRows())
        {
            if (OpenRouterContextWindows.TryGet(fragment, out var fromCatalogue) && fromCatalogue == window)
                redundant.Add($"{fragment} ({window})");
        }

        Assert.True(redundant.Count == 0,
            "the catalogue already answers these identically, so the override rows are dead weight: "
            + string.Join(", ", redundant));
    }

    /// <summary>A row that is a substring of another would shadow it, and the shadowed model would silently
    /// take the wrong window.</summary>
    [Fact]
    public void NoRowShadowsAnother()
    {
        var fragments = OverrideRows().Select(r => r.Fragment).ToList();

        foreach (var fragment in fragments)
        {
            var others = fragments.Where(f => f != fragment && fragment.Contains(f, StringComparison.OrdinalIgnoreCase));
            Assert.True(!others.Any(), $"'{fragment}' contains another row: {string.Join(", ", others)}");
        }
    }

    /// <summary>Read off the shipped table, so neither check can drift from what it guards.</summary>
    private static IReadOnlyList<(string Fragment, int Window)> OverrideRows()
    {
        var field = typeof(ContextWindowDefaults)
            .GetField("VendorDocumented", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);

        return [.. ((ValueTuple<string, int>[])field!.GetValue(null)!).Select(r => (r.Item1, r.Item2))];
    }
}
