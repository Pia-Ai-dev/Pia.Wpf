using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Tts;

/// <summary>
/// The migration deletes from the user's profile, so the marker check is the whole safety story: a
/// folder that is not the old piper.exe layout has to survive untouched.
/// </summary>
public sealed class LegacyPiperTreeRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PiaLegacyPiper_" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempPath.Remove(_root);

    [Fact]
    public void Removes_the_tree_when_the_piper_executable_marks_it()
    {
        var legacy = Path.Combine(_root, "Piper");
        Directory.CreateDirectory(Path.Combine(legacy, "piper"));
        Directory.CreateDirectory(Path.Combine(legacy, "models", "de_DE-eva_k-x_low"));
        File.WriteAllText(Path.Combine(legacy, "piper", "piper.exe"), "MZ");
        File.WriteAllText(Path.Combine(legacy, "models", "de_DE-eva_k-x_low", "de_DE-eva_k-x_low.onnx"), "onnx");

        Assert.True(TtsService.TryRemoveLegacyPiperTree(legacy, NullLogger.Instance));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void Leaves_a_directory_that_is_not_the_old_layout_alone()
    {
        var notLegacy = Path.Combine(_root, "Piper");
        Directory.CreateDirectory(notLegacy);
        var bystander = Path.Combine(notLegacy, "something-else.txt");
        File.WriteAllText(bystander, "keep me");

        Assert.False(TtsService.TryRemoveLegacyPiperTree(notLegacy, NullLogger.Instance));
        Assert.True(File.Exists(bystander));
    }

    [Fact]
    public void Is_a_no_op_when_the_tree_was_already_removed()
    {
        Assert.False(TtsService.TryRemoveLegacyPiperTree(Path.Combine(_root, "absent"), NullLogger.Instance));
    }
}
