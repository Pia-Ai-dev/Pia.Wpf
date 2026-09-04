namespace Pia.Services.LiveTranscription;

/// <summary>
/// An engine that can emit a running hypothesis before a segment ends. Feature-detected: an engine
/// that does not implement this still works, it just produces nothing until the segment closes.
/// </summary>
public interface IStreamingTranscriptionEngine
{
    IStreamingSession BeginSession();
}

/// <summary>
/// One decoder state. Each capture source needs its own, or two speakers interleave into one
/// hypothesis.
/// </summary>
public interface IStreamingSession : IDisposable
{
    void Feed(float[] samples16kMono);

    string CurrentPartial { get; }

    void Reset();
}

/// <summary>
/// Raises a hypothesis only when it differs from the last one. The recognizer is polled every audio
/// frame and usually returns the same text, so the raw stream would be mostly redundant.
/// </summary>
internal sealed class PartialHypothesisRelay(Action<string> onChanged)
{
    private string _last = string.Empty;

    public void Offer(string partial)
    {
        var next = partial ?? string.Empty;
        if (string.Equals(next, _last, StringComparison.Ordinal)) return;
        _last = next;
        onChanged(next);
    }

    public void Clear() => Offer(string.Empty);
}
