using Pia.Models;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class ToolLoopImageChannelTests
{
    private static ToolLoopImage Image(string callId) =>
        new(callId, [1, 2, 3], "image/jpeg", 4, 4, "caption");

    [Fact]
    public void Park_ThenDrain_ReturnsInOrder_AndEmpties()
    {
        var channel = new ToolLoopImageChannel(AiProviderType.PiaCloud);
        channel.Park(Image("call-1"));
        channel.Park(Image("call-2"));

        var drained = channel.Drain();

        Assert.Equal(["call-1", "call-2"], drained.Select(i => i.CallId));
        Assert.Equal(0, channel.Count);
        Assert.Empty(channel.Drain());
    }

    [Fact]
    public void Count_TracksParkedNotDrained()
    {
        var channel = new ToolLoopImageChannel(AiProviderType.OpenAI);
        Assert.Equal(0, channel.Count);

        channel.Park(Image("call-1"));
        Assert.Equal(1, channel.Count);

        channel.Drain();
        Assert.Equal(0, channel.Count);
    }

    [Fact]
    public void ProviderType_IsWhateverTheLoopOpenedItWith()
    {
        Assert.Equal(AiProviderType.PiaCloud, new ToolLoopImageChannel(AiProviderType.PiaCloud).ProviderType);
        Assert.Equal(AiProviderType.Ollama, new ToolLoopImageChannel(AiProviderType.Ollama).ProviderType);
    }

    [Fact]
    public void Current_IsNullOnAFreshFlow()
    {
        Assert.Null(ToolLoopImageChannel.Current);
    }

    /// <summary>The refusal a handler outside a loop depends on: a channel set deeper in the call tree is gone
    /// by the time the caller resumes, so nothing outside its own dispatch can see or reuse it.</summary>
    [Fact]
    public async Task Current_SetOnAChildFlow_DoesNotLeakToTheParent()
    {
        await Child();

        Assert.Null(ToolLoopImageChannel.Current);

        static async Task Child()
        {
            ToolLoopImageChannel.Current = new ToolLoopImageChannel(AiProviderType.PiaCloud);
            await Task.Yield();
            Assert.NotNull(ToolLoopImageChannel.Current);
        }
    }

    [Fact]
    public void TryPark_TakesFour_ThenRefuses()
    {
        var channel = new ToolLoopImageChannel(AiProviderType.PiaCloud);

        for (var i = 1; i <= ToolLoopImageChannel.MaxImagesPerRound; i++)
            Assert.True(channel.TryPark(Image($"call-{i}")));

        Assert.False(channel.TryPark(Image("call-5")));
        Assert.Equal(ToolLoopImageChannel.MaxImagesPerRound, channel.Count);
        Assert.DoesNotContain("call-5", channel.Drain().Select(i => i.CallId));
    }

    [Fact]
    public void TryPark_AfterADrain_TakesAFullRoundAgain()
    {
        var channel = new ToolLoopImageChannel(AiProviderType.PiaCloud);
        for (var i = 1; i <= ToolLoopImageChannel.MaxImagesPerRound; i++) channel.TryPark(Image($"a-{i}"));
        channel.Drain();

        Assert.True(channel.TryPark(Image("b-1")));
    }

    /// <summary>screen_capture's path is uncapped on purpose — every frame has an approval card behind it.</summary>
    [Fact]
    public void Park_IgnoresTheCap()
    {
        var channel = new ToolLoopImageChannel(AiProviderType.PiaCloud);

        for (var i = 1; i <= ToolLoopImageChannel.MaxImagesPerRound + 1; i++) channel.Park(Image($"call-{i}"));

        Assert.Equal(ToolLoopImageChannel.MaxImagesPerRound + 1, channel.Count);
    }
}
