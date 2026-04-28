# Cross-talk detection — Phase 3 limitation

**Status:** Documented limitation. See spec §3.10.

The Phase 3 consent pipeline ships **without** true cross-talk detection. The
`ICrossTalkResolver` and its `ConservativeCrossTalkResolver` implementation are wired
into DI and ready to be invoked, but there is no producer of multi-speaker VAD segments
in the current pipeline. Concretely:

* `SpeakerIdentificationService.IdentifyOrRegister` is built around
  sherpa-onnx's `SpeakerEmbeddingExtractor`, which returns a single embedding for the
  entire segment and therefore a single best-matching label. It cannot report
  "this segment contains speakers A and B".
* sherpa-onnx exposes an `OfflineSpeakerDiarization` API that performs segment-level
  diarization, but it operates on whole files (offline) and is incompatible with the
  per-segment streaming flow used by `LiveTranscriptionEngineService`. Adapting it
  would require either (a) buffering an entire segment and running offline diarization
  on it, with the latency cost that implies, or (b) integrating a separate streaming
  diarizer (e.g. a Pyannote-style ONNX model).

## What this means for the gate

The single-speaker `IConsentGate` correctly handles the common case (segment ⇒ one
label). In the cross-talk case, the embedding pool returns the *closest* centroid,
which is whichever speaker dominates the mix; the consent state of the quieter speaker
is silently ignored. This is a known false-negative on consent honouring under
overlap. It does not affect the single-speaker decision points (Strict refuses cloud
unconditionally, Strategy A pauses on every new label, blocklist is per-speaker).

## When to revisit

Lift this limitation either when:

1. sherpa-onnx ships a streaming multi-label diarizer compatible with our 16 kHz
   per-segment frame rate, **or**
2. We integrate a parallel streaming diarizer (e.g. Pyannote ONNX) and route its
   per-frame label set into the resolver.

At that point, replace the engine's call to `_consentGate.Evaluate(label)` with a
call to `ICrossTalkResolver.Resolve(activeLabels)` whenever the segment has more than
one active label.
