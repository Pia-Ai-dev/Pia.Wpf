using System.Threading.Channels;
using Pia.Services.LiveTranscription;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Fakes shared by <c>DirectTranscriptionServiceTests</c>. Mirrors the shape of
/// <c>MeetingAttendeeServiceStateTests</c>' private <c>FakeAudioSource</c> (that file belongs to another
/// module and is off-limits to edit), duplicated here rather than shared so this module's tests stay
/// self-contained and do not create a cross-module test dependency.
/// </summary>
internal sealed class FakeAudioSource : IAudioCaptureSource
{
    private readonly List<string>? _order;
    private readonly string _tag;
    private readonly bool _throwOnStart;
    private readonly Channel<float[]> _channel = Channel.CreateUnbounded<float[]>();

    public FakeAudioSource(List<string>? order = null, string tag = "source", bool throwOnStart = false)
    {
        _order = order;
        _tag = tag;
        _throwOnStart = throwOnStart;
    }

    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public int SampleRate => 16000;
    public bool IsRunning => Started && !Stopped;
    public ChannelReader<float[]> Reader => _channel.Reader;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_throwOnStart) throw new InvalidOperationException($"fake {_tag} failed to start");
        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stopped = true;
        _channel.Writer.TryComplete();
        _order?.Add($"{_tag}-stop");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _order?.Add(_tag);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A no-op <see cref="IAsyncDisposable"/> that records its dispose (and, optionally, one extra
/// action — used to model an engine's trailing-segment write during <c>DisposeAsync</c>) into a shared
/// ordering list.</summary>
internal sealed class RecordingDisposable : IAsyncDisposable
{
    private readonly List<string>? _order;
    private readonly string _tag;
    private readonly Func<Task>? _onDispose;

    public RecordingDisposable(List<string>? order, string tag, Func<Task>? onDispose = null)
    {
        _order = order;
        _tag = tag;
        _onDispose = onDispose;
    }

    public bool Disposed { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (_onDispose is not null) await _onDispose().ConfigureAwait(false);
        Disposed = true;
        _order?.Add(_tag);
    }
}

/// <summary>Scripted <see cref="ITranscriptionEngine"/> — never actually used by
/// <c>DirectTranscriptionServiceTests</c> (the engine-service factory seam bypasses real transcription
/// entirely), but required to satisfy the create-transcription seam's return shape. Optionally records
/// its own dispose into a shared ordering list (for the "diarizer disposed last" invariant).</summary>
internal sealed class FakeTranscriptionEngine : ITranscriptionEngine
{
    private readonly List<string>? _order;
    private readonly string _tag;

    public FakeTranscriptionEngine(List<string>? order = null, string tag = "transcription-engine")
    {
        _order = order;
        _tag = tag;
    }

    public bool Disposed { get; private set; }

    public Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
        => Task.FromResult(string.Empty);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _order?.Add(_tag);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Scripted <see cref="ISpeakerIdentificationService"/> double. <see cref="SpeakersReassigned"/>
/// has empty add/remove accessors (dodges CS0067) and can never fire — mirroring the real manual
/// implementation's documented safety property. Optionally records its own dispose into a shared
/// ordering list (for the "diarizer disposed last" invariant).</summary>
internal sealed class FakeSpeakerIdentificationService : ISpeakerIdentificationService
{
    private readonly List<string>? _order;
    private readonly string _tag;
    private long _nextSegmentId;

    public FakeSpeakerIdentificationService(List<string>? order = null, string tag = "speaker-id")
    {
        _order = order;
        _tag = tag;
    }

    public bool Disposed { get; private set; }
    public List<(string OldLabel, string NewLabel)> Renames { get; } = new();

    /// <summary>Set false to model the real diarizer refusing a rename (unknown old label, or its
    /// display-label collision guard).</summary>
    public bool RenameSucceeds { get; set; } = true;

    public event EventHandler<string>? SpeakerRegistered;
    public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned { add { } remove { } }

    public string IdentifyOrRegister(float[] segmentSamples, int sampleRate) => "Speaker 1";

    public (string Label, float[] Embedding) IdentifyOrRegisterWithEmbedding(float[] segmentSamples, int sampleRate)
        => ("Speaker 1", Array.Empty<float>());

    public SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate)
        => new(System.Threading.Interlocked.Increment(ref _nextSegmentId), "Speaker 1");

    public bool Rename(string oldLabel, string newLabel)
    {
        if (string.IsNullOrWhiteSpace(newLabel)) return false;
        if (!RenameSucceeds) return false;
        Renames.Add((oldLabel, newLabel));
        return true;
    }

    public void Reset() { }

    /// <summary>Test hook: fire <see cref="SpeakerRegistered"/> as the real diarizer would.</summary>
    public void RaiseSpeakerRegistered(string label) => SpeakerRegistered?.Invoke(this, label);

    public void Dispose()
    {
        Disposed = true;
        _order?.Add(_tag);
    }
}
