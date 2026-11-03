using System.IO;
using Xunit;

namespace Pia.Tests.Services.Assets;

// onnxruntime.dll imports the VC++ runtime, which Windows does not ship; a package without it loses embeddings and speech.
public class VcRuntimeStagingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string Read(params string[] relative) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. relative]));

    [Fact]
    public void Release_workflow_stages_vc_runtime_after_publish_and_before_signing()
    {
        var workflow = Read(".github", "workflows", "build-and-release.yml");

        var publish = IndexOf(workflow, "dotnet publish src/Pia.Wpf/Pia.Wpf.csproj");
        var stage = IndexOf(workflow, "scripts/Copy-VcRuntime.ps1 -Destination publish");
        var sign = IndexOf(workflow, "Sign published Pia.Wpf.exe");
        var pack = IndexOf(workflow, "vpk pack");

        Assert.True(stage > publish, "the runtime must be staged after dotnet publish");
        Assert.True(stage < sign, "the runtime must be staged before the exe is signed");
        Assert.True(stage < pack, "the runtime must be staged before the first vpk pack");
    }

    [Fact]
    public void Local_velopack_build_stages_vc_runtime_before_packing()
    {
        var script = Read("scripts", "build-velopack.ps1");

        var publish = IndexOf(script, "dotnet publish");
        var stage = IndexOf(script, "Copy-VcRuntime.ps1");
        var pack = IndexOf(script, "vpk pack");

        Assert.True(stage > publish, "the runtime must be staged after dotnet publish");
        Assert.True(stage < pack, "the runtime must be staged before vpk pack");
    }

    private static int IndexOf(string text, string value)
    {
        var index = text.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{value}' not found");
        return index;
    }
}
