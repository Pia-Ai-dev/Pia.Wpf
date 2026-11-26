using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services.Tts;

public sealed class TtsReadAloudTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly TtsService _service;

    public TtsReadAloudTests()
    {
        _service = new TtsService(NullLogger<TtsService>.Instance, _settings, Substitute.For<IAssetDownloader>());
    }

    [Theory]
    [InlineData("```\nvar x = 1;\n```")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |")]
    [InlineData("   ")]
    public async Task SpeakAsync_WithNothingSpeakable_ReturnsBeforeLoadingAVoice(string markdown)
    {
        await _service.SpeakAsync(markdown, TestContext.Current.CancellationToken);

        await _settings.DidNotReceive().GetSettingsAsync();
        Assert.False(_service.IsPlaying);
    }
}
