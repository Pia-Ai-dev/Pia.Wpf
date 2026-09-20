using System.IO;
using Pia.Paths;
using Pia.Services.Tts;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Tts;

/// <summary>
/// Pins the voice bundle URLs and the completeness gate. Offline by construction — it composes strings
/// and writes fixture files under the temp root, never into the profile the app downloads to.
/// </summary>
public sealed class TtsVoiceCatalogTests : IDisposable
{
    private const string SherpaTts =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models";

    private const string Key = "en_US-amy-medium";

    private readonly string _voiceDir = Path.Combine(
        Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"), "vits-piper-" + Key);

    public void Dispose() => TempPath.Remove(Path.GetDirectoryName(_voiceDir));

    [Fact]
    public void BundleUrl_matches_the_released_asset_name()
    {
        Assert.Equal($"{SherpaTts}/vits-piper-de_DE-thorsten-medium.tar.bz2",
            TtsVoiceCatalog.BundleUrl("de_DE-thorsten-medium"));
    }

    [Fact]
    public void Every_curated_voice_gets_a_bundle_url_under_the_tts_release_tag()
    {
        foreach (var voice in TtsVoiceCatalog.Curated)
        {
            var url = TtsVoiceCatalog.BundleUrl(voice.Key);
            Assert.StartsWith($"{SherpaTts}/vits-piper-", url, StringComparison.Ordinal);
            Assert.EndsWith(".tar.bz2", url, StringComparison.Ordinal);
            Assert.Contains(voice.Key, url, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Curated_keys_are_unique()
    {
        var keys = TtsVoiceCatalog.Curated.Select(v => v.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_default_voice_is_one_of_the_curated_ones()
    {
        Assert.Contains(TtsVoiceCatalog.DefaultVoiceKey, TtsVoiceCatalog.Curated.Select(v => v.Key));
    }

    /// <summary>
    /// Their training corpora forbid commercial use — Blizzard 2013 Lessac is research-only and
    /// RyanSpeech is CC BY-NC-SA 4.0 — so offering either from the picker would ship the breach.
    /// </summary>
    [Theory]
    [InlineData("en_US-lessac-medium")]
    [InlineData("en_US-ryan-medium")]
    public void A_retired_voice_is_never_offered(string key)
    {
        Assert.True(TtsVoiceCatalog.IsRetired(key));
        Assert.DoesNotContain(key, TtsVoiceCatalog.Curated.Select(v => v.Key));
    }

    [Fact]
    public void The_default_voice_is_not_retired()
    {
        Assert.False(TtsVoiceCatalog.IsRetired(TtsVoiceCatalog.DefaultVoiceKey));
    }

    [Fact]
    public void IsRetired_is_false_for_a_curated_voice_and_for_no_selection()
    {
        Assert.All(TtsVoiceCatalog.Curated, v => Assert.False(TtsVoiceCatalog.IsRetired(v.Key), v.Key));
        Assert.False(TtsVoiceCatalog.IsRetired(null));
        Assert.False(TtsVoiceCatalog.IsRetired(string.Empty));
    }

    /// <summary>The picker shows this as the download size, so a zero would read as "free".</summary>
    [Fact]
    public void Every_curated_voice_carries_a_measured_bundle_size()
    {
        Assert.All(TtsVoiceCatalog.Curated, v => Assert.True(v.BundleBytes > 1_000_000, v.Key));
    }

    [Fact]
    public void VoiceDirectory_is_named_after_the_bundle_and_sits_under_the_voices_root()
    {
        var dir = TtsVoiceCatalog.VoiceDirectory(Key);

        Assert.Equal("vits-piper-" + Key, Path.GetFileName(dir));
        Assert.Equal(TtsVoiceCatalog.VoicesDirectory, Path.GetDirectoryName(dir));
        Assert.StartsWith(PiaPaths.TtsDirectory, dir, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void TryResolveIn_refuses_a_voice_missing_any_of_the_three_artifacts(
        bool model, bool tokens, bool phontab)
    {
        WriteVoice(model, tokens, phontab);

        Assert.Null(TtsVoiceCatalog.TryResolveIn(_voiceDir, Key));
    }

    [Fact]
    public void TryResolveIn_returns_all_three_paths_for_a_complete_voice()
    {
        WriteVoice(model: true, tokens: true, phontab: true);

        var files = TtsVoiceCatalog.TryResolveIn(_voiceDir, Key);

        Assert.NotNull(files);
        Assert.Equal(Path.Combine(_voiceDir, Key + ".onnx"), files.ModelPath);
        Assert.Equal(Path.Combine(_voiceDir, "tokens.txt"), files.TokensPath);
        Assert.Equal(Path.Combine(_voiceDir, "espeak-ng-data"), files.DataDirectory);
    }

    /// <summary>
    /// A "*.onnx" pattern also matches the sidecar "<c>.onnx.json</c>" through its 8.3 short name, and
    /// handing that to the engine is an access violation rather than an error. The model is under a
    /// name that does not match the key, so resolution has to go through the glob to find it.
    /// </summary>
    [Fact]
    public void TryResolveIn_never_picks_the_json_sidecar_as_the_model()
    {
        WriteVoice(model: false, tokens: true, phontab: true);
        File.WriteAllText(Path.Combine(_voiceDir, Key + ".onnx.json"), "{}");
        File.WriteAllText(Path.Combine(_voiceDir, "model.onnx"), "onnx");

        var files = TtsVoiceCatalog.TryResolveIn(_voiceDir, Key);

        Assert.NotNull(files);
        Assert.Equal(Path.Combine(_voiceDir, "model.onnx"), files.ModelPath);
    }

    [Fact]
    public void TryResolveIn_is_null_when_nothing_was_ever_downloaded()
    {
        Assert.Null(TtsVoiceCatalog.TryResolveIn(_voiceDir, Key));
    }

    private void WriteVoice(bool model, bool tokens, bool phontab)
    {
        Directory.CreateDirectory(_voiceDir);
        if (model) File.WriteAllText(Path.Combine(_voiceDir, Key + ".onnx"), "onnx");
        if (tokens) File.WriteAllText(Path.Combine(_voiceDir, "tokens.txt"), "a 1");

        var dataDir = Path.Combine(_voiceDir, "espeak-ng-data");
        Directory.CreateDirectory(dataDir);
        if (phontab) File.WriteAllText(Path.Combine(dataDir, "phontab"), "tab");
    }
}
