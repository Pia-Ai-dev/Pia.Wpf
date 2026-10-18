using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Mxc.Sdk.V1;
using Pia.Services.Plugins.Sandbox;

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

    /// <summary>Stands in for the provisioner: the native copy in the test output counts as verified.</summary>
    public static IMxcRuntime Runtime { get; } = new TestRuntime();

    private sealed class TestRuntime : IMxcRuntime
    {
        public bool TryLoad(out string? reason)
        {
            reason = Reason;
            if (!CanSpawn)
                return false;

            Environment.SetEnvironmentVariable("MXC_FFI_DIR", NativeDirectory);
            return true;
        }
    }

    private static string? Probe()
    {
        if (!File.Exists(Path.Combine(NativeDirectory, "mxc_ffi.dll")))
            return "mxc_ffi.dll was not copied to the test output";

        Environment.SetEnvironmentVariable("MXC_FFI_DIR", NativeDirectory);

        // Per process, so two test runs on one host never spawn the same container name at once.
        var prefix = "test-probe-" + Environment.ProcessId;
        try
        {
            var support = MxcPlatform.GetPlatformSupport();
            if (!support.IsSupported)
                return $"MXC is not supported here: {support.Reason}";

            var request = new ContainerRequest("\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\" /c exit 0")
            {
                ContainerName = SandboxLaunchSpecBuilder.ContainerName(prefix, isProbe: false),
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
        finally
        {
            new AppContainerProfileService(NullLogger<AppContainerProfileService>.Instance).DeleteServerProfiles(prefix);
        }
    }
}
