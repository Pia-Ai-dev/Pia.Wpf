using System.IO;
using System.Text;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Extraction goes through SharpCompress against a real GNU-tar archive, because the only thing worth
/// asserting here is that the library still decodes one — a hand-built or SharpCompress-written fixture
/// would pass a broken upgrade. Nothing downloads: the archive is the constant below.
/// </summary>
public sealed class BundleExtractionTests : IDisposable
{
    /// <summary>
    /// 283 bytes from <c>tar -cjf</c>, laid out the way a sherpa-onnx release is:
    /// <c>sherpa-onnx-whisper-tiny/{encoder.onnx,tokens.txt,sub/nested.bin}</c>.
    /// </summary>
    private const string BundleTarBz2Base64 =
        "QlpoOTFBWSZTWTT7IQwAATVfgcuQQAP/qh6BnGB+ad7gAAgEiDAA+NsIUMTQEYBMIaYAAmAMkKNNGIaZMmmE0aYmjQ0N" +
        "ARUhoCeiRpkAGmmIaNGmmnqY7uPCReZiFMkgae6YBwuRUz1dGecLTASKc5RTooStFIK7ZtuCfaTp6bN5SkU0Jum4W/pC" +
        "Dcykg4JABNWrVt2ZgTXX1JYaYRSBPDpo1jTEhFbyMqLK0IY8xChDNIYFA5iEzjkpegtoRUltttxaESmNymRxIg1YFwOI" +
        "LbCF4gjuGWWu5tLlBCzYQPYIck9rEilryyZJxmsGhOVTflT4SSb0sUs49n9DGSaEqynC4ZJXbG+JilKSHLBOtQP8XckU" +
        "4UJA0+yEMA==";

    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));

    public BundleExtractionTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string ExtractFixture()
    {
        var archive = Path.Combine(_tmpDir, "bundle.tar.bz2");
        File.WriteAllBytes(archive, Convert.FromBase64String(BundleTarBz2Base64));

        var target = Path.Combine(_tmpDir, "out");
        Directory.CreateDirectory(target);
        LiveTranscriptionModels.ExtractTarBz2(archive, target);
        return target;
    }

    [Fact]
    public void Strips_the_bundle_folder_so_models_land_flat()
    {
        var target = ExtractFixture();

        Assert.True(File.Exists(Path.Combine(target, "encoder.onnx")));
        Assert.True(File.Exists(Path.Combine(target, "tokens.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "sherpa-onnx-whisper-tiny")));
    }

    [Fact]
    public void Keeps_paths_below_the_stripped_folder()
    {
        var target = ExtractFixture();

        Assert.Equal("NESTED", File.ReadAllText(Path.Combine(target, "sub", "nested.bin"), Encoding.UTF8));
    }

    [Fact]
    public void Writes_every_entry_once_and_byte_exact()
    {
        var target = ExtractFixture();

        Assert.Equal(3, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("ONNX-ENCODER-BYTES", File.ReadAllText(Path.Combine(target, "encoder.onnx"), Encoding.UTF8));
        Assert.Equal("tok-a\ntok-b\n", File.ReadAllText(Path.Combine(target, "tokens.txt"), Encoding.UTF8));
    }
}
