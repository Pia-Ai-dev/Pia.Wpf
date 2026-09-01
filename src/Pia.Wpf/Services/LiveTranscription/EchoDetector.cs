using System.Text;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>What the detector decided about one microphone utterance.</summary>
public enum EchoVerdict
{
    /// <summary>The far end was not talking over it, or it says something else — the local user's own speech.</summary>
    Emit,

    /// <summary>The far end's words, re-recorded off the loudspeakers.</summary>
    Drop,

    /// <summary>Overlaps far-end speech whose text has not been recognised yet — ask again shortly.</summary>
    Hold,
}

/// <summary>A held utterance the detector has now made up its mind about.</summary>
/// <param name="Utterance">The microphone utterance that was held.</param>
/// <param name="Dropped">True when it resolved to echo; false when it should be emitted after all.</param>
public readonly record struct EchoDecision(TranscriptUtterance Utterance, bool Dropped);

/// <summary>
/// Recognises the local loudspeakers being re-recorded by the local microphone. Speaker attribution is
/// decided by which device the audio arrived on, so without this the far end lands under the local
/// user's name — past the consent gate, and under a null label that revoke can never reach.
///
/// <para>Two independent signals: the far end's voice-activity windows say a mic segment is
/// <em>suspect</em> (known immediately, before any recognition), and the far end's recognised text says
/// it is really an echo. A suspect whose counterpart text has not arrived yet is held, never blocked on —
/// see the note on <see cref="TakeDecided"/>.</para>
/// </summary>
public sealed class EchoDetector
{
    /// <summary>Below this share of a mic segment covered by far-end speech, nothing is suspected.</summary>
    public const double DefaultOverlapFraction = 0.6;

    /// <summary>Token overlap at which a suspect's text counts as the same sentence.</summary>
    public const double DefaultTextSimilarity = 0.6;

    /// <summary>Under this many words a coincidental match is likely, so only an exact one counts.</summary>
    private const int LooseMatchMinTokens = 4;

    /// <summary>Widens the far-end text window: the echo reaches the mic after a speaker-to-mic delay.</summary>
    private static readonly TimeSpan PairingSlack = TimeSpan.FromSeconds(1.5);

    /// <summary>A voice-activity window left open this long lost its "stopped" event; stop trusting it.</summary>
    private static readonly TimeSpan MaxOpenSpeechWindow = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _holdFor;
    private readonly TimeSpan _remoteMemory;
    private readonly double _overlapFraction;
    private readonly double _textSimilarity;

    private readonly List<(DateTimeOffset Start, DateTimeOffset? End)> _remoteSpeech = new();
    private readonly List<(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<string> Tokens)> _remoteText = new();
    private readonly List<(TranscriptUtterance Utterance, DateTimeOffset Deadline)> _held = new();
    private readonly object _lock = new();

    public EchoDetector(
        TimeSpan? holdFor = null,
        TimeSpan? remoteMemory = null,
        double overlapFraction = DefaultOverlapFraction,
        double textSimilarity = DefaultTextSimilarity)
    {
        _holdFor = holdFor ?? TimeSpan.FromSeconds(2);
        _remoteMemory = remoteMemory ?? TimeSpan.FromSeconds(30);
        _overlapFraction = overlapFraction;
        _textSimilarity = textSimilarity;
    }

    /// <summary>
    /// Records a far-end voice-activity transition. Fed from the loopback engine's VAD, so a suspect can
    /// be spotted before its text exists.
    /// </summary>
    public void NoteRemoteSpeaking(bool speaking, DateTimeOffset at)
    {
        lock (_lock)
        {
            if (speaking)
            {
                _remoteSpeech.Add((at, null));
            }
            else
            {
                for (var i = _remoteSpeech.Count - 1; i >= 0; i--)
                {
                    if (_remoteSpeech[i].End is null)
                    {
                        _remoteSpeech[i] = (_remoteSpeech[i].Start, at);
                        break;
                    }
                }
            }

            Prune(at);
        }
    }

    /// <summary>Records what the far end actually said, so a suspect can be confirmed or cleared.</summary>
    public void NoteRemoteUtterance(TranscriptUtterance remote)
    {
        if (string.IsNullOrWhiteSpace(remote.Text)) return;

        lock (_lock)
        {
            var start = remote.SpeechStart ?? remote.Timestamp;
            var end = remote.SpeechEnd ?? remote.Timestamp;
            _remoteText.Add((start, end, Tokenize(remote.Text)));
            Prune(end);
        }
    }

    /// <summary>Verdict for one microphone utterance. <see cref="EchoVerdict.Hold"/> means call
    /// <see cref="Hold"/> and re-ask via <see cref="TakeDecided"/>.</summary>
    public EchoVerdict Inspect(TranscriptUtterance mic, DateTimeOffset now)
    {
        lock (_lock)
        {
            return InspectCore(mic, now, deadline: null);
        }
    }

    /// <summary>Parks a suspect until its counterpart text arrives or the hold window expires.</summary>
    public void Hold(TranscriptUtterance mic, DateTimeOffset now)
    {
        lock (_lock)
        {
            _held.Add((mic, now + _holdFor));
        }
    }

    /// <summary>
    /// Every held utterance that can now be decided — matched, or held long enough. Returns fast and
    /// never waits: the caller is the single reader of the utterance channel, so blocking here would
    /// block the very far-end utterance being waited for.
    /// </summary>
    public IReadOnlyList<EchoDecision> TakeDecided(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_held.Count == 0) return Array.Empty<EchoDecision>();

            var decided = new List<EchoDecision>();
            for (var i = _held.Count - 1; i >= 0; i--)
            {
                var (utterance, deadline) = _held[i];
                var verdict = InspectCore(utterance, now, deadline);
                if (verdict == EchoVerdict.Hold) continue;

                decided.Add(new EchoDecision(utterance, verdict == EchoVerdict.Drop));
                _held.RemoveAt(i);
            }

            decided.Reverse();
            return decided;
        }
    }

    /// <summary>Releases everything still held, undecided, as emit. For shutdown.</summary>
    public IReadOnlyList<TranscriptUtterance> DrainHeld()
    {
        lock (_lock)
        {
            var remaining = _held.Select(h => h.Utterance).ToArray();
            _held.Clear();
            return remaining;
        }
    }

    private EchoVerdict InspectCore(TranscriptUtterance mic, DateTimeOffset now, DateTimeOffset? deadline)
    {
        // Undated audio cannot be placed against the far end's timeline; the local user keeps the benefit
        // of the doubt.
        if (mic.SpeechStart is not { } start || mic.SpeechEnd is not { } end || end <= start)
            return EchoVerdict.Emit;

        var covered = OverlapSeconds(start, end, now);
        var span = (end - start).TotalSeconds;
        if (span <= 0 || covered / span < _overlapFraction) return EchoVerdict.Emit;

        var micTokens = Tokenize(mic.Text);
        if (micTokens.Count == 0) return EchoVerdict.Emit;

        // How much of the suspect window the far end's recognised text already accounts for. Recognition
        // runs behind voice activity, so "covered by speech but not yet by text" is the wait case.
        var explained = 0d;
        foreach (var (remoteStart, remoteEnd, remoteTokens) in _remoteText)
        {
            var from = Max(remoteStart - PairingSlack, start);
            var to = Min(remoteEnd + PairingSlack, end);
            if (to <= from) continue;

            if (IsSameSentence(micTokens, remoteTokens)) return EchoVerdict.Drop;
            explained += (to - from).TotalSeconds;
        }

        // Everything the far end said here is known and none of it matches — the local user talking over
        // the top. Anything still missing is worth a short wait, then the benefit of the doubt.
        if (explained / span >= _overlapFraction) return EchoVerdict.Emit;
        return deadline is { } due && now >= due ? EchoVerdict.Emit : EchoVerdict.Hold;
    }

    private double OverlapSeconds(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
    {
        var total = 0d;
        foreach (var (windowStart, windowEnd) in _remoteSpeech)
        {
            // An open window means the far end is still talking right now.
            var closed = windowEnd ?? Min(now, windowStart + MaxOpenSpeechWindow);
            var from = windowStart > start ? windowStart : start;
            var to = closed < end ? closed : end;
            if (to > from) total += (to - from).TotalSeconds;
        }
        return total;
    }

    private bool IsSameSentence(IReadOnlyList<string> micTokens, IReadOnlyList<string> remoteTokens)
    {
        if (remoteTokens.Count == 0) return false;

        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in remoteTokens)
            remaining[token] = remaining.TryGetValue(token, out var n) ? n + 1 : 1;

        var shared = 0;
        foreach (var token in micTokens)
        {
            if (!remaining.TryGetValue(token, out var n) || n == 0) continue;
            remaining[token] = n - 1;
            shared++;
        }

        var coefficient = shared / (double)Math.Min(micTokens.Count, remoteTokens.Count);
        return micTokens.Count >= LooseMatchMinTokens
            ? coefficient >= _textSimilarity
            : coefficient >= 1d;
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - _remoteMemory;
        _remoteSpeech.RemoveAll(w => (w.End ?? w.Start + MaxOpenSpeechWindow) < cutoff);
        _remoteText.RemoveAll(t => t.End < cutoff);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>
    /// Lowercased word tokens. Recognisers punctuate and capitalise the same audio differently on each
    /// pass, so neither survives normalisation.
    /// </summary>
    internal static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
