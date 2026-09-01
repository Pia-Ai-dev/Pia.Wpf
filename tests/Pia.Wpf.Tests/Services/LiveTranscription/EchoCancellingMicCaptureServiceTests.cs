using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Losing echo cancellation has to be a degradation, never a failure: a machine without the Voice
/// Capture DSP, without a render endpoint, or with a driver the DSP will not open must still transcribe.
/// </summary>
public class EchoCancellingMicCaptureServiceTests
{
    private static ISettingsService SettingsWith(bool echoCancellation)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(Task.FromResult(new AppSettings { MicEchoCancellation = echoCancellation }));
        return settings;
    }

    private static EchoCancellingMicCaptureService Build(
        Func<IAudioCaptureSource> echoCancelling, Func<IAudioCaptureSource> plain, bool setting = true)
        => new(echoCancelling, plain, SettingsWith(setting),
            NullLogger<EchoCancellingMicCaptureService>.Instance);

    [Fact]
    public async Task StartAsync_FallsBackToThePlainMicrophone_WhenTheEchoCancellerCannotStart()
    {
        var plain = new PushableAudioSource();
        await using var sut = Build(() => new UnstartableAudioSource(), () => plain);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(sut.IsEchoCancelled);

        var frame = new float[] { 0.25f };
        plain.Push(frame);
        Assert.Equal(frame, await sut.Reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_SkipsTheEchoCanceller_WhenTheSettingIsOff()
    {
        var attempted = false;
        var plain = new PushableAudioSource();
        await using var sut = Build(
            () => { attempted = true; return new UnstartableAudioSource(); },
            () => plain,
            setting: false);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(attempted);
        Assert.False(sut.IsEchoCancelled);
    }

    [Fact]
    public async Task StartAsync_KeepsTheEchoCanceller_WhenItStarts()
    {
        var cancelled = new PushableAudioSource { StartedAt = DateTimeOffset.UnixEpoch };
        await using var sut = Build(() => cancelled, () => new PushableAudioSource());

        await sut.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(sut.IsEchoCancelled);
        Assert.Equal(DateTimeOffset.UnixEpoch, sut.StartedAt);
    }

    private sealed class UnstartableAudioSource : IAudioCaptureSource
    {
        public int SampleRate => 16000;
        public bool IsRunning => false;
        public System.Threading.Channels.ChannelReader<float[]> Reader =>
            throw new InvalidOperationException("never started");

        public Task StartAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The Windows voice capture DSP produced no audio.");

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Needs a real microphone and a real render endpoint, so it cannot run in the gate. Run it on the
/// machine that reported the echo: <c>-explicit on -class …WindowsAecMicCaptureSmokeTests</c>.
/// </summary>
public class WindowsAecMicCaptureSmokeTests(ITestOutputHelper output)
{
    [LiveApiFact]
    public async Task DspSource_EitherProducesAudioOrFailsCleanly()
    {
        await using var sut = new WindowsAecMicCaptureService(
            NullLogger<WindowsAecMicCaptureService>.Instance);

        try
        {
            await sut.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The documented degradation: no capture endpoint, no render endpoint, or no DSP. The point
            // of the test is that the interop returns an HRESULT instead of tearing the process down.
            // Which step gave up is the first thing to look at when the canceller does not engage.
            output.WriteLine($"echo canceller unavailable: {ex.InnerException?.Message ?? ex.Message}");
            return;
        }

        Assert.Equal(16000, sut.SampleRate);
        var frame = await sut.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(frame);
        Assert.NotNull(sut.StartedAt);
    }
}
