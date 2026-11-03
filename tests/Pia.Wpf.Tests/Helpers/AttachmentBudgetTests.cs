using Pia.Helpers;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Helpers;

public sealed class AttachmentBudgetTests
{
    private static AiProvider Provider(int? window, int? maxOutput, string model = "some-model") => new()
    {
        Name = "Test",
        Endpoint = "https://example.test",
        ModelName = model,
        MaxContextWindowTokens = window,
        MaxOutputTokens = maxOutput,
    };

    [Theory]
    [InlineData(128_000, null, 32_000)]
    [InlineData(200_000, 8_000, 48_000)]
    [InlineData(1_000_000, 32_000, AttachmentBudget.MaxTokens)]
    [InlineData(8_000, 2_000, AttachmentBudget.MinTokens)]
    public void ForChat_IsAQuarterOfTheInputWindow_Clamped(int window, int? maxOutput, int expected) =>
        Assert.Equal(expected, AttachmentBudget.ForChat(Provider(window, maxOutput)));

    [Theory]
    [InlineData(4_096, 3_072, 512)]
    [InlineData(4_096, 4_095, 0)]
    public void ForChat_ASmallInputCapsTheFloorAtHalfOfIt(int window, int maxOutput, int expected) =>
        Assert.Equal(expected, AttachmentBudget.ForChat(Provider(window, maxOutput)));

    [Fact]
    public void ForChat_WithoutAProvider_UsesTheDefaultWindow() =>
        Assert.Equal((int)(ContextWindowDefaults.Fallback * AttachmentBudget.WindowShare), AttachmentBudget.ForChat(null));

    [Fact]
    public void ForChat_WithAnOutputAboveTheWindow_UsesTheModelsDefaultWindow() =>
        Assert.Equal(
            (int)(ContextWindowDefaults.For("unlisted-local-model") * AttachmentBudget.WindowShare),
            AttachmentBudget.ForChat(Provider(4_000, 8_000, "unlisted-local-model")));

    [Fact]
    public void Distribute_OneFileMayUseEverything() =>
        Assert.Equal([32_000], AttachmentBudget.Distribute([50_000], 32_000));

    [Fact]
    public void Distribute_ASmallFileArrivesWholeAndTheBigOneGetsTheRest() =>
        Assert.Equal([31_000, 1_000], AttachmentBudget.Distribute([40_000, 1_000], 32_000));

    [Fact]
    public void Distribute_TwoBigFilesShareEqually() =>
        Assert.Equal([16_000, 16_000], AttachmentBudget.Distribute([30_000, 30_000], 32_000));

    [Fact]
    public void Distribute_NothingAvailable_GrantsNothing() =>
        Assert.Equal([0, 0], AttachmentBudget.Distribute([10, 20], -5));
}
