namespace Pia.Services.LiveTranscription;

/// <summary>
/// Tuning knobs for <see cref="AdaptiveSpeakerIdentificationService"/>. Every default is the shipping
/// value, so an omitted options object is the shipping behaviour; the diarization bench overrides them
/// to score one setting against another on a recording with a known answer key.
/// </summary>
internal sealed record AdaptiveSpeakerOptions
{
    /// <summary>Holds the instant match here instead of deriving it from each pass's cut; the derivation
    /// sat on its own clamp floor, above every threshold that scored better.</summary>
    public float? FixedMatchSimilarity { get; init; } = 0.30f;
    public float InitialMatchSimilarity { get; init; } = AdaptiveSpeakerIdentificationService.InitialMatchSimilarity;
    public float MatchSimilarityMin { get; init; } = AdaptiveSpeakerIdentificationService.MatchSimilarityMin;
    public float MatchSimilarityMax { get; init; } = AdaptiveSpeakerIdentificationService.MatchSimilarityMax;
    public float MinClusterSegmentSeconds { get; init; } = AdaptiveSpeakerIdentificationService.MinClusterSegmentSeconds;
    public int WarmupSegments { get; init; } = AdaptiveSpeakerIdentificationService.WarmupSegments;
    public int PassSegmentStride { get; init; } = AdaptiveSpeakerIdentificationService.PassSegmentStride;

    public static AdaptiveSpeakerOptions Default { get; } = new();
}
