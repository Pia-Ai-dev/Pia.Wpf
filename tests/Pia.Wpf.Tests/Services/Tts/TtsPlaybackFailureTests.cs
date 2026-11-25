using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services.Tts;

public sealed class TtsPlaybackFailureTests
{
    private const int SentenceCount = 20;

    [Fact]
    public async Task A_FailingPlayback_CancelsAndAwaitsTheProducer()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new TtsService(
            NullLogger<TtsService>.Instance, Substitute.For<ISettingsService>(), Substitute.For<IAssetDownloader>());
        var source = new SentenceSource();
        var synthesized = 0;

        await service.PlaySentencesAsync(
            source.Read(),
            ct,
            onFirstChunk: null,
            synthesize: (_, _) =>
            {
                Interlocked.Increment(ref synthesized);
                return Task.FromResult(new byte[1]);
            },
            play: (_, _) => throw new InvalidOperationException("sink failed"))
            .WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.True(source.Closed);
        Assert.True(synthesized < SentenceCount);
        Assert.False(service.IsPlaying);
    }

    private sealed class SentenceSource
    {
        private volatile bool _closed;

        public bool Closed => _closed;

        public async IAsyncEnumerable<string> Read()
        {
            try
            {
                for (var i = 0; i < SentenceCount; i++)
                {
                    await Task.Yield();
                    yield return $"Sentence {i}.";
                }
            }
            finally
            {
                _closed = true;
            }
        }
    }
}
