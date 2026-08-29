using Pia.Models;
using Pia.Services.LiveTranscription;

namespace Pia.Services.Assets;

/// <summary>
/// One downloadable artifact: where it lives on our mirror, and the upstream host to fall back to.
/// </summary>
/// <param name="MirrorKey">Relative to the mirror base. Must match the storage service's upload
/// charset <c>^[A-Za-z0-9._/-]+$</c>.</param>
public readonly record struct RuntimeAsset(string MirrorKey, string UpstreamUrl);

/// <summary>
/// Every asset the client fetches from a host we control a copy of. The keys are the contract with
/// <c>scripts/RuntimeAssetCatalogue.ps1</c>, which uploads them — <c>RuntimeAssetCatalogTests</c>
/// compares the two lists, because a key that disagrees is a mirror miss nobody sees.
/// </summary>
public static class RuntimeAssetCatalog
{
    public const string ModelsPrefix = "models/";
    public const string EmbeddingsPrefix = "embeddings/";

    public static RuntimeAsset SileroVad { get; } = new(
        ModelsPrefix + "silero_vad.onnx", LiveTranscriptionModels.SileroVadUrl);

    public static RuntimeAsset SpeakerEmbedding { get; } = new(
        ModelsPrefix + LiveTranscriptionModels.SpeakerEmbeddingFileName,
        LiveTranscriptionModels.SpeakerEmbeddingUrl);

    public static RuntimeAsset Whisper(WhisperModelSize size) => Bundle(
        LiveTranscriptionModels.WhisperBundleUrl(size));

    public static RuntimeAsset Parakeet { get; } = Bundle(LiveTranscriptionModels.ParakeetBundleUrl);

    // The ONNX is renamed on the way in — EmbeddingService looks for the model under the model's own
    // name, and "model.onnx" would collide with anything else mirrored under the same prefix.
    public static RuntimeAsset EmbeddingModel { get; } = new(
        EmbeddingsPrefix + EmbeddingService.ModelFileName, EmbeddingService.ModelUrl);

    public static RuntimeAsset EmbeddingTokenizer { get; } = new(
        EmbeddingsPrefix + EmbeddingService.TokenizerFileName, EmbeddingService.TokenizerUrl);

    public static RuntimeAsset EmbeddingSentencePiece { get; } = new(
        EmbeddingsPrefix + EmbeddingService.SentencePieceFileName, EmbeddingService.SentencePieceUrl);

    /// <summary>The whole set, in the order the publishing script walks it.</summary>
    public static IReadOnlyList<RuntimeAsset> All { get; } =
    [
        SileroVad,
        SpeakerEmbedding,
        EmbeddingModel,
        EmbeddingTokenizer,
        EmbeddingSentencePiece,
        Whisper(WhisperModelSize.Tiny),
        Whisper(WhisperModelSize.Base),
        Whisper(WhisperModelSize.Small),
        Whisper(WhisperModelSize.Medium),
        Whisper(WhisperModelSize.Large),
        Parakeet,
    ];

    // A sherpa bundle keeps its released archive name: mirroring the archive rather than the extracted
    // tree is what lets the client's extract step stay identical on both paths.
    private static RuntimeAsset Bundle(string upstreamUrl) =>
        new(ModelsPrefix + upstreamUrl[(upstreamUrl.LastIndexOf('/') + 1)..], upstreamUrl);
}
