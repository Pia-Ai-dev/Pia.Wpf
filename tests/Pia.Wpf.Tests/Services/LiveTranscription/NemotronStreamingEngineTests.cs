using System.IO;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Asserts on the config only — constructing the recognizer would need the 453 MiB bundle. The
/// decoding-method assertion is the important one: sherpa's streaming NeMo implementations call
/// exit(-1) on anything but greedy_search, which kills the process with no catchable exception.
/// </summary>
public class NemotronStreamingEngineTests
{
    private static string FakeModelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PiaTests_nemotron_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx" })
            File.WriteAllBytes(Path.Combine(dir, name), [0]);
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "<blk> 0\n");
        return dir;
    }

    [Fact]
    public void BuildConfig_uses_greedy_search_because_anything_else_kills_the_process()
    {
        var dir = FakeModelDir();
        try
        {
            Assert.Equal("greedy_search", NemotronStreamingEngine.BuildConfig(dir).DecodingMethod);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BuildConfig_leaves_endpointing_to_silero_vad()
    {
        var dir = FakeModelDir();
        try
        {
            Assert.Equal(0, NemotronStreamingEngine.BuildConfig(dir).EnableEndpoint);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BuildConfig_resolves_the_int8_transducer_files()
    {
        var dir = FakeModelDir();
        try
        {
            var config = NemotronStreamingEngine.BuildConfig(dir);
            Assert.EndsWith("encoder.int8.onnx", config.ModelConfig.Transducer.Encoder);
            Assert.EndsWith("decoder.int8.onnx", config.ModelConfig.Transducer.Decoder);
            Assert.EndsWith("joiner.int8.onnx", config.ModelConfig.Transducer.Joiner);
            Assert.EndsWith("tokens.txt", config.ModelConfig.Tokens);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BuildConfig_does_not_set_hotwords_which_this_model_cannot_honour()
    {
        var dir = FakeModelDir();
        try
        {
            Assert.True(string.IsNullOrEmpty(NemotronStreamingEngine.BuildConfig(dir).HotwordsFile));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Measured against the bundle's own de.wav: unpadded, a cold decode returns "hat ein Ende nur
    /// die Wurst hat" — it loses the words before its first full chunk and never flushes the last
    /// partial one. One chunk of silence at each end returns "Alles hat ein Ende, nur die Wurst hat
    /// zwei", punctuation included. Two chunks buy nothing more.
    /// </summary>
    [Fact]
    public void PadForColdDecode_brackets_the_segment_with_one_chunk_of_silence()
    {
        const int chunk = 16000 * 560 / 1000;
        var speech = new float[1000];
        Array.Fill(speech, 0.5f);

        var padded = NemotronStreamingEngine.PadForColdDecode(speech);

        Assert.Equal(chunk + speech.Length + chunk, padded.Length);
        Assert.All(padded[..chunk], v => Assert.Equal(0f, v));
        Assert.All(padded[chunk..(chunk + speech.Length)], v => Assert.Equal(0.5f, v));
        Assert.All(padded[(chunk + speech.Length)..], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void PadForColdDecode_leaves_the_callers_buffer_alone()
    {
        var speech = new float[10];
        Array.Fill(speech, 0.25f);

        NemotronStreamingEngine.PadForColdDecode(speech);

        Assert.All(speech, v => Assert.Equal(0.25f, v));
    }

    [Fact]
    public void BuildConfig_fails_loudly_when_a_transducer_file_is_missing()
    {
        var dir = FakeModelDir();
        File.Delete(Path.Combine(dir, "joiner.int8.onnx"));
        try
        {
            Assert.Throws<FileNotFoundException>(() => NemotronStreamingEngine.BuildConfig(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
