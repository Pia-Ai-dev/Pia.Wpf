using System.IO;
using Microsoft.Mxc.Sdk.V1;

namespace Pia.Tests.TestInfrastructure;

/// <summary>Decides once per run whether this machine can create an MXC container, so the tests that need one
/// skip instead of failing on a host that cannot.</summary>
internal static class MxcTestHost
{
    private static readonly Lazy<string?> Verdict = new(Probe);

    public static bool CanSpawn => Verdict.Value is null;

    /// <summary>Why no container can be created here; null when one can.</summary>
    public static string? Reason => Verdict.Value;

    public static string NativeDirectory => Path.Combine(AppContext.BaseDirectory, "mxc-native");

    private static string? Probe()
    {
        if (!File.Exists(Path.Combine(NativeDirectory, "mxc_ffi.dll")))
            return "mxc_ffi.dll was not copied to the test output";

        Environment.SetEnvironmentVariable("MXC_FFI_DIR", NativeDirectory);
        try
        {
            var support = MxcPlatform.GetPlatformSupport();
            if (!support.IsSupported)
                return $"MXC is not supported here: {support.Reason}";

            var request = new ContainerRequest("\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\" /c exit 0")
            {
                ContainerName = "pia-mcp-test-probe",
                Ui = new UiPolicy { Disable = false },
                InheritDefaultEnvironment = true
            };
            using var process = MxcContainer.SpawnAsync(request).GetAwaiter().GetResult();
            _ = process.StandardOutput;
            process.WaitAsync().GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return $"MXC cannot create a container here: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
