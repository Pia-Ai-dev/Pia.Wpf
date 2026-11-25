using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Pia.Services;
using Pia.Services.Exceptions;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public class EmbeddingServiceRuntimeFailureTests : IDisposable
{
    private readonly string _modelDirectory;
    private int _factoryCalls;

    public EmbeddingServiceRuntimeFailureTests()
    {
        _modelDirectory = Path.Combine(Path.GetTempPath(), $"pia-embed-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_modelDirectory);
        foreach (var name in new[]
                 {
                     EmbeddingService.ModelFileName,
                     EmbeddingService.TokenizerFileName,
                     EmbeddingService.SentencePieceFileName,
                 })
        {
            File.WriteAllBytes(Path.Combine(_modelDirectory, name), []);
        }
    }

    public void Dispose() => TempPath.Remove(_modelDirectory);

    // What a machine without the VC++ runtime produces: ORT's static NativeMethods ctor fails to load onnxruntime.dll.
    private InferenceSession ThrowingFactory(string modelPath)
    {
        _factoryCalls++;
        throw new TypeInitializationException(
            "Microsoft.ML.OnnxRuntime.NativeMethods", new DllNotFoundException());
    }

    private EmbeddingService NewService() =>
        new(NullLogger<EmbeddingService>.Instance, new StubAssetDownloader(), _modelDirectory, ThrowingFactory);

    [Fact]
    public async Task Runtime_load_failure_surfaces_as_EmbeddingUnavailable()
    {
        using var svc = NewService();

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() =>
            svc.GenerateEmbeddingAsync("hello", TestContext.Current.CancellationToken));

        Assert.IsType<TypeInitializationException>(ex.InnerException);
    }

    [Fact]
    public async Task Runtime_load_failure_is_not_retried()
    {
        using var svc = NewService();

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() =>
            svc.GenerateEmbeddingAsync("first", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() =>
            svc.GenerateEmbeddingAsync("second", TestContext.Current.CancellationToken));

        Assert.Equal(1, _factoryCalls);
    }

    [Fact]
    public void NativeLoadProbe_reports_a_missing_file()
    {
        var absent = Path.Combine(_modelDirectory, "absent.dll");

        Assert.Equal(126, NativeLoadProbe.GetLoadError(absent));
    }
}
