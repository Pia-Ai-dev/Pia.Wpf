using Pia.Models;
using Pia.Services;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Pins the model-download URLs to their exact live spelling. Every one of them is fetched lazily on
/// first use behind a catch-and-log, so a plausible-looking "correction" is a 404 nobody notices until
/// a user picks that model. Offline by construction — it composes strings, it never dials out.
/// </summary>
public class ModelDownloadUrlTests
{
    private const string SherpaAsr =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models";

    [Theory]
    [InlineData(WhisperModelSize.Tiny, "tiny")]
    [InlineData(WhisperModelSize.Base, "base")]
    [InlineData(WhisperModelSize.Small, "small")]
    [InlineData(WhisperModelSize.Medium, "medium")]
    // "large-v3-turbo" is what sherpa calls the model and what the UI label says, but the released
    // asset is named "turbo" — the spelled-out form 404s.
    [InlineData(WhisperModelSize.Large, "turbo")]
    public void WhisperBundleUrl_matches_the_released_asset_name(WhisperModelSize size, string slug)
    {
        Assert.Equal($"{SherpaAsr}/sherpa-onnx-whisper-{slug}.tar.bz2",
            LiveTranscriptionModels.WhisperBundleUrl(size));
    }

    [Fact]
    public void ParakeetBundleUrl_is_pinned()
    {
        Assert.Equal($"{SherpaAsr}/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2",
            LiveTranscriptionModels.ParakeetBundleUrl);
    }

    /// <summary>The "recongition" misspelling is the real release tag; the corrected spelling 404s.</summary>
    [Fact]
    public void SpeakerEmbeddingUrl_keeps_the_misspelled_release_tag()
    {
        Assert.Equal(
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/"
            + "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
            LiveTranscriptionModels.SpeakerEmbeddingUrl);
    }

    [Fact]
    public void SileroVadUrl_is_pinned()
    {
        Assert.Equal(
            "https://github.com/snakers4/silero-vad/raw/v6.2.1/src/silero_vad/data/silero_vad.onnx",
            LiveTranscriptionModels.SileroVadUrl);
    }

    [Fact]
    public void SileroVadUrl_names_a_tag_not_a_branch()
    {
        Assert.DoesNotContain("/raw/master/", LiveTranscriptionModels.SileroVadUrl);
    }

    [Theory]
    [InlineData(EmbeddingService.ModelUrl, "onnx/model.onnx")]
    [InlineData(EmbeddingService.TokenizerUrl, "tokenizer.json")]
    [InlineData(EmbeddingService.SentencePieceUrl, "sentencepiece.bpe.model")]
    public void EmbeddingUrls_are_pinned(string url, string file)
    {
        Assert.Equal(
            "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/"
            + "resolve/main/" + file,
            url);
    }
}
